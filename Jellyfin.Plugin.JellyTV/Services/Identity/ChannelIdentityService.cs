using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Identity;

/// <summary>
/// Finds the identity of a channel in the public iptv-org database (also covers web and FAST channels) and uses it
/// to fill what the channel lacks: country, guide id (tvg-id), logo and website. Nothing a person typed is overwritten.
/// </summary>
public sealed class ChannelIdentityService : IDisposable
{
    /// <summary>Minimum score for the automatic enrichment to apply a candidate without asking.</summary>
    public const double AutoApplyScore = 0.95;

    // Countries preferred when a name exists in several and the caller does not narrow it down.
    private static readonly string[] DefaultCountries = ["US", "MX", "ES", "GB"];

    private readonly IptvOrgCatalog _catalog;
    private readonly ILogger<ChannelIdentityService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelIdentityService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public ChannelIdentityService(IHttpClientFactory httpClientFactory, ILogger<ChannelIdentityService> logger)
    {
        _logger = logger;
        _catalog = new IptvOrgCatalog(httpClientFactory, logger);
    }

    /// <inheritdoc/>
    public void Dispose() => _catalog.Dispose();

    /// <summary>
    /// Copies a candidate into a channel, filling only what the channel does not have yet.
    /// </summary>
    /// <param name="channel">Channel to update.</param>
    /// <param name="candidate">Chosen identity.</param>
    /// <param name="overwriteLogo">Whether an existing logo is replaced.</param>
    /// <param name="confirmed">Whether a person confirmed the identity.</param>
    public static void Apply(ChannelEntry channel, IdentityCandidate candidate, bool overwriteLogo, bool confirmed)
    {
        channel.Identity = new ChannelIdentity
        {
            IptvOrgId = candidate.IptvOrgId,
            Name = candidate.Name,
            Country = candidate.Country,
            Website = candidate.Website,
            Confirmed = confirmed
        };

        if (!string.IsNullOrWhiteSpace(candidate.LogoUrl) && (overwriteLogo || string.IsNullOrWhiteSpace(channel.LogoUrl)))
        {
            channel.LogoUrl = candidate.LogoUrl;
        }

        if (string.IsNullOrWhiteSpace(channel.TvgId) && !string.IsNullOrWhiteSpace(candidate.IptvOrgId))
        {
            channel.TvgId = candidate.IptvOrgId;
        }

        if (string.IsNullOrWhiteSpace(channel.Country) && !string.IsNullOrWhiteSpace(candidate.Country))
        {
            channel.Country = candidate.Country;
        }
    }

    /// <summary>
    /// The iptv-org channel behind an id (for the guide mapping), or <c>null</c> when it is unknown.
    /// </summary>
    /// <param name="id">iptv-org id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The channel, or null.</returns>
    internal Task<IptvOrgChannel?> GetByIdAsync(string id, CancellationToken cancellationToken) => _catalog.GetByIdAsync(id, cancellationToken);

    /// <summary>
    /// Searches the identity of a channel by name.
    /// </summary>
    /// <param name="query">Channel name.</param>
    /// <param name="country">Optional country code that narrows the search (for example MX).</param>
    /// <param name="tvgId">Optional guide id of the channel; an exact iptv-org id wins over name similarity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidates ordered from the best match.</returns>
    public async Task<IReadOnlyList<IdentityCandidate>> SearchAsync(string query, string? country, string? tvgId, CancellationToken cancellationToken)
    {
        var result = new List<IdentityCandidate>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        try
        {
            var code = string.IsNullOrWhiteSpace(country) ? null : country.Trim();
            var guideId = (tvgId ?? string.Empty).Split('@')[0].Trim();
            foreach (var channel in await _catalog.FindAsync(query, tvgId, cancellationToken).ConfigureAwait(false))
            {
                if (code is not null && !string.Equals(channel.Country, code, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var exactId = guideId.Length > 0 && string.Equals(channel.Id, guideId, StringComparison.OrdinalIgnoreCase);

                // La coincidencia exacta de tvg-id gana a cualquier parecido de nombre (tope 0.99).
                var score = exactId ? 1.0 : Math.Min(0.99, channel.AltNames.Append(channel.Name).Max(n => NameMatcher.Similarity(query, n)));
                if (score < 0.6)
                {
                    continue;
                }

                result.Add(new IdentityCandidate
                {
                    Name = channel.Name,
                    IptvOrgId = channel.Id,
                    TvgId = channel.Id,
                    Country = channel.Country,
                    Website = channel.Website,
                    LogoUrl = channel.Logo,
                    Score = score,
                    Sources = "iptv-org"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("[JellyTV] iptv-org search failed: {Message}", ex.Message);
        }

        // Sin país, los mercados principales primero; así el empate entre "ESPN.us" y "ESPN.au" se resuelve igual siempre.
        return result
            .OrderByDescending(static c => c.Score)
            .ThenBy(static c => Array.IndexOf(DefaultCountries, c.Country?.ToUpperInvariant()) is var i and >= 0 ? i : int.MaxValue)
            .Take(8)
            .ToList();
    }

    /// <summary>
    /// Completes the identity, country, guide id and logo of channels that lack them, applying only confident matches.
    /// </summary>
    /// <param name="channels">Channels to enrich; modified in place.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many channels were updated and how many were skipped.</returns>
    public async Task<(int Updated, int Skipped)> EnrichAsync(IReadOnlyList<ChannelEntry> channels, CancellationToken cancellationToken)
    {
        var updated = 0;
        var skipped = 0;
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var incomplete = channel.Identity is null
                || string.IsNullOrWhiteSpace(channel.LogoUrl)
                || string.IsNullOrWhiteSpace(channel.TvgId)
                || string.IsNullOrWhiteSpace(channel.Country);
            if (channel.Identity?.Confirmed == true || !incomplete)
            {
                skipped++;
                continue;
            }

            var knownCountry = channel.Identity?.Country ?? channel.Country;
            var candidates = await SearchAsync(channel.Name, knownCountry, channel.TvgId, cancellationToken).ConfigureAwait(false);
            var best = candidates.Count > 0 ? candidates[0] : null;

            // "ESPN" existe en varios países: sin país conocido no se adivina cuál es.
            var ambiguous = best is not null
                && string.IsNullOrWhiteSpace(knownCountry)
                && !string.Equals(best.IptvOrgId, (channel.TvgId ?? string.Empty).Split('@')[0].Trim(), StringComparison.OrdinalIgnoreCase)
                && candidates.Skip(1).Any(c => c.Score >= AutoApplyScore
                    && !string.IsNullOrWhiteSpace(c.Country)
                    && !string.IsNullOrWhiteSpace(best.Country)
                    && !string.Equals(c.Country, best.Country, StringComparison.OrdinalIgnoreCase));
            if (best is null || ambiguous || best.Score < AutoApplyScore)
            {
                skipped++;
                continue;
            }

            Apply(channel, best, overwriteLogo: string.IsNullOrWhiteSpace(channel.LogoUrl), confirmed: false);
            updated++;
        }

        return (updated, skipped);
    }
}
