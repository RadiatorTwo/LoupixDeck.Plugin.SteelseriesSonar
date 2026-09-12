using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Raises or lowers one channel's volume in one mix. One instance exists per mix and channel, so
/// a button is bound to a fixed target and needs no configuration beyond an optional step size.
/// </summary>
public sealed class SonarVolumeCommand : SonarCommandBase, IDisplayCommand, IAdjustmentCommand
{
    private readonly SonarMix _mix;
    private readonly SonarChannel _channel;
    private readonly int _direction;

    public SonarVolumeCommand(SonarContext context, SonarMix mix, SonarChannel channel, bool up)
        : base(context)
    {
        _mix = mix;
        _channel = channel;
        _direction = up ? 1 : -1;

        string action = up ? "VolumeUp" : "VolumeDown";
        string arrow = up ? "+" : "-";

        Descriptor = new CommandDescriptor
        {
            CommandName = $"SteelseriesSonar.{mix}.{channel.Role}.{action}",
            DisplayName = $"Sonar: {SonarChannels.DisplayName(mix)} {channel.DisplayName} {arrow}",
            Group = GroupName,
            Description = up
                ? $"Raise the {channel.DisplayName} volume in the {SonarChannels.DisplayName(mix)} mix"
                : $"Lower the {channel.DisplayName} volume in the {SonarChannels.DisplayName(mix)} mix",
            ParameterTemplate = "({step})",
            Parameters = StepParameters(context.DefaultStepPercent()),
            HiddenFromMenu = true
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);

    public string GetText(CommandContext ctx)
    {
        SonarLevel? level = Context.Monitor.GetLevel(_mix, _channel.Role);
        return $"{_channel.DisplayName}\n{FormatLevel(level)}";
    }

    protected override Task ExecuteCore(CommandContext ctx, CancellationToken ct) =>
        AdjustAsync(ctx, ResolveStep(ctx) * _direction, ct);

    // ---- IAdjustmentCommand ----------------------------------------------------------------
    // Forward-compatible: the host dispatches these once rotary adjustment commands are wired up.

    /// <summary>
    /// The encoder supplies the direction through <paramref name="ticks"/>, so this instance's own
    /// up/down direction does not apply here.
    /// </summary>
    public async Task ApplyAdjustment(CommandContext ctx, int ticks)
    {
        try
        {
            await AdjustAsync(ctx, ResolveStep(ctx) * ticks, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Context.Logger?.Error($"Sonar: {Descriptor.CommandName} adjustment failed", ex);
        }
    }

    public Task ApplyReset(CommandContext ctx) => Task.CompletedTask;

    public string? GetValueText(CommandContext ctx) =>
        FormatLevel(Context.Monitor.GetLevel(_mix, _channel.Role));

    // ---- Core ------------------------------------------------------------------------------

    private async Task AdjustAsync(CommandContext ctx, double delta, CancellationToken ct)
    {
        SonarLevel? level = await ReadLevelAsync(_mix, _channel.Role, ct).ConfigureAwait(false);
        if (level == null)
        {
            Context.Logger?.Info(
                $"Sonar: {_channel.DisplayName} has no {SonarChannels.DisplayName(_mix)} level right now.");
            return;
        }

        double target = Math.Clamp(level.Volume + delta, 0d, 1d);
        if (Math.Abs(target - level.Volume) < 0.0005d)
        {
            return;
        }

        if (await Context.Client.SetVolumeAsync(_mix, _channel.Role, target, ct).ConfigureAwait(false))
        {
            Context.Monitor.ApplyLocalLevel(_mix, _channel.Role, target, level.Muted);
            ShowRotaryOverlay(ctx, $"{_channel.DisplayName} {FormatLevel(new SonarLevel { Volume = target, Muted = level.Muted })}");
        }
    }
}

/// <summary>Mutes or unmutes one channel in one mix, and shows the current state on the button.</summary>
public sealed class SonarMuteToggleCommand : SonarCommandBase, IDisplayCommand, ISonarStatefulCommand
{
    /// <summary>State names are persisted in the button config and must never change.</summary>
    public const string UnmutedState = "Unmuted";
    public const string MutedState = "Muted";

    private readonly SonarMix _mix;
    private readonly SonarChannel _channel;

    public SonarMuteToggleCommand(SonarContext context, SonarMix mix, SonarChannel channel)
        : base(context)
    {
        _mix = mix;
        _channel = channel;

        Descriptor = new CommandDescriptor
        {
            CommandName = $"SteelseriesSonar.{mix}.{channel.Role}.MuteToggle",
            DisplayName = $"Sonar: {SonarChannels.DisplayName(mix)} {channel.DisplayName} Mute",
            Group = GroupName,
            Description = $"Mute or unmute {channel.DisplayName} in the {SonarChannels.DisplayName(mix)} mix",
            HiddenFromMenu = true,
            States =
            [
                new ButtonStateDescriptor { Name = UnmutedState, Description = "The channel is audible" },
                new ButtonStateDescriptor { Name = MutedState, Description = "The channel is muted" }
            ]
        };
    }

    public override CommandDescriptor Descriptor { get; }

    public TimeSpan UpdateInterval => TimeSpan.FromMilliseconds(500);

    public string CommandName => Descriptor.CommandName;

    public string? CurrentStateName
    {
        get
        {
            SonarLevel? level = Context.Monitor.GetLevel(_mix, _channel.Role);
            return level == null ? null : level.Muted ? MutedState : UnmutedState;
        }
    }

    public string GetText(CommandContext ctx)
    {
        SonarLevel? level = Context.Monitor.GetLevel(_mix, _channel.Role);
        if (level == null)
        {
            return $"{_channel.DisplayName}\n--";
        }

        return $"{_channel.DisplayName}\n{(level.Muted ? "MUTED" : "ON")}";
    }

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        SonarLevel? level = await ReadLevelAsync(_mix, _channel.Role, ct).ConfigureAwait(false);
        if (level == null)
        {
            Context.Logger?.Info(
                $"Sonar: {_channel.DisplayName} has no {SonarChannels.DisplayName(_mix)} level right now.");
            return;
        }

        bool target = !level.Muted;
        if (await Context.Client.SetMuteAsync(_mix, _channel.Role, target, ct).ConfigureAwait(false))
        {
            Context.Monitor.ApplyLocalLevel(_mix, _channel.Role, level.Volume, target);
            ShowRotaryOverlay(ctx, $"{_channel.DisplayName} {(target ? "MUTED" : "ON")}");
        }
    }
}
