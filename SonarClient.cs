using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Talks to the Sonar HTTP API. The API itself is plain HTTP on loopback; only the discovery step
/// that finds its port uses TLS. Because the port changes whenever SteelSeries GG restarts, every
/// request that fails at the transport level triggers one rediscovery and one retry.
/// </summary>
public sealed class SonarClient(IPluginLogger? logger) : IDisposable
{
    private readonly SonarDiscovery _discovery = new(logger);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);

    private string? _baseAddress;

    /// <summary>True once a Sonar instance has been located.</summary>
    public bool IsAvailable => _baseAddress != null;

    /// <summary>Resolves the base address if it is not known yet. Safe to call repeatedly.</summary>
    public async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_baseAddress != null)
        {
            return true;
        }

        return await RediscoverAsync(ct).ConfigureAwait(false);
    }

    // ---- Mode -----------------------------------------------------------------------------

    /// <summary>The active mode, either <c>classic</c> or <c>stream</c>.</summary>
    public Task<string?> GetModeAsync(CancellationToken ct) =>
        GetAsync<string>("/mode", ct);

    public Task<bool> SetModeAsync(bool streamMode, CancellationToken ct) =>
        PutAsync($"/mode/{(streamMode ? "stream" : "classic")}", ct);

    // ---- Volume and mute ------------------------------------------------------------------

    public Task<SonarVolumeSettings?> GetVolumeSettingsAsync(SonarMix mix, CancellationToken ct) =>
        GetAsync<SonarVolumeSettings>(SonarChannels.VolumeSettingsPath(mix), ct);

    public Task<bool> SetVolumeAsync(SonarMix mix, string role, double volume, CancellationToken ct) =>
        PutAsync($"{SonarChannels.VolumePath(mix, role)}/{FormatVolume(volume)}", ct);

    public Task<bool> SetMuteAsync(SonarMix mix, string role, bool muted, CancellationToken ct) =>
        PutAsync($"{SonarChannels.MutePath(mix, role)}/{FormatBool(muted)}", ct);

    // ---- Presets --------------------------------------------------------------------------

    public Task<List<SonarConfig>?> GetConfigsAsync(CancellationToken ct) =>
        GetAsync<List<SonarConfig>>("/configs", ct);

    public Task<List<SonarConfig>?> GetSelectedConfigsAsync(CancellationToken ct) =>
        GetAsync<List<SonarConfig>>("/configs/selected", ct);

    public Task<bool> SelectConfigAsync(string configId, CancellationToken ct) =>
        PutAsync($"/configs/{configId}/select", ct);

    // ---- Stream redirections ---------------------------------------------------------------

    public Task<List<SonarStreamRedirection>?> GetStreamRedirectionsAsync(CancellationToken ct) =>
        GetAsync<List<SonarStreamRedirection>>("/streamRedirections", ct);

    public Task<bool?> GetStreamMonitoringAsync(CancellationToken ct) =>
        GetValueAsync<bool>("/streamRedirections/isStreamMonitoringEnabled", ct);

    public Task<bool> SetStreamMonitoringAsync(bool enabled, CancellationToken ct) =>
        PutAsync($"/streamRedirections/isStreamMonitoringEnabled/{FormatBool(enabled)}", ct);

    /// <summary>Routes a channel into (or out of) the streaming or monitoring mix.</summary>
    public Task<bool> SetRedirectionEnabledAsync(SonarMix mix, string role, bool enabled, CancellationToken ct)
    {
        string? redirectionId = SonarChannels.RedirectionId(mix);
        if (redirectionId == null)
        {
            return Task.FromResult(false);
        }

        return PutAsync(
            $"/streamRedirections/{redirectionId}/redirections/{role}/isEnabled/{FormatBool(enabled)}", ct);
    }

    // ---- Output devices -------------------------------------------------------------------

    /// <summary>Physical playback devices, without Sonar's own virtual devices.</summary>
    public Task<List<SonarAudioDevice>?> GetOutputDevicesAsync(CancellationToken ct) =>
        GetAsync<List<SonarAudioDevice>>("/audioDevices?deviceDataFlow=render&removeSteelSeriesVAD=true", ct);

    /// <summary>
    /// Sends a stream mix to another physical device. The device id contains braces and dots and
    /// must be escaped. Returns the updated redirection, or null when the call failed.
    /// </summary>
    public async Task<SonarStreamRedirection?> SetRedirectionDeviceAsync(SonarMix mix, string deviceId, CancellationToken ct)
    {
        string? redirectionId = SonarChannels.RedirectionId(mix);
        if (redirectionId == null)
        {
            return null;
        }

        string path = $"/streamRedirections/{redirectionId}/deviceId/{Uri.EscapeDataString(deviceId)}";
        SonarResponse? response = await SendAsync(HttpMethod.Put, path, ct).ConfigureAwait(false);
        if (response is not { Success: true, Body.Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SonarStreamRedirection>(response.Body, SonarJson.Options);
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Sonar: unexpected response for PUT {path} ({ex.Message}).");
            return null;
        }
    }

    // ---- Formatting -----------------------------------------------------------------------

    /// <summary>
    /// Formats a volume for the URL. This must use the invariant culture: on a German system the
    /// default formatting produces <c>0,35</c>, which Sonar rejects.
    /// </summary>
    public static string FormatVolume(double volume) =>
        Math.Clamp(volume, 0d, 1d).ToString("0.0###", CultureInfo.InvariantCulture);

    private static string FormatBool(bool value) => value ? "true" : "false";

    // ---- Transport ------------------------------------------------------------------------

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        string? body = await ReadBodyAsync(path, ct).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, SonarJson.Options);
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Sonar: unexpected response for GET {path} ({ex.Message}).");
            return null;
        }
    }

    private async Task<T?> GetValueAsync<T>(string path, CancellationToken ct) where T : struct
    {
        string? body = await ReadBodyAsync(path, ct).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, SonarJson.Options);
        }
        catch (JsonException ex)
        {
            logger?.Warn($"Sonar: unexpected response for GET {path} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Runs a GET and hands back its body, or null when the call did not succeed.</summary>
    private async Task<string?> ReadBodyAsync(string path, CancellationToken ct)
    {
        SonarResponse? response = await SendAsync(HttpMethod.Get, path, ct).ConfigureAwait(false);
        return response is { Success: true, Body.Length: > 0 } ? response.Body : null;
    }

    private async Task<bool> PutAsync(string path, CancellationToken ct)
    {
        SonarResponse? response = await SendAsync(HttpMethod.Put, path, ct).ConfigureAwait(false);
        return response?.Success == true;
    }

    /// <summary>
    /// Sends one request, rediscovering the Sonar port once if the transport fails. Never throws:
    /// a null result means the call could not be completed at all.
    /// </summary>
    private async Task<SonarResponse?> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        SonarResponse? response = await TrySendAsync(method, path, ct).ConfigureAwait(false);
        if (response != null)
        {
            return response;
        }

        // The port changes on every GG restart, so a dead endpoint is expected rather than fatal.
        if (!await RediscoverAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return await TrySendAsync(method, path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One attempt against the current base address. Null signals a transport failure worth
    /// rediscovering for; a returned response means the server answered, successfully or not.
    /// </summary>
    private async Task<SonarResponse?> TrySendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        string? baseAddress = _baseAddress;
        if (baseAddress == null)
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(method, baseAddress + path);
            if (method == HttpMethod.Put)
            {
                // Sonar refuses PUTs without an explicit zero content length.
                request.Content = new ByteArrayContent([]);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A 404 means the feature is absent on this setup (for instance chat mix without a
                // supported headset), not that the connection is broken. Do not trigger rediscovery.
                logger?.Info($"Sonar: {path} is not available on this setup.");
                return new SonarResponse(false, string.Empty);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger?.Warn($"Sonar: {method} {path} returned {(int)response.StatusCode}.");
                return new SonarResponse(false, string.Empty);
            }

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new SonarResponse(true, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Info($"Sonar: {method} {path} failed ({ex.Message}).");
            _baseAddress = null;
            return null;
        }
    }

    private async Task<bool> RediscoverAsync(CancellationToken ct)
    {
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have rediscovered while we waited for the gate.
            if (_baseAddress != null)
            {
                return true;
            }

            _baseAddress = await _discovery.DiscoverAsync(ct).ConfigureAwait(false);
            if (_baseAddress != null)
            {
                logger?.Info($"Sonar: connected to {_baseAddress}.");
            }

            return _baseAddress != null;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _discovery.Dispose();
        _discoveryGate.Dispose();
    }
}

/// <summary>One answer from the Sonar server: whether it succeeded, and its raw body.</summary>
internal sealed record SonarResponse(bool Success, string Body);
