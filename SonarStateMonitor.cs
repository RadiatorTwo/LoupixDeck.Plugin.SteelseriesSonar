using System.Collections.Concurrent;
using System.Text;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// An immutable snapshot of everything the plugin shows on buttons. Replaced wholesale by the
/// poll loop so readers on the UI and command threads never see a half-updated state.
/// </summary>
public sealed class SonarState
{
    public static SonarState Empty { get; } = new();

    public bool IsConnected { get; init; }
    public bool IsStreamMode { get; init; }
    public SonarVolumeSettings? Volumes { get; init; }
    public bool MonitoringEnabled { get; init; }
    public IReadOnlyDictionary<string, SonarStreamRedirection> Redirections { get; init; } =
        new Dictionary<string, SonarStreamRedirection>();
    public IReadOnlyList<SonarConfig> Configs { get; init; } = [];

    /// <summary>The active preset per channel role.</summary>
    public IReadOnlyDictionary<string, SonarConfig> SelectedConfigs { get; init; } =
        new Dictionary<string, SonarConfig>();

    /// <summary>
    /// The level of one channel in one mix, or null when that mix is not the one Sonar is
    /// currently running. Both volume-settings responses carry a block for the inactive mode as
    /// well, but its values go stale — reporting them would show a wrong number on the button.
    /// </summary>
    public SonarLevel? LevelFor(SonarMix mix, string role)
    {
        if (!IsConnected || SonarChannels.RequiresStreamMode(mix) != IsStreamMode)
        {
            return null;
        }

        return Volumes?.Level(mix, role);
    }
}

/// <summary>
/// Keeps a current picture of Sonar's state and raises <see cref="StateChanged"/> whenever it
/// moves.
/// </summary>
/// <remarks>
/// Sonar exposes a WebSocket under <c>/sock</c> and the GG core exposes one too. Both accept the
/// upgrade, but neither pushed a single frame while volumes and mutes were being changed in the
/// Sonar UI, so neither is usable as a change feed. This class therefore polls. It is written so
/// the change feed can be swapped later without touching any command.
/// </remarks>
public sealed class SonarStateMonitor : IDisposable
{
    /// <summary>How long an optimistic local value wins over the last polled one.</summary>
    private static readonly TimeSpan OptimisticLifetime = TimeSpan.FromSeconds(2);

    /// <summary>Poll cycles between refreshes of the slow-moving data (presets, redirections).</summary>
    private const int SlowRefreshEveryNthTick = 8;

    private readonly SonarClient _client;
    private readonly IPluginLogger? _logger;
    private readonly Func<TimeSpan> _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, OptimisticLevel> _optimistic = new();

    private Task? _loop;
    private volatile SonarState _state = SonarState.Empty;
    private string _signature = string.Empty;

    public SonarStateMonitor(SonarClient client, IPluginLogger? logger, Func<TimeSpan> pollInterval)
    {
        _client = client;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    /// <summary>Raised on the poll thread whenever the observed state actually differs.</summary>
    public event Action? StateChanged;

    /// <summary>The most recent snapshot. Never null.</summary>
    public SonarState State => _state;

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// The current level of one channel in one mix, or null when Sonar reports nothing for it
    /// (for instance a stream-mode mix while Sonar runs in classic mode).
    /// </summary>
    public SonarLevel? GetLevel(SonarMix mix, string role)
    {
        SonarLevel? polled = _state.LevelFor(mix, role);
        if (polled == null)
        {
            // The mix is not the active one, or Sonar is unreachable. An optimistic value from
            // before a mode switch must not resurrect a channel that has nothing to show.
            return null;
        }

        if (_optimistic.TryGetValue(Key(mix, role), out OptimisticLevel? pending) && !pending.IsExpired)
        {
            return pending.Level;
        }

        return polled;
    }

    /// <summary>True when the given channel is routed into the given stream mix.</summary>
    public bool IsRedirectionEnabled(SonarMix mix, string role)
    {
        string? id = SonarChannels.RedirectionId(mix);
        return id != null &&
               _state.Redirections.TryGetValue(id, out SonarStreamRedirection? redirection) &&
               redirection.IsRoleEnabled(role);
    }

    /// <summary>
    /// Records a value we just wrote ourselves, so the button reflects the press immediately
    /// instead of lagging until the next poll.
    /// </summary>
    public void ApplyLocalLevel(SonarMix mix, string role, double volume, bool muted)
    {
        _optimistic[Key(mix, role)] = new OptimisticLevel(new SonarLevel { Volume = volume, Muted = muted });
        StateChanged?.Invoke();
    }

    /// <summary>Drops the optimistic value for a channel, e.g. after a failed write.</summary>
    public void DiscardLocalLevel(SonarMix mix, string role) => _optimistic.TryRemove(Key(mix, role), out _);

    private static string Key(SonarMix mix, string role) => $"{mix}/{role}";

    private async Task RunAsync(CancellationToken ct)
    {
        int tick = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(tick, ct).ConfigureAwait(false);
                tick++;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure, otherwise a hiccup freezes every button.
                _logger?.Warn($"Sonar: polling failed ({ex.Message}).");
            }

            try
            {
                await Task.Delay(_pollInterval(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync(int tick, CancellationToken ct)
    {
        if (!await _client.EnsureConnectedAsync(ct).ConfigureAwait(false))
        {
            Publish(SonarState.Empty);
            return;
        }

        string? mode = await _client.GetModeAsync(ct).ConfigureAwait(false);
        if (mode == null)
        {
            Publish(SonarState.Empty);
            return;
        }

        bool isStreamMode = mode == "stream";

        // Only the endpoint matching the active mode carries trustworthy values: the classic block
        // inside the streamer response goes stale while Sonar runs in stream mode, and vice versa.
        SonarMix activeMix = isStreamMode ? SonarMix.Streaming : SonarMix.Classic;
        SonarVolumeSettings? volumes =
            await _client.GetVolumeSettingsAsync(activeMix, ct).ConfigureAwait(false);

        SonarState previous = _state;
        bool refreshSlowData = tick % SlowRefreshEveryNthTick == 0 || !previous.IsConnected;

        bool monitoringEnabled = previous.MonitoringEnabled;
        IReadOnlyDictionary<string, SonarStreamRedirection> redirections = previous.Redirections;
        IReadOnlyList<SonarConfig> configs = previous.Configs;
        IReadOnlyDictionary<string, SonarConfig> selectedConfigs = previous.SelectedConfigs;

        if (refreshSlowData)
        {
            monitoringEnabled =
                await _client.GetStreamMonitoringAsync(ct).ConfigureAwait(false) ?? monitoringEnabled;

            List<SonarStreamRedirection>? fetchedRedirections =
                await _client.GetStreamRedirectionsAsync(ct).ConfigureAwait(false);
            if (fetchedRedirections != null)
            {
                redirections = fetchedRedirections
                    .Where(r => !string.IsNullOrEmpty(r.Id))
                    .ToDictionary(r => r.Id);
            }

            List<SonarConfig>? fetchedConfigs = await _client.GetConfigsAsync(ct).ConfigureAwait(false);
            if (fetchedConfigs != null)
            {
                configs = fetchedConfigs;
            }

            List<SonarConfig>? fetchedSelected =
                await _client.GetSelectedConfigsAsync(ct).ConfigureAwait(false);
            if (fetchedSelected != null)
            {
                selectedConfigs = fetchedSelected
                    .Where(c => !string.IsNullOrEmpty(c.VirtualAudioDevice))
                    .GroupBy(c => c.VirtualAudioDevice)
                    .ToDictionary(g => g.Key, g => g.First());
            }
        }

        Publish(new SonarState
        {
            IsConnected = true,
            IsStreamMode = isStreamMode,
            Volumes = volumes,
            MonitoringEnabled = monitoringEnabled,
            Redirections = redirections,
            Configs = configs,
            SelectedConfigs = selectedConfigs
        });
    }

    /// <summary>Swaps in a new snapshot and notifies listeners only when something moved.</summary>
    private void Publish(SonarState state)
    {
        string signature = BuildSignature(state);
        _state = state;

        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        ExpireOptimisticValues();
        StateChanged?.Invoke();
    }

    private void ExpireOptimisticValues()
    {
        foreach (KeyValuePair<string, OptimisticLevel> entry in _optimistic)
        {
            if (entry.Value.IsExpired)
            {
                _optimistic.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// A compact fingerprint of everything a button can display. Comparing it avoids repainting
    /// the whole deck on every poll when nothing changed.
    /// </summary>
    private static string BuildSignature(SonarState state)
    {
        StringBuilder builder = new();
        builder.Append(state.IsConnected).Append('|')
            .Append(state.IsStreamMode).Append('|')
            .Append(state.MonitoringEnabled).Append('|');

        foreach (SonarMix mix in SonarChannels.AllMixes)
        {
            foreach (SonarChannel channel in SonarChannels.All)
            {
                SonarLevel? level = state.LevelFor(mix, channel.Role);
                builder.Append(level == null ? "-" : $"{level.Volume:F3}{(level.Muted ? "m" : "")}")
                    .Append(',');
            }

            string? redirectionId = SonarChannels.RedirectionId(mix);
            if (redirectionId != null && state.Redirections.TryGetValue(redirectionId, out SonarStreamRedirection? redirection))
            {
                foreach (SonarChannel channel in SonarChannels.RedirectableChannels)
                {
                    builder.Append(redirection.IsRoleEnabled(channel.Role) ? '1' : '0');
                }
            }

            builder.Append('|');
        }

        foreach (KeyValuePair<string, SonarConfig> entry in state.SelectedConfigs.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append('=').Append(entry.Value.Id).Append(',');
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>A value we wrote ourselves, valid until the next poll confirms or replaces it.</summary>
    private sealed class OptimisticLevel(SonarLevel level)
    {
        private readonly DateTime _created = DateTime.UtcNow;

        public SonarLevel Level { get; } = level;

        public bool IsExpired => DateTime.UtcNow - _created > OptimisticLifetime;
    }
}
