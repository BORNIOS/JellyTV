using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Age rating for a specific country attached to an enriched programme.
/// </summary>
public sealed class EnrichCertification
{
    /// <summary>Gets or sets the ISO country code of the certification.</summary>
    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    /// <summary>Gets or sets the certification rating.</summary>
    [JsonPropertyName("rating")]
    public string Rating { get; set; } = string.Empty;
}
