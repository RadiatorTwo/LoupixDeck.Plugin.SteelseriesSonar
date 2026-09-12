using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Side-strip provider that shows the Sonar level of the three dials next to the strip as large
/// bars. Each bar follows the mix and channel its dial actually controls, read from the dial's
/// bound <c>SteelseriesSonar.*</c> command, so the strip needs no configuration of its own.
/// </summary>
internal sealed class SonarStripProvider(SonarContext context, IPluginSettings settings)
    : ISideStripProvider, ISegmentStripProvider
{
    /// <summary>Settings key: when true the strip renders as stacked horizontal bands instead of
    /// the default side-by-side vertical bars.</summary>
    internal const string HorizontalLayoutKey = "strip.horizontalLayout";

    /// <summary>Settings key: whether the strip carries the Sonar mode header.</summary>
    internal const string ShowModeHeaderKey = "strip.showModeHeader";

    public string Id => "steelseriessonar.mixer";

    public string Title => "Sonar Mixer Bars";

    // Live sessions, so a settings change can repaint the affected strips immediately.
    private readonly List<SonarStripSession> _sessions = [];

    public ISideStripSession CreateSession(SideStripContext stripContext)
    {
        SonarStripSession session = new(context, settings, stripContext, Forget);

        lock (_sessions)
        {
            _sessions.Add(session);
        }

        return session;
    }

    private void Forget(SonarStripSession session)
    {
        lock (_sessions)
        {
            _sessions.Remove(session);
        }
    }

    /// <summary>Repaints every live strip. Called after the settings were saved, because the
    /// layout and header switches change what a strip looks like.</summary>
    public void NotifyLayoutChanged()
    {
        SonarStripSession[] snapshot;

        lock (_sessions)
        {
            snapshot = _sessions.ToArray();
        }

        foreach (SonarStripSession session in snapshot)
        {
            session.RaiseChanged();
        }
    }
}

/// <summary>One live attachment of <see cref="SonarStripProvider"/> to a single strip.</summary>
internal sealed class SonarStripSession : ISideStripSession, ISegmentStripSession
{
    /// <summary>What one dial of this strip controls.</summary>
    private sealed class Bar
    {
        /// <summary>The explicit rotary label, which wins over the channel name when set.</summary>
        public string Label = string.Empty;

        /// <summary>Last-resort label for a dial that controls no Sonar channel.</summary>
        public int DialNumber;

        public SonarMix Mix;
        public SonarChannel? Channel;
    }

    private readonly SonarContext _context;
    private readonly IPluginSettings _settings;
    private readonly SideStripContext _strip;
    private readonly Action<SonarStripSession> _onDisposed;
    private readonly List<Bar> _bars = [];
    private readonly int _width;
    private readonly int _height;

    // Set once the host renders this session per segment, so tap hit-testing uses the stacked
    // axis regardless of the whole-strip layout setting.
    private volatile bool _segmentMode;

    public event EventHandler? StripChanged;

    public SonarStripSession(SonarContext context, IPluginSettings settings, SideStripContext strip,
        Action<SonarStripSession> onDisposed)
    {
        _context = context;
        _settings = settings;
        _strip = strip;
        _onDisposed = onDisposed;
        _width = strip.Width;
        _height = strip.Height;

        foreach (SideStripRotary rotary in strip.Rotaries)
        {
            SonarBinding? binding = SonarStripCommandParser.Parse(rotary);

            _bars.Add(new Bar
            {
                Label = rotary.Label?.Trim() ?? string.Empty,
                DialNumber = rotary.Index + 1,
                Mix = binding?.Mix ?? SonarMix.Classic,
                Channel = binding?.Channel
            });
        }

        _context.Monitor.StateChanged += OnStateChanged;
    }

    /// <summary>Forces a redraw of this strip.</summary>
    public void RaiseChanged() => StripChanged?.Invoke(this, EventArgs.Empty);

    private void OnStateChanged() => RaiseChanged();

    public bool RenderStrip(IRenderCanvas canvas)
    {
        // Nothing Sonar-related on this side, so let the host draw its own dial labels.
        if (_bars.Count == 0 || _bars.All(bar => bar.Channel == null))
        {
            return false;
        }

        bool horizontal = _settings.Get(SonarStripProvider.HorizontalLayoutKey, false);
        bool showHeader = _settings.Get(SonarStripProvider.ShowModeHeaderKey, true);

        SonarStripRenderer.Render(
            _bars.Select(View).ToList(),
            showHeader ? Header() : null,
            canvas,
            horizontal);

        return true;
    }

    /// <summary>
    /// Draws one segment in the host's segmented mode: the single band of the dial at
    /// <paramref name="rotaryIndex"/>, or false when that dial controls no Sonar channel so the
    /// host draws its normal label. A segment never carries the mode header — it belongs to the
    /// strip as a whole, not to one dial.
    /// </summary>
    public bool RenderSegment(int rotaryIndex, IRenderCanvas canvas)
    {
        _segmentMode = true;

        if (rotaryIndex < 0 || rotaryIndex >= _bars.Count)
        {
            return false;
        }

        Bar bar = _bars[rotaryIndex];
        if (bar.Channel == null)
        {
            return false;
        }

        SonarStripRenderer.RenderBand(View(bar), canvas);
        return true;
    }

    /// <summary>Reads everything the renderer needs about one dial, at render time so the bar
    /// always shows the freshest polled value.</summary>
    private SonarStripRenderer.BarView View(Bar bar)
    {
        if (bar.Channel == null)
        {
            return new SonarStripRenderer.BarView(DisplayName(bar), null, SonarBarStatus.Unbound, 0f, false, null);
        }

        SonarState state = _context.Monitor.State;
        SonarMix mix = bar.Mix;
        SonarLevel? level = _context.Monitor.GetLevel(mix, bar.Channel.Role);

        // Routing only exists for the two stream mixes, and never for master.
        bool? routed = SonarChannels.RedirectionId(mix) != null && bar.Channel.Role != SonarChannels.MasterRole
            ? _context.Monitor.IsRedirectionEnabled(mix, bar.Channel.Role)
            : null;

        return new SonarStripRenderer.BarView(
            DisplayName(bar),
            MixTag(mix),
            Status(mix, state, level),
            Volume: level == null ? 0f : (float)Math.Clamp(level.Volume, 0d, 1d),
            Muted: level?.Muted ?? false,
            Routed: routed);
    }

    /// <summary>
    /// What the bar can show right now. A mix that is not the one Sonar currently runs reports no
    /// level at all — telling the two apart matters, because an empty bar otherwise looks like a
    /// broken plugin when it really means the dial is bound to the other mode's mix.
    /// </summary>
    private static SonarBarStatus Status(SonarMix mix, SonarState state, SonarLevel? level)
    {
        if (!state.IsConnected)
        {
            return SonarBarStatus.Offline;
        }

        if (SonarChannels.RequiresStreamMode(mix) != state.IsStreamMode)
        {
            return SonarBarStatus.InactiveMix;
        }

        return level == null ? SonarBarStatus.Offline : SonarBarStatus.Live;
    }

    private SonarStripRenderer.HeaderView Header()
    {
        SonarState state = _context.Monitor.State;
        return new SonarStripRenderer.HeaderView(state.IsConnected, state.IsStreamMode, state.MonitoringEnabled);
    }

    /// <summary>
    /// The short mix marker on a bar. Every mix carries one, including classic: a bar shows which
    /// of the three mixes its dial drives, and that is exactly what an empty classic bar in stream
    /// mode needs to say.
    /// </summary>
    private static string MixTag(SonarMix mix) => mix switch
    {
        SonarMix.Streaming => "STR",
        SonarMix.Monitoring => "MON",
        _ => "CLA"
    };

    /// <summary>An explicit rotary label wins, otherwise the channel name, otherwise the dial
    /// number so an unbound dial still shows something.</summary>
    private static string DisplayName(Bar bar)
    {
        if (!string.IsNullOrWhiteSpace(bar.Label))
        {
            return bar.Label;
        }

        return bar.Channel?.DisplayName ?? $"Dial {bar.DialNumber}";
    }

    /// <summary>Tapping a bar mutes or unmutes its channel. The bars run left to right in the
    /// vertical layout and top to bottom in the horizontal one; in segmented mode they are always
    /// stacked, so the y axis applies regardless of the layout setting.</summary>
    public void OnStripTapped(int x, int y)
    {
        if (_bars.Count == 0)
        {
            return;
        }

        bool stacked = _segmentMode || _settings.Get(SonarStripProvider.HorizontalLayoutKey, false);
        int index = stacked
            ? Math.Clamp((int)(y / (_height / (float)_bars.Count)), 0, _bars.Count - 1)
            : Math.Clamp((int)(x / (_width / (float)_bars.Count)), 0, _bars.Count - 1);

        Bar bar = _bars[index];
        if (bar.Channel != null)
        {
            ToggleMute(bar.Mix, bar.Channel);
        }
    }

    /// <summary>
    /// Mutes or unmutes a channel off the caller's thread. The tap must return immediately, and
    /// the monitor's optimistic value makes the bar follow the press before the next poll.
    /// </summary>
    private void ToggleMute(SonarMix mix, SonarChannel channel)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                SonarLevel? level = _context.Monitor.GetLevel(mix, channel.Role);
                if (level == null)
                {
                    return;
                }

                bool target = !level.Muted;
                if (await _context.Client.SetMuteAsync(mix, channel.Role, target, CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    _context.Monitor.ApplyLocalLevel(mix, channel.Role, level.Volume, target);
                }
            }
            catch (Exception ex)
            {
                _context.Logger?.Error("Sonar: muting from the side strip failed", ex);
            }
        });
    }

    /// <summary>Swiping the strip pages this side's rotaries, as it does without a provider.</summary>
    public void OnStripSwiped(StripSwipeDirection direction)
    {
        if (direction == StripSwipeDirection.Up)
        {
            _strip.RequestNextPage();
        }
        else
        {
            _strip.RequestPreviousPage();
        }
    }

    public void Dispose()
    {
        _context.Monitor.StateChanged -= OnStateChanged;
        _bars.Clear();
        _onDisposed(this);
    }
}

/// <summary>The Sonar target a dial controls.</summary>
internal sealed record SonarBinding(SonarMix Mix, SonarChannel Channel);

/// <summary>
/// Reads the mix and channel a dial controls out of its bound commands. Every Sonar command is
/// named <c>SteelseriesSonar.{Mix}.{role}.{Action}</c>, optionally followed by parameters in
/// parentheses, so the binding can be recovered without any extra configuration.
/// </summary>
internal static class SonarStripCommandParser
{
    private const string Prefix = SonarCommandBase.GroupName + ".";

    public static SonarBinding? Parse(SideStripRotary rotary) =>
        FromCommand(rotary.RightCommand)
        ?? FromCommand(rotary.LeftCommand)
        ?? FromCommand(rotary.PressCommand);

    private static SonarBinding? FromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        int start = command.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        // Cut off any parameter list, e.g. "SteelseriesSonar.Classic.game.VolumeUp(5)".
        string name = command[(start + Prefix.Length)..];
        int parameters = name.IndexOf('(');
        if (parameters >= 0)
        {
            name = name[..parameters];
        }

        // Mix, role and action — the mode and preset commands carry fewer segments and are
        // deliberately not bars.
        string[] segments = name.Trim().Split('.');
        if (segments.Length < 3 || !Enum.TryParse(segments[0], ignoreCase: true, out SonarMix mix))
        {
            return null;
        }

        SonarChannel? channel = SonarChannels.All.FirstOrDefault(c =>
            string.Equals(c.Role, segments[1], StringComparison.OrdinalIgnoreCase));

        return channel == null ? null : new SonarBinding(mix, channel);
    }
}
