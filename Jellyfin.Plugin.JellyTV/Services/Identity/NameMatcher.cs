using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.JellyTV.Services.Identity;

/// <summary>
/// Fuzzy comparison of channel and network names ("Fox Sports 2 HD" vs "FOX Sports 2").
/// </summary>
internal static class NameMatcher
{
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "hd", "sd", "fhd", "uhd", "4k", "tv", "canal", "channel", "network", "en", "de", "la", "el", "espanol", "latino", "latam", "mx", "us", "mexico", "the"
    };

    /// <summary>
    /// Lowercases, removes accents and punctuation and drops filler words.
    /// </summary>
    /// <param name="value">Raw name.</param>
    /// <returns>Normalized tokens joined by a space.</returns>
    public static string Normalize(string? value) => string.Join(' ', Tokens(value));

    /// <summary>
    /// Removes a trailing channel number ("Fox Sports 2" becomes "Fox Sports").
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <returns>The name without the trailing number, or the original name when it has none.</returns>
    public static string StripTrailingNumber(string name)
    {
        var trimmed = name.TrimEnd();
        var end = trimmed.Length;
        while (end > 0 && char.IsDigit(trimmed[end - 1]))
        {
            end--;
        }

        return end < trimmed.Length && end > 0 ? trimmed[..end].TrimEnd() : name;
    }

    /// <summary>
    /// Similarity between two names: 1 for the same tokens, high when one contains the other.
    /// </summary>
    /// <param name="a">First name.</param>
    /// <param name="b">Second name.</param>
    /// <returns>A score between 0 and 1.</returns>
    public static double Similarity(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        if (left.SequenceEqual(right, StringComparer.Ordinal))
        {
            return 1;
        }

        var common = left.Intersect(right, StringComparer.Ordinal).Count();
        if (common == 0)
        {
            return 0;
        }

        // Los números distinguen canales ("Fox Sports 2" no es "Fox Sports 3").
        var digitsLeft = left.Where(t => char.IsDigit(t[0])).ToHashSet(StringComparer.Ordinal);
        var digitsRight = right.Where(t => char.IsDigit(t[0])).ToHashSet(StringComparer.Ordinal);
        if (!digitsLeft.SetEquals(digitsRight))
        {
            return 0;
        }

        var dice = 2.0 * common / (left.Count + right.Count);
        var contained = common == Math.Min(left.Count, right.Count) ? 0.85 : 0;
        return Math.Max(dice, contained);
    }

    private static List<string> Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => !Noise.Contains(token))
            .ToList();
    }
}
