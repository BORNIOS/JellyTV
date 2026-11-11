namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// EPG identity provided by a source and its link to a plugin channel.
/// </summary>
/// <param name="EpgSourceId">EPG source that provides the identity.</param>
/// <param name="XmltvId">XMLTV channel id.</param>
/// <param name="ChannelId">Matched plugin channel, if any.</param>
/// <param name="DisplayName">Display name (channel name when matched).</param>
/// <param name="IconUrl">Channel logo, if known.</param>
/// <param name="MatchMethod">How the match was made (tvg-id, tvg-name, none).</param>
public sealed record EpgChannelRecord(
    string EpgSourceId,
    string XmltvId,
    string? ChannelId,
    string? DisplayName,
    string? IconUrl,
    string MatchMethod);
