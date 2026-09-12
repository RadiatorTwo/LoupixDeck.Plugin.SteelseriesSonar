using System.Collections.Concurrent;
using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// Controls the SteelSeries Sonar audio mixer from the deck. Sonar has no public API; this plugin
/// talks to the local HTTP service the Sonar UI itself uses.
/// </summary>
public sealed class SteelseriesSonarPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    /// <summary>Settings keys. Persisted in plugins/steelseriessonar/settings.json.</summary>
    private const string PollIntervalKey = "pollIntervalMs";
    private const string VolumeStepKey = "volumeStepPercent";

    private const int DefaultPollIntervalMs = 750;
    private const int DefaultVolumeStepPercent = 5;

    private readonly List<IPluginCommand> _commands = [];
    private readonly List<ISonarStatefulCommand> _statefulCommands = [];
    private readonly ConcurrentDictionary<string, string> _pushedStates = new();

    private SonarStripProvider? _stripProvider;
    private SonarClient? _client;
    private SonarStateMonitor? _monitor;
    private IPluginHost? _host;
    private IPluginSettings? _settings;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "steelseriessonar",
        Name = "SteelSeries Sonar",
        Version = new Version(1, 1, 0),
        SdkVersion = new Version(1, 21, 0),
        Author = "RadiatorTwo",
        Description = "Control the SteelSeries Sonar mixer, including the separate streaming and " +
                      "monitoring volumes of stream mode."
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = host.Settings;

        _client = new SonarClient(host.Logger);
        _monitor = new SonarStateMonitor(_client, host.Logger, () => TimeSpan.FromMilliseconds(PollIntervalMs));

        SonarContext context = new(_client, _monitor, host, () => VolumeStepPercent);
        BuildCommands(context);

        _stripProvider = new SonarStripProvider(context, host.Settings);

        _monitor.StateChanged += OnStateChanged;
        _monitor.Start();
    }

    public override void Shutdown()
    {
        if (_monitor != null)
        {
            _monitor.StateChanged -= OnStateChanged;
            _monitor.Dispose();
            _monitor = null;
        }

        _client?.Dispose();
        _client = null;
        _stripProvider = null;

        base.Shutdown();
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = SonarCommandBase.GroupName,
            Description = "SteelSeries Sonar audio mixer",
            Icon = "󰘮",
            Section = CommandGroupSection.Plugins
        }
    ];

    public override IEnumerable<ISideStripProvider> GetSideStripProviders() =>
        _stripProvider == null ? [] : [_stripProvider];

    // ---- Command construction ---------------------------------------------------------------

    /// <summary>
    /// Builds one command per mix, channel and action. They are generated rather than written out
    /// so the set stays consistent, but each one is still a fixed, fully configured command from
    /// the user's point of view.
    /// </summary>
    private void BuildCommands(SonarContext context)
    {
        foreach (SonarMix mix in SonarChannels.AllMixes)
        {
            foreach (SonarChannel channel in SonarChannels.All)
            {
                _commands.Add(new SonarVolumeCommand(context, mix, channel, up: true));
                _commands.Add(new SonarVolumeCommand(context, mix, channel, up: false));
                Add(new SonarMuteToggleCommand(context, mix, channel));
            }

            if (SonarChannels.RedirectionId(mix) == null)
            {
                continue;
            }

            foreach (SonarChannel channel in SonarChannels.RedirectableChannels)
            {
                Add(new SonarStreamRoutingCommand(context, mix, channel));
            }
        }

        Add(new SonarModeCommand(context, targetStreamMode: null));
        Add(new SonarModeCommand(context, targetStreamMode: true));
        Add(new SonarModeCommand(context, targetStreamMode: false));
        Add(new SonarStreamMonitoringToggleCommand(context));

        // Monitoring only: the streaming mix goes to Sonar's virtual stream device, which the
        // device list deliberately leaves out, so cycling it would break the stream.
        _commands.Add(new SonarNextOutputDeviceCommand(context, SonarMix.Monitoring));

        _commands.Add(new SonarSelectConfigCommand(context));

        void Add<T>(T command) where T : IPluginCommand, ISonarStatefulCommand
        {
            _commands.Add(command);
            _statefulCommands.Add(command);
        }
    }

    // ---- Live updates -----------------------------------------------------------------------

    /// <summary>
    /// Pushes the current state to every stateful button. Runs on the poll thread, so it must not
    /// throw.
    /// </summary>
    private void OnStateChanged()
    {
        IPluginHost? host = _host;
        if (host == null)
        {
            return;
        }

        try
        {
            foreach (ISonarStatefulCommand command in _statefulCommands)
            {
                string? state = command.CurrentStateName;
                if (state == null)
                {
                    continue;
                }

                // Only push what actually changed: the host would otherwise redraw the whole deck.
                if (_pushedStates.TryGetValue(command.CommandName, out string? pushed) && pushed == state)
                {
                    continue;
                }

                _pushedStates[command.CommandName] = state;
                host.SetActiveButtonState(command.CommandName, state);
            }

            // Display commands need no refresh request: the host already polls GetText on their
            // own UpdateInterval, and an optimistic write is visible there immediately.
        }
        catch (Exception ex)
        {
            host.Logger?.Error("Sonar: pushing the button state failed", ex);
        }
    }

    // ---- Menu -------------------------------------------------------------------------------

    /// <summary>
    /// Builds the command menu. Roughly seventy generated commands would be unusable as a flat
    /// list, so they are nested here and hidden from the plain list.
    /// </summary>
    /// <remarks>
    /// The classic mix is the top level: it is the single set of sliders most setups ever touch, so
    /// its channels sit directly under the group instead of behind a mode branch. Stream mode splits
    /// every channel into a streaming and a monitoring slider, and those two belong together under
    /// one branch at the bottom — keeping them apart is the whole point of the separate mix.
    ///
    /// Exactly one root node is returned, and its name must equal the command group name: the host
    /// merges returned roots into the menu by name, so anything else becomes a top-level group of
    /// its own instead of landing inside the plugin's group.
    /// </remarks>
    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        bool withRotary = target.HasFlag(ButtonTargets.RotaryEncoder);

        List<MenuNode> categories = SonarChannels.All
            .Select(channel => new MenuNode
            {
                Name = channel.DisplayName,
                Children = MixActions(SonarMix.Classic, channel, prefix: null, withRotary)
            })
            .ToList();

        categories.Add(new MenuNode
        {
            Name = "Mode",
            Children =
            [
                Leaf("Toggle Classic / Streamer", "SteelseriesSonar.ModeToggle"),
                Leaf("Streamer Mode", "SteelseriesSonar.ModeStream"),
                Leaf("Classic Mode", "SteelseriesSonar.ModeClassic"),
                Leaf("Stream Monitoring", "SteelseriesSonar.StreamMonitoringToggle")
            ]
        });

        MenuNode? presets = BuildPresetMenu();
        if (presets != null)
        {
            categories.Add(presets);
        }

        categories.Add(new MenuNode
        {
            Name = "Streamer Mode",
            Children =
            [
                .. SonarChannels.All.Select(channel => new MenuNode
                {
                    Name = channel.DisplayName,
                    Children =
                    [
                        .. MixActions(SonarMix.Streaming, channel, "Streaming", withRotary),
                        .. MixActions(SonarMix.Monitoring, channel, "Monitoring", withRotary)
                    ]
                }),
                Leaf("Monitoring Next Output Device", "SteelseriesSonar.Monitoring.NextOutputDevice")
            ]
        });

        IReadOnlyList<MenuNode> roots =
        [
            new MenuNode { Name = SonarCommandBase.GroupName, Children = categories }
        ];

        return Task.FromResult(roots);
    }

    /// <summary>
    /// The actions of one channel in one mix. In stream mode a channel carries two of these sets,
    /// so each entry is prefixed with the slider it belongs to; in classic mode there is only one
    /// set and the prefix is left off.
    /// </summary>
    private static List<MenuNode> MixActions(SonarMix mix, SonarChannel channel, string? prefix, bool withRotary)
    {
        string command = $"SteelseriesSonar.{mix}.{channel.Role}";
        string label = prefix == null ? string.Empty : prefix + " ";

        List<MenuNode> actions =
        [
            Leaf($"{label}Volume Up", $"{command}.VolumeUp"),
            Leaf($"{label}Volume Down", $"{command}.VolumeDown"),
            Leaf($"{label}Mute Toggle", $"{command}.MuteToggle")
        ];

        if (SonarChannels.RedirectionId(mix) != null && channel.Role != SonarChannels.MasterRole)
        {
            actions.Add(Leaf($"{label}Routing Toggle", $"{command}.RoutingToggle"));
        }

        if (withRotary)
        {
            actions.Add(new MenuNode
            {
                Name = $"{label}Assign to Rotary",
                RotaryGroup = new Dictionary<RotaryAction, MenuCommandRef>
                {
                    [RotaryAction.Clockwise] = new() { CommandName = $"{command}.VolumeUp" },
                    [RotaryAction.CounterClockwise] = new() { CommandName = $"{command}.VolumeDown" },
                    [RotaryAction.Press] = new() { CommandName = $"{command}.MuteToggle" }
                }
            });
        }

        return actions;
    }

    private static MenuNode Leaf(string name, string commandName) =>
        new() { Name = name, CommandName = commandName };

    /// <summary>
    /// Groups the user's presets by the channel they apply to. Returns null while Sonar has not
    /// been reached yet, so the menu simply omits the entry instead of showing an empty branch.
    /// </summary>
    private MenuNode? BuildPresetMenu()
    {
        IReadOnlyList<SonarConfig> configs = _monitor?.State.Configs ?? [];
        if (configs.Count == 0)
        {
            return null;
        }

        List<MenuNode> channels = [];

        foreach (SonarChannel channel in SonarChannels.All)
        {
            List<MenuNode> presets = configs
                .Where(c => c.VirtualAudioDevice == channel.Role)
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new MenuNode
                {
                    Name = c.Name,
                    CommandName = "SteelseriesSonar.SelectConfig",
                    Parameters = new Dictionary<string, string> { ["configId"] = c.Id }
                })
                .ToList();

            if (presets.Count > 0)
            {
                channels.Add(new MenuNode { Name = channel.DisplayName, Children = presets });
            }
        }

        return channels.Count == 0 ? null : new MenuNode { Name = "Presets", Children = channels };
    }

    // ---- Settings ---------------------------------------------------------------------------

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new PluginSettingDescriptor
        {
            Key = "__heading_polling",
            Label = "Live Updates",
            Kind = PluginSettingKind.Heading,
            Description = "Sonar offers no change notifications, so the plugin asks it regularly.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = PollIntervalKey,
            Label = "Refresh interval (ms)",
            Kind = PluginSettingKind.Number,
            Description = "How often the button labels are brought up to date. 250 to 10000.",
            DefaultValue = DefaultPollIntervalMs
        },
        new PluginSettingDescriptor
        {
            Key = "__heading_strip",
            Label = "Side Strip",
            Kind = PluginSettingKind.Heading,
            Description = "Bars for the dials next to the strip. Each bar follows the mix and " +
                          "channel its dial is bound to.",
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = SonarStripProvider.HorizontalLayoutKey,
            Label = "Horizontal segments",
            Kind = PluginSettingKind.Toggle,
            Description = "Off: side-by-side vertical bars. On: stacked horizontal bands.",
            DefaultValue = false
        },
        new PluginSettingDescriptor
        {
            Key = SonarStripProvider.ShowModeHeaderKey,
            Label = "Show the mode header",
            Kind = PluginSettingKind.Toggle,
            Description = "Shows the current Sonar mode and, in stream mode, whether stream " +
                          "monitoring is on.",
            DefaultValue = true
        },
        new PluginSettingDescriptor
        {
            Key = "__heading_volume",
            Label = "Volume",
            Kind = PluginSettingKind.Heading,
            DefaultValue = string.Empty
        },
        new PluginSettingDescriptor
        {
            Key = VolumeStepKey,
            Label = "Default step (%)",
            Kind = PluginSettingKind.Number,
            Description = "Used by volume buttons that carry no step of their own. 1 to 50.",
            DefaultValue = DefaultVolumeStepPercent
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
        _settingsActions ??=
        [
            new PluginSettingAction { Label = "Test connection", Invoke = TestConnectionAsync }
        ];

    private IReadOnlyList<PluginSettingAction>? _settingsActions;

    /// <summary>
    /// Reports whether Sonar can be reached. Discovery is the part most likely to go wrong on a
    /// user's machine, so it is worth being able to check it from the settings page.
    /// </summary>
    private async Task<string> TestConnectionAsync()
    {
        SonarClient? client = _client;
        if (client == null)
        {
            return "The plugin is not initialized.";
        }

        if (!await client.EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return "Sonar was not found. Make sure SteelSeries GG is running and Sonar is enabled.";
        }

        string? mode = await client.GetModeAsync(CancellationToken.None).ConfigureAwait(false);
        return mode == null
            ? "Sonar was found but did not answer."
            : $"Connected. Sonar is in {(mode == "stream" ? "stream" : "classic")} mode.";
    }

    public void OnSettingsSaved()
    {
        // The numeric settings are read through the accessors below on every use, so nothing to
        // apply here beyond dropping the cached button states so the next poll pushes them again.
        _pushedStates.Clear();

        // The strip layout may have changed — repaint anything currently attached.
        _stripProvider?.NotifyLayoutChanged();
    }

    private int PollIntervalMs =>
        Math.Clamp(ReadInt(PollIntervalKey, DefaultPollIntervalMs), 250, 10000);

    private int VolumeStepPercent =>
        Math.Clamp(ReadInt(VolumeStepKey, DefaultVolumeStepPercent), 1, 50);

    /// <summary>
    /// Reads a numeric setting. The host stores numbers as <c>long</c>, but an older settings file
    /// may hold the same value as a string, so both are accepted.
    /// </summary>
    private int ReadInt(string key, int fallback)
    {
        IPluginSettings? settings = _settings;
        if (settings == null || !settings.Contains(key))
        {
            return fallback;
        }

        try
        {
            long stored = settings.Get<long>(key, fallback);
            if (stored != 0)
            {
                return (int)stored;
            }
        }
        catch (Exception)
        {
            // Fall through to the string form below.
        }

        string? text = settings.Get<string>(key);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }
}
