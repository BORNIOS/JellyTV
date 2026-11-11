using System;

namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// XMLTV guide source configured in the plugin.
/// </summary>
public class EpgSource
{
    /// <summary>Gets or sets unique identifier for this EPG source.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Gets or sets display name (e.g. "México EPG").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets uRL of the XMLTV file (may be large — the Plugin handles download + filter).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether whether this EPG source is active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets last time this source was successfully synced (local guide).</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>Gets or sets last sync result summary (e.g. "42 channels, 1800 programmes").</summary>
    public string? LastSyncSummary { get; set; }

    /// <summary>Gets or sets last error during sync, null if OK.</summary>
    public string? LastSyncError { get; set; }
}
