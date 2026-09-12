using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>Everything a Sonar command needs, handed to it at construction time.</summary>
public sealed class SonarContext(
    SonarClient client,
    SonarStateMonitor monitor,
    IPluginHost host,
    Func<int> defaultStepPercent)
{
    public SonarClient Client { get; } = client;
    public SonarStateMonitor Monitor { get; } = monitor;
    public IPluginHost Host { get; } = host;
    public IPluginLogger? Logger { get; } = host.Logger;

    /// <summary>The volume step to use when a button carries no explicit one, in percent.</summary>
    public Func<int> DefaultStepPercent { get; } = defaultStepPercent;
}

/// <summary>
/// A command whose button shows one of a fixed set of states. The plugin pushes the current state
/// to the host whenever Sonar's state changes.
/// </summary>
public interface ISonarStatefulCommand
{
    string CommandName { get; }

    /// <summary>The state the button should currently show, or null when it is unknown.</summary>
    string? CurrentStateName { get; }
}

/// <summary>
/// Base class for every Sonar command. Guarantees the contract the host relies on: execution runs
/// off the UI thread, never blocks and never throws — failures are logged and swallowed.
/// </summary>
public abstract class SonarCommandBase(SonarContext context) : IPluginCommand
{
    /// <summary>The group every Sonar command is filed under.</summary>
    public const string GroupName = "SteelseriesSonar";

    protected SonarContext Context { get; } = context;

    public abstract CommandDescriptor Descriptor { get; }

    public virtual ButtonTargets SupportedTargets =>
        ButtonTargets.TouchButton | ButtonTargets.SimpleButton | ButtonTargets.RotaryEncoder;

    public async Task Execute(CommandContext ctx)
    {
        try
        {
            await ExecuteCore(ctx, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Context.Logger?.Error($"Sonar: {Descriptor.CommandName} failed", ex);
        }
    }

    protected abstract Task ExecuteCore(CommandContext ctx, CancellationToken ct);

    /// <summary>
    /// Reads the volume step for this press, in the 0..1 scale Sonar uses. A button may override
    /// the plugin-wide default through its first parameter, given in percent.
    /// </summary>
    protected double ResolveStep(CommandContext ctx)
    {
        int percent = Context.DefaultStepPercent();

        string[]? parameters = ctx.Parameters;
        if (parameters is { Length: > 0 } &&
            int.TryParse(parameters[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed != 0)
        {
            percent = Math.Abs(parsed);
        }

        return Math.Clamp(percent, 1, 100) / 100d;
    }

    /// <summary>
    /// The current level of a channel, falling back to a direct read when the poll loop has not
    /// seen it yet. Null when Sonar does not report the channel at all.
    /// </summary>
    protected async Task<SonarLevel?> ReadLevelAsync(SonarMix mix, string role, CancellationToken ct)
    {
        SonarLevel? level = Context.Monitor.GetLevel(mix, role);
        if (level != null)
        {
            return level;
        }

        SonarVolumeSettings? settings = await Context.Client.GetVolumeSettingsAsync(mix, ct).ConfigureAwait(false);
        return settings?.Level(mix, role);
    }

    /// <summary>The parameter declaration shared by every volume command.</summary>
    protected static IReadOnlyList<CommandParameter> StepParameters(int defaultPercent) =>
    [
        new CommandParameter("step", typeof(int))
        {
            DefaultValue = defaultPercent.ToString(CultureInfo.InvariantCulture)
        }
    ];

    /// <summary>
    /// Flashes the new value on the touch slot belonging to a rotary encoder. A dial carries no
    /// label of its own, so without this a turn gives no feedback anywhere on the device — and the
    /// Sonar window does not refresh either when something else changes a volume.
    /// </summary>
    protected void ShowRotaryOverlay(CommandContext ctx, string text)
    {
        if (ctx.Target != ButtonTargets.RotaryEncoder || ctx.SourceIndex is not int rotaryIndex)
        {
            return;
        }

        try
        {
            int slot = Context.Host.GetTouchSlotForRotary(rotaryIndex);
            if (slot >= 0)
            {
                Context.Host.OverlayTouchText(slot, text, TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception ex)
        {
            // Feedback is a nicety; never let it take down the actual volume change.
            Context.Logger?.Info($"Sonar: could not show the rotary overlay ({ex.Message}).");
        }
    }

    /// <summary>Formats a level for a touch button, e.g. <c>35%</c> or <c>MUTE</c>.</summary>
    protected static string FormatLevel(SonarLevel? level)
    {
        if (level == null)
        {
            return "--";
        }

        return level.Muted
            ? "MUTE"
            : Math.Round(level.Volume * 100d).ToString("0", CultureInfo.InvariantCulture) + "%";
    }
}
