using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Normalizes programme titles before TMDB matching.
/// </summary>
public interface ITmdbNormalizationService
{
    /// <summary>
    /// Normalizes a title by removing accents, punctuation and noise tokens.
    /// </summary>
    /// <param name="title">Raw title.</param>
    /// <returns>The normalized title.</returns>
    string NormalizeTitle(string title);
}
