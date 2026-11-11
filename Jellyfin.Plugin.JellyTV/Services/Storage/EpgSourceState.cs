using System;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Sync outcome of one EPG source.
/// </summary>
/// <param name="EpgSourceId">EPG source identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Url">Source URL.</param>
/// <param name="IsEnabled">Whether the source is enabled.</param>
/// <param name="Priority">Configuration order.</param>
/// <param name="SyncedAt">Moment of the sync.</param>
/// <param name="Summary">Human-readable result.</param>
/// <param name="Error">Failure detail, if any.</param>
/// <param name="ChannelCount">Channels kept in the last sync.</param>
/// <param name="ProgrammeCount">Programmes kept in the last sync.</param>
public sealed record EpgSourceState(
    string EpgSourceId,
    string Name,
    string Url,
    bool IsEnabled,
    int Priority,
    DateTimeOffset? SyncedAt,
    string? Summary,
    string? Error,
    int ChannelCount,
    int ProgrammeCount);
