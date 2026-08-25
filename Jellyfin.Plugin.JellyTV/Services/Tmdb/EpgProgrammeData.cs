namespace JellyTV.Services.Tmdb;

/// <summary>
/// Rich metadata extracted inline from a single &lt;programme&gt; element while streaming
/// the XMLTV file.  Populated by <c>EpgFilterService.ParseProgrammeData</c> and consumed
/// by <c>TmdbEnrichmentService.EnrichProgrammesAsync</c> for inline TMDB enrichment.
/// </summary>
internal sealed class EpgProgrammeData
{
    /// <summary>Title in Spanish (or primary EPG language).</summary>
    public string? TitleEs { get; init; }

    /// <summary>
    /// Title in English — preferred for TMDB search because English coverage
    /// is significantly higher and match rates are much better.
    /// </summary>
    public string? TitleEn { get; init; }

    /// <summary>First &lt;desc&gt; element content.</summary>
    public string? Description { get; init; }

    /// <summary>First &lt;category&gt; element content.</summary>
    public string? Category { get; init; }

    /// <summary>Year parsed from the first 4 digits of &lt;date&gt;.</summary>
    public int? Year { get; init; }

    /// <summary>src attribute from &lt;icon&gt;, if present.</summary>
    public string? ExistingIconUrl { get; init; }

    /// <summary>
    /// Best title for TMDB search: English is used when available because TMDB
    /// results are dramatically more accurate with English titles.
    /// Falls back to the primary (Spanish) title.
    /// </summary>
    public string BestTitle =>
        !string.IsNullOrWhiteSpace(TitleEn) ? TitleEn :
        !string.IsNullOrWhiteSpace(TitleEs) ? TitleEs : string.Empty;

    /// <summary>
    /// Secondary title in the other language, used as an additional TMDB search variant.
    /// Only present when both TitleEn and TitleEs are non-empty.
    /// </summary>
    public string? FallbackTitle =>
        !string.IsNullOrWhiteSpace(TitleEn) && !string.IsNullOrWhiteSpace(TitleEs)
            ? TitleEs : null;

    /// <summary>
    /// True when the &lt;icon&gt; URL points to image.tmdb.org, meaning the EPG
    /// provider already enriched this programme from TMDB.
    /// </summary>
    public bool HasTmdbIcon =>
        !string.IsNullOrWhiteSpace(ExistingIconUrl)
        && ExistingIconUrl!.Contains("image.tmdb.org", StringComparison.OrdinalIgnoreCase);
}
