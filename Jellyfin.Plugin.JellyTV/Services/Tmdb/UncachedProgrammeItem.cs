using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Programme data used as input for TMDB matching before it is cached.
/// </summary>
/// <param name="Title">Primary programme title.</param>
/// <param name="Description">Optional programme description.</param>
/// <param name="Category">Optional programme category or genre.</param>
/// <param name="Year">Optional production year.</param>
/// <param name="FallbackTitle">Secondary title (e.g. Spanish when primary is English) used as additional search variant.</param>
/// <param name="ExpectedMediaType">Kind of work the source says this is: <c>tv</c> for a series episode, <c>movie</c> for a film, null when unknown.</param>
/// <param name="Season">Season number declared by the source, if any.</param>
/// <param name="Country">ISO 3166-1 alpha-2 country of the channel that airs it, used to prefer works from that country.</param>
/// <param name="Corroborated">How many other sources describe this same airing with an equivalent title.</param>
/// <param name="AlternateTitles">Other titles the other sources use for it, tried as extra search queries.</param>
public sealed record UncachedProgrammeItem(
    string Title,
    string? Description = null,
    string? Category = null,
    int? Year = null,
    string? FallbackTitle = null,
    string? ExpectedMediaType = null,
    int? Season = null,
    string? Country = null,
    int Corroborated = 0,
    System.Collections.Generic.IReadOnlyList<string>? AlternateTitles = null);
