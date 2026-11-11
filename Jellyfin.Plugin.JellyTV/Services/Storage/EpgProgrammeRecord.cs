using System;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// One programme persisted for fast EPG queries.
/// </summary>
/// <param name="EpgSourceId">EPG source that provided the programme.</param>
/// <param name="XmltvId">XMLTV channel id.</param>
/// <param name="ChannelId">Matched plugin channel, if any.</param>
/// <param name="Start">Start instant (UTC).</param>
/// <param name="Stop">End instant (UTC).</param>
/// <param name="Title">Programme title.</param>
/// <param name="Description">Synopsis, if any.</param>
/// <param name="Category">Primary category, if any.</param>
/// <param name="IconUrl">Artwork, if any.</param>
/// <param name="Year">Release year, if known.</param>
public sealed record EpgProgrammeRecord(
    string EpgSourceId,
    string XmltvId,
    string? ChannelId,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    string Title,
    string? Description,
    string? Category,
    string? IconUrl,
    int? Year);
