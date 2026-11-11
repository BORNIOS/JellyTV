using System;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Pointer to the source currently used for a channel.
/// </summary>
/// <param name="ChannelId">Channel identifier.</param>
/// <param name="SourceId">Selected source identifier.</param>
/// <param name="Reason">Why the pointer changed (health, open-failure, manual, seed).</param>
/// <param name="SwitchedAt">Moment of the switch (UTC).</param>
public sealed record ActiveSourceRecord(
    string ChannelId,
    string SourceId,
    string Reason,
    DateTimeOffset SwitchedAt);
