using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Switches Sonar between classic and stream mode, or toggles between the two. Stream mode is what
/// splits every channel into the separate streaming and monitoring sliders.
/// </summary>
public sealed class SonarModeCommand : SonarCommandBase, IDisplayCommand, ISonarStatefulCommand
{
    /// <summary>State names are persisted in the button config and must never change.</summary>
    public const string ClassicState = "Classic";
    public const string StreamState = "Stream";

    /// <summary>Null means toggle; otherwise the mode this command switches to.</summary>
    private readonly bool? _targetStreamMode;

    public SonarModeCommand(SonarContext context, bool? targetStreamMode)
        : base(context)
    {
        _targetStreamMode = targetStreamMode;

        (string suffix, string display, string description) = targetStreamMode switch
        {
            true => ("ModeStream", "Sonar: Stream Mode", "Switch Sonar to stream mode"),
            false => ("ModeClassic", "Sonar: Classic Mode", "Switch Sonar to classic mode"),
            _ => ("ModeToggle", "Sonar: Toggle Mode", "Switch Sonar between classic and stream mode")
        };

        Descriptor = new CommandDescriptor
        {
            CommandName = $"SteelseriesSonar.{suffix}",
            DisplayName = display,
            Group = GroupName,
            Description = description,
            States =
            [
                new ButtonStateDescriptor { Name = ClassicState, Description = "Sonar runs in classic mode" },
                new ButtonStateDescriptor { Name = StreamState, Description = "Sonar runs in stream mode" }
            ]
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public string CommandName => Descriptor.CommandName;

    public string? CurrentStateName => Context.Monitor.State.IsConnected
        ? Context.Monitor.State.IsStreamMode ? StreamState : ClassicState
        : null;

    public string GetText(CommandContext ctx)
    {
        if (!Context.Monitor.State.IsConnected)
        {
            return "Sonar\noffline";
        }

        return Context.Monitor.State.IsStreamMode ? "Sonar\nStream" : "Sonar\nClassic";
    }

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        bool target = _targetStreamMode ?? !Context.Monitor.State.IsStreamMode;
        await Context.Client.SetModeAsync(target, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Turns stream monitoring on or off. With it off, Sonar mixes for the stream only and you hear
/// nothing of the monitoring path.
/// </summary>
public sealed class SonarStreamMonitoringToggleCommand(SonarContext context)
    : SonarCommandBase(context), IDisplayCommand, ISonarStatefulCommand
{
    public const string EnabledState = "Enabled";
    public const string DisabledState = "Disabled";

    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SteelseriesSonar.StreamMonitoringToggle",
        DisplayName = "Sonar: Stream Monitoring",
        Group = GroupName,
        Description = "Turn stream monitoring on or off",
        States =
        [
            new ButtonStateDescriptor { Name = DisabledState, Description = "Stream monitoring is off" },
            new ButtonStateDescriptor { Name = EnabledState, Description = "Stream monitoring is on" }
        ]
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public string CommandName => Descriptor.CommandName;

    public string? CurrentStateName => Context.Monitor.State.IsConnected
        ? Context.Monitor.State.MonitoringEnabled ? EnabledState : DisabledState
        : null;

    public string GetText(CommandContext ctx) =>
        $"Monitor\n{(Context.Monitor.State.MonitoringEnabled ? "ON" : "OFF")}";

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        bool target = !Context.Monitor.State.MonitoringEnabled;
        await Context.Client.SetStreamMonitoringAsync(target, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Sends a stream mix to the next physical playback device, wrapping around after the last one.
/// For the monitoring mix this is the device you hear your personal mix on.
/// </summary>
public sealed class SonarNextOutputDeviceCommand : SonarCommandBase, IDisplayCommand
{
    private readonly SonarMix _mix;

    public SonarNextOutputDeviceCommand(SonarContext context, SonarMix mix)
        : base(context)
    {
        _mix = mix;

        Descriptor = new CommandDescriptor
        {
            CommandName = $"SteelseriesSonar.{mix}.NextOutputDevice",
            DisplayName = $"Sonar: {SonarChannels.DisplayName(mix)} Next Output Device",
            Group = GroupName,
            Description = $"Send the {SonarChannels.DisplayName(mix)} mix to the next output device",
            HiddenFromMenu = true
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public string GetText(CommandContext ctx)
    {
        if (!Context.Monitor.State.IsConnected)
        {
            return "Sonar\noffline";
        }

        return $"{SonarChannels.DisplayName(_mix)}\n{Context.Monitor.GetOutputDevice(_mix)?.ShortName ?? "--"}";
    }

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        // Read both lists fresh: devices come and go, and the poll only refreshes them slowly.
        List<SonarAudioDevice>? devices = await Context.Client.GetOutputDevicesAsync(ct).ConfigureAwait(false);
        List<SonarStreamRedirection>? redirections =
            await Context.Client.GetStreamRedirectionsAsync(ct).ConfigureAwait(false);

        List<SonarAudioDevice> active = devices?.Where(d => d.IsActive && d.Id.Length > 0).ToList() ?? [];
        if (active.Count == 0)
        {
            Context.Logger?.Info("Sonar: no output device to switch to.");
            return;
        }

        string? redirectionId = SonarChannels.RedirectionId(_mix);
        string? currentId = redirections?.FirstOrDefault(r => r.Id == redirectionId)?.DeviceId;

        // An unknown current device (index -1) starts over at the first one.
        int index = active.FindIndex(d => d.Id == currentId);
        SonarAudioDevice next = active[(index + 1) % active.Count];
        if (next.Id == currentId)
        {
            return;
        }

        SonarStreamRedirection? updated =
            await Context.Client.SetRedirectionDeviceAsync(_mix, next.Id, ct).ConfigureAwait(false);
        if (updated == null)
        {
            return;
        }

        Context.Monitor.ApplyRedirection(updated);
        ShowRotaryOverlay(ctx, next.ShortName);
    }
}

/// <summary>
/// Routes one channel into or out of the streaming or the monitoring mix. This is what decides
/// whether your viewers hear a channel at all, independently of its volume.
/// </summary>
public sealed class SonarStreamRoutingCommand : SonarCommandBase, IDisplayCommand, ISonarStatefulCommand
{
    public const string EnabledState = "Enabled";
    public const string DisabledState = "Disabled";

    private readonly SonarMix _mix;
    private readonly SonarChannel _channel;

    public SonarStreamRoutingCommand(SonarContext context, SonarMix mix, SonarChannel channel)
        : base(context)
    {
        _mix = mix;
        _channel = channel;

        Descriptor = new CommandDescriptor
        {
            CommandName = $"SteelseriesSonar.{mix}.{channel.Role}.RoutingToggle",
            DisplayName = $"Sonar: {SonarChannels.DisplayName(mix)} {channel.DisplayName} Routing",
            Group = GroupName,
            Description =
                $"Route {channel.DisplayName} into or out of the {SonarChannels.DisplayName(mix)} mix",
            HiddenFromMenu = true,
            States =
            [
                new ButtonStateDescriptor { Name = DisabledState, Description = "The channel is not routed" },
                new ButtonStateDescriptor { Name = EnabledState, Description = "The channel is routed" }
            ]
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public string CommandName => Descriptor.CommandName;

    public string? CurrentStateName => Context.Monitor.State.IsConnected
        ? Context.Monitor.IsRedirectionEnabled(_mix, _channel.Role) ? EnabledState : DisabledState
        : null;

    public string GetText(CommandContext ctx) =>
        $"{_channel.DisplayName}\n{(Context.Monitor.IsRedirectionEnabled(_mix, _channel.Role) ? "ROUTED" : "OFF")}";

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        bool target = !Context.Monitor.IsRedirectionEnabled(_mix, _channel.Role);
        await Context.Client
            .SetRedirectionEnabledAsync(_mix, _channel.Role, target, ct)
            .ConfigureAwait(false);
    }
}
