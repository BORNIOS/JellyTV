using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Trailer or video attached to an enriched programme.
/// </summary>
public sealed class EnrichTrailer
{
    /// <summary>Gets or sets the video title.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the hosting site (for example YouTube).</summary>
    [JsonPropertyName("site")]
    public string? Site { get; set; }

    /// <summary>Gets or sets the site-specific video key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>Gets or sets the video type (for example Trailer or Teaser).</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
