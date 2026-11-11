using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Cast or crew member attached to an enriched programme.
/// </summary>
public sealed class EnrichCastMember
{
    /// <summary>Gets or sets the person's name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the character or job performed by the person.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}
