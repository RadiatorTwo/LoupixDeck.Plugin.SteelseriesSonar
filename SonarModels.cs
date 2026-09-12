using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>Volume plus mute state of a single channel in a single mix.</summary>
public sealed class SonarLevel
{
    public double Volume { get; init; }
    public bool Muted { get; init; }
}

/// <summary>The two sliders a channel is split into while Sonar runs in stream mode.</summary>
public sealed class SonarStreamLevels
{
    public SonarLevel? Streaming { get; init; }
    public SonarLevel? Monitoring { get; init; }
}

/// <summary>One channel's entry in a volume-settings response, across both modes.</summary>
public sealed class SonarChannelVolume
{
    public SonarStreamLevels? Stream { get; init; }
    public SonarLevel? Classic { get; init; }
}

/// <summary>Response body of <c>/volumeSettings/classic</c> and <c>/volumeSettings/streamer</c>.</summary>
public sealed class SonarVolumeSettings
{
    /// <summary>The master channel. Sonar keeps it outside <see cref="Devices"/>.</summary>
    public SonarChannelVolume? Masters { get; init; }

    /// <summary>Every non-master channel, keyed by role.</summary>
    public Dictionary<string, SonarChannelVolume>? Devices { get; init; }

    /// <summary>Looks up one channel's entry, transparently handling the master special case.</summary>
    public SonarChannelVolume? Find(string role)
    {
        if (role == SonarChannels.MasterRole)
        {
            return Masters;
        }

        return Devices != null && Devices.TryGetValue(role, out SonarChannelVolume? channel)
            ? channel
            : null;
    }

    /// <summary>The level of one channel in one mix, or null when Sonar reports nothing for it.</summary>
    public SonarLevel? Level(SonarMix mix, string role)
    {
        SonarChannelVolume? channel = Find(role);
        if (channel == null)
        {
            return null;
        }

        return mix switch
        {
            SonarMix.Classic => channel.Classic,
            SonarMix.Streaming => channel.Stream?.Streaming,
            SonarMix.Monitoring => channel.Stream?.Monitoring,
            _ => null
        };
    }
}

/// <summary>One audio configuration preset, as returned by <c>/configs</c>.</summary>
public sealed class SonarConfig
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>The channel this preset applies to, e.g. <c>game</c> or <c>chatCapture</c>.</summary>
    public string VirtualAudioDevice { get; init; } = string.Empty;
}

/// <summary>Whether a single channel is routed into a given stream redirection.</summary>
public sealed class SonarRedirectionStatus
{
    public string Role { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
}

/// <summary>One entry of <c>/streamRedirections</c>: the streaming, monitoring or mic route.</summary>
public sealed class SonarStreamRedirection
{
    [JsonPropertyName("streamRedirectionId")]
    public string Id { get; init; } = string.Empty;

    public string? DeviceId { get; init; }

    public List<SonarRedirectionStatus>? Status { get; init; }

    public bool IsRunning { get; init; }

    public bool IsRoleEnabled(string role) =>
        Status?.FirstOrDefault(s => s.Role == role)?.IsEnabled ?? false;
}

/// <summary>One physical audio device, as returned by <c>/audioDevices</c>.</summary>
public sealed class SonarAudioDevice
{
    /// <summary>Windows endpoint id, e.g. <c>{0.0.0.00000000}.{guid}</c>.</summary>
    public string Id { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;

    /// <summary><c>active</c> for a usable device. Absent on older Sonar builds.</summary>
    public string? State { get; init; }

    public bool IsActive => State == null || State == "active";

    /// <summary>
    /// The name without the Windows endpoint prefix: <c>Lautsprecher (2- FiiO K11)</c> becomes
    /// <c>FiiO K11</c>. The full name is too long for a button.
    /// </summary>
    public string ShortName
    {
        get
        {
            string name = FriendlyName;
            int open = name.IndexOf('(');
            if (open >= 0 && name.EndsWith(')'))
            {
                name = name[(open + 1)..^1];
            }

            Match numbered = Regex.Match(name, @"^\d+-\s*");
            return numbered.Success ? name[numbered.Length..] : name;
        }
    }
}

/// <summary>Metadata of one SteelSeries GG sub-application, from <c>/subApps</c>.</summary>
public sealed class SubAppMetadata
{
    public string? WebServerAddress { get; init; }
}

/// <summary>One sub-application entry of the GG core's <c>/subApps</c> response.</summary>
public sealed class SubApp
{
    public bool IsEnabled { get; init; }
    public bool IsReady { get; init; }
    public bool IsRunning { get; init; }
    public SubAppMetadata? Metadata { get; init; }
}

/// <summary>Response body of the GG core's <c>/subApps</c> endpoint.</summary>
public sealed class SubAppsResponse
{
    public Dictionary<string, SubApp>? SubApps { get; init; }
}

/// <summary>Contents of <c>coreProps.json</c>, which points at the GG core's HTTPS endpoint.</summary>
public sealed class SonarCoreProps
{
    [JsonPropertyName("ggEncryptedAddress")]
    public string? GgEncryptedAddress { get; init; }
}
