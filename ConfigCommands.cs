using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Activates one of Sonar's audio configuration presets, for instance "FPS Footsteps" on the game
/// channel. The preset is identified by its id, which the plugin's menu fills in — presets are
/// user-specific, so they cannot be baked into fixed commands the way channels can.
/// </summary>
public sealed class SonarSelectConfigCommand(SonarContext context)
    : SonarCommandBase(context), IDisplayCommand
{
    public override CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "SteelseriesSonar.SelectConfig",
        DisplayName = "Sonar: Select Preset",
        Group = GroupName,
        Description = "Activate an audio preset on one channel",
        ParameterTemplate = "({configId})",
        Parameters = [new CommandParameter("configId", typeof(string))],
        HiddenFromMenu = true
    };

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(2);

    public string GetText(CommandContext ctx)
    {
        SonarConfig? config = ResolveConfig(ctx);
        if (config == null)
        {
            return "Preset";
        }

        SonarChannel? channel = SonarChannels.All
            .FirstOrDefault(c => c.Role == config.VirtualAudioDevice);

        return $"{channel?.DisplayName ?? config.VirtualAudioDevice}\n{config.Name}";
    }

    protected override async Task ExecuteCore(CommandContext ctx, CancellationToken ct)
    {
        string? configId = ResolveConfigId(ctx);
        if (configId == null)
        {
            Context.Logger?.Warn("Sonar: the preset button carries no preset id.");
            return;
        }

        await Context.Client.SelectConfigAsync(configId, ct).ConfigureAwait(false);
    }

    private static string? ResolveConfigId(CommandContext ctx)
    {
        string[]? parameters = ctx.Parameters;
        if (parameters is not { Length: > 0 })
        {
            return null;
        }

        string id = parameters[0];
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    private SonarConfig? ResolveConfig(CommandContext ctx)
    {
        string? id = ResolveConfigId(ctx);
        return id == null
            ? null
            : Context.Monitor.State.Configs.FirstOrDefault(c => c.Id == id);
    }
}
