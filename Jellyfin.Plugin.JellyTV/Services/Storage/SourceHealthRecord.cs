using System;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Outcome of checking one channel source.
/// </summary>
/// <param name="ChannelId">Owning channel identifier.</param>
/// <param name="SourceId">Checked source identifier.</param>
/// <param name="CheckedAt">Moment of the check (UTC).</param>
/// <param name="IsAlive">Whether the source could be opened.</param>
/// <param name="Error">Failure detail, if any.</param>
public sealed record SourceHealthRecord(
    string ChannelId,
    string SourceId,
    DateTimeOffset CheckedAt,
    bool IsAlive,
    string? Error);
