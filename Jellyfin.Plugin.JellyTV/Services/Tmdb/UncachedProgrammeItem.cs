using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

public sealed record UncachedProgrammeItem(
    string Title,
    string? Description = null,
    string? Category = null,
    int? Year = null,
    /// <summary>Secondary title (e.g. Spanish when primary is English) used as additional search variant.</summary>
    string? FallbackTitle = null);
