using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JellyTV.Services.Tmdb;

public interface ITmdbNormalizationService
{
    string NormalizeTitle(string title);
}

public sealed class TmdbNormalizationService : ITmdbNormalizationService
{
    private static readonly Regex MultiSpaceRe = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex BracketedRe = new("\\([^)]*\\)|\\[[^]]*\\]|\\{[^}]*\\}", RegexOptions.Compiled);
    private static readonly Regex EpisodeRe = new(@"\b(s\d{1,2}e\d{1,3}|season\s*\d{1,2}|temporada\s*\d{1,2}|episode\s*\d{1,3}|episodio\s*\d{1,3}|capitulo\s*\d{1,3}|cap\.?\s*\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearRe = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumRe = new("[^a-z0-9\\s]", RegexOptions.Compiled);
    private static readonly Regex AmpRe = new(@"\s*&\s*", RegexOptions.Compiled);
    private static readonly Regex SubtitleSepRe = new(@"\s*[:\-\|]\s*", RegexOptions.Compiled);
    private static readonly Regex RomanWordRe = new(@"\b(?=[mdclxvi]+\b)m{0,4}(cm|cd|d?c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var s = title.Trim();
        s = BracketedRe.Replace(s, " ");
        s = EpisodeRe.Replace(s, " ");
        s = YearRe.Replace(s, " ");
        s = AmpRe.Replace(s, " and ");
        s = SubtitleSepRe.Replace(s, " ");

        s = RemoveDiacritics(s).ToLowerInvariant();
        s = RomanWordRe.Replace(s, m => RomanToArabicSafe(m.Value));
        s = NonAlphaNumRe.Replace(s, " ");
        s = MultiSpaceRe.Replace(s, " ").Trim();

        return s.Length > 220 ? s[..220] : s;
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string RomanToArabicSafe(string input)
    {
        var value = RomanToArabic(input);
        if (value <= 0 || value > 40) return input;
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static int RomanToArabic(string roman)
    {
        int total = 0;
        int prev = 0;
        foreach (var c in roman.ToUpperInvariant())
        {
            int cur = c switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                'D' => 500,
                'M' => 1000,
                _ => 0
            };

            if (cur == 0) return 0;
            total += cur > prev ? cur - (2 * prev) : cur;
            prev = cur;
        }

        return total;
    }
}
