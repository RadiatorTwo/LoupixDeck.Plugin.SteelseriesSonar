using System.Net.Http;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Locates the Sonar HTTP API. SteelSeries writes the GG core's address into
/// <c>coreProps.json</c>; the core in turn reports the Sonar sub-app's own web server address,
/// whose port changes on every GG restart. Everything here therefore has to be re-runnable at
/// any time.
/// </summary>
public sealed class SonarDiscovery(IPluginLogger? logger) : IDisposable
{
    private readonly HttpClient _http = CreateLoopbackClient();

    /// <summary>
    /// Candidate locations of <c>coreProps.json</c>. Current installs use the GG folder; older
    /// ones keep the file under the Engine 3 folder.
    /// </summary>
    public static IReadOnlyList<string> CorePropsPaths { get; } = BuildCorePropsPaths();

    /// <summary>
    /// Resolves the Sonar API base address, e.g. <c>http://127.0.0.1:62381</c>. Returns null when
    /// GG is not installed, not running, or Sonar is disabled — all of which are normal states
    /// the plugin has to survive.
    /// </summary>
    public async Task<string?> DiscoverAsync(CancellationToken ct)
    {
        string? coreAddress = ReadCoreAddress();
        if (coreAddress == null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response =
                await _http.GetAsync($"https://{coreAddress}/subApps", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger?.Warn($"Sonar: GG core returned {(int)response.StatusCode} for /subApps.");
                return null;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            SubAppsResponse? subApps = await JsonSerializer
                .DeserializeAsync<SubAppsResponse>(body, SonarJson.Options, ct)
                .ConfigureAwait(false);

            if (subApps?.SubApps == null || !subApps.SubApps.TryGetValue("sonar", out SubApp? sonar))
            {
                logger?.Warn("Sonar: the GG core does not list a sonar sub-app.");
                return null;
            }

            if (!sonar.IsEnabled)
            {
                logger?.Info("Sonar: the sub-app is disabled in SteelSeries GG.");
                return null;
            }

            if (!sonar.IsRunning || !sonar.IsReady)
            {
                logger?.Info("Sonar: the sub-app is not running yet.");
                return null;
            }

            string? address = sonar.Metadata?.WebServerAddress;
            if (string.IsNullOrWhiteSpace(address) || address == "null")
            {
                logger?.Warn("Sonar: the sub-app reports no web server address.");
                return null;
            }

            return address.TrimEnd('/');
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Sonar: could not reach the GG core at {coreAddress} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Reads the GG core's host:port out of the first coreProps.json that exists.</summary>
    private string? ReadCoreAddress()
    {
        foreach (string path in CorePropsPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                SonarCoreProps? props =
                    JsonSerializer.Deserialize<SonarCoreProps>(File.ReadAllText(path), SonarJson.Options);

                if (!string.IsNullOrWhiteSpace(props?.GgEncryptedAddress))
                {
                    return props.GgEncryptedAddress;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"Sonar: could not read {path} ({ex.Message}).");
            }
        }

        logger?.Info("Sonar: no coreProps.json found — SteelSeries GG does not appear to be installed.");
        return null;
    }

    private static List<string> BuildCorePropsPaths()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return
        [
            Path.Combine(programData, "SteelSeries", "GG", "coreProps.json"),
            Path.Combine(programData, "SteelSeries", "SteelSeries Engine 3", "coreProps.json")
        ];
    }

    /// <summary>
    /// The GG core serves HTTPS with a self-signed certificate it regenerates at runtime, so the
    /// certificate check has to be waived. It is only ever waived for loopback addresses.
    /// </summary>
    private static HttpClient CreateLoopbackClient()
    {
        HttpClientHandler handler = new()
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || IsLoopback(request.RequestUri)
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static bool IsLoopback(Uri? uri) =>
        uri != null && System.Net.IPAddress.TryParse(uri.Host, out System.Net.IPAddress? ip) &&
        System.Net.IPAddress.IsLoopback(ip);

    public void Dispose() => _http.Dispose();
}

/// <summary>Shared serializer settings. Sonar answers in camelCase, our properties are PascalCase.</summary>
internal static class SonarJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
