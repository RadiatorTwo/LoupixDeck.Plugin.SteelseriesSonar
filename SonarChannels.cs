namespace LoupixDeck.Plugin.SteelseriesSonar;

/// <summary>
/// One of the three independent mixes Sonar exposes. <see cref="Classic"/> is the single mix used
/// in classic mode; <see cref="Streaming"/> and <see cref="Monitoring"/> are the two separate
/// sliders stream mode splits every channel into.
/// </summary>
public enum SonarMix
{
    Classic,
    Streaming,
    Monitoring
}

/// <summary>A Sonar audio channel: the wire role plus how it is labelled in the UI.</summary>
public sealed record SonarChannel(string Role, string DisplayName);

/// <summary>
/// Static description of Sonar's channel and mix layout, including the path differences between
/// the classic and the stream endpoints. Callers build URLs through here, never by hand.
/// </summary>
public static class SonarChannels
{
    /// <summary>The master channel's role. It uses a different path segment than the others.</summary>
    public const string MasterRole = "master";

    /// <summary>Every channel Sonar exposes, in the order they are shown in the Sonar UI.</summary>
    public static IReadOnlyList<SonarChannel> All { get; } =
    [
        new SonarChannel(MasterRole, "Master"),
        new SonarChannel("game", "Game"),
        new SonarChannel("chatRender", "Chat"),
        new SonarChannel("media", "Media"),
        new SonarChannel("aux", "Aux"),
        new SonarChannel("chatCapture", "Mic")
    ];

    /// <summary>Every mix, in menu order.</summary>
    public static IReadOnlyList<SonarMix> AllMixes { get; } =
        [SonarMix.Streaming, SonarMix.Monitoring, SonarMix.Classic];

    /// <summary>Channels that take part in the stream redirections (master is not one of them).</summary>
    public static IReadOnlyList<SonarChannel> RedirectableChannels { get; } =
        All.Where(c => c.Role != MasterRole).ToArray();

    public static string DisplayName(SonarMix mix) => mix switch
    {
        SonarMix.Classic => "Classic",
        SonarMix.Streaming => "Streaming",
        SonarMix.Monitoring => "Monitoring",
        _ => mix.ToString()
    };

    /// <summary>
    /// The stream redirection id a mix maps to, as used by <c>/streamRedirections/{id}/...</c>.
    /// Null for <see cref="SonarMix.Classic"/>, which has no redirection.
    /// </summary>
    public static string? RedirectionId(SonarMix mix) => mix switch
    {
        SonarMix.Streaming => "streaming",
        SonarMix.Monitoring => "monitoring",
        _ => null
    };

    /// <summary>True when the mix only exists while Sonar runs in stream mode.</summary>
    public static bool RequiresStreamMode(SonarMix mix) => mix != SonarMix.Classic;

    /// <summary>The volume-settings endpoint that carries the given mix's current values.</summary>
    public static string VolumeSettingsPath(SonarMix mix) =>
        mix == SonarMix.Classic ? "/volumeSettings/classic" : "/volumeSettings/streamer";

    /// <summary>
    /// Path of the PUT that sets a channel's volume. Sonar spells the segment <c>Volume</c> in
    /// classic mode and <c>volume</c> in stream mode, and names the master channel <c>Master</c>
    /// resp. <c>master</c> instead of using a role.
    /// </summary>
    public static string VolumePath(SonarMix mix, string role) => mix == SonarMix.Classic
        ? $"/volumeSettings/classic/{ClassicSegment(role)}/Volume"
        : $"/volumeSettings/streamer/{RedirectionId(mix)}/{StreamSegment(role)}/volume";

    /// <summary>
    /// Path of the PUT that mutes a channel. The mute segment is called <c>Mute</c> in classic mode
    /// and <c>isMuted</c> in stream mode.
    /// </summary>
    public static string MutePath(SonarMix mix, string role) => mix == SonarMix.Classic
        ? $"/volumeSettings/classic/{ClassicSegment(role)}/Mute"
        : $"/volumeSettings/streamer/{RedirectionId(mix)}/{StreamSegment(role)}/isMuted";

    private static string ClassicSegment(string role) =>
        role == MasterRole ? "Master" : role;

    private static string StreamSegment(string role) =>
        role == MasterRole ? "master" : role;
}
