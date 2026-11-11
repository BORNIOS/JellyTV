using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

internal sealed class TmdbClient : ITmdbClient
{
    private const string TmdbBase = "https://api.themoviedb.org/3";

    private static readonly JsonSerializerOptions JsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<HttpStatusCode> Retryable =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    ];

    private readonly HttpClient _client;
    private readonly ITmdbRateLimiter _rateLimiter;
    private readonly ILogger _logger;

    public TmdbClient(HttpClient client, ITmdbRateLimiter rateLimiter, ILogger logger)
    {
        _client = client;
        _rateLimiter = rateLimiter;
        _logger = logger;
        Metrics = new TmdbClientMetrics();
    }

    public TmdbClientMetrics Metrics { get; }

    public Task<TmdbSearchResponse?> SearchMultiAsync(string apiKey, string query, string language, CancellationToken ct)
    {
        var url = $"{TmdbBase}/search/multi?{TmdbAuth.Query(apiKey)}&query={Uri.EscapeDataString(query)}&language={language}&include_adult=false&page=1";
        return SendWithRetryAsync<TmdbSearchResponse>(url, apiKey, ct);
    }

    public Task<TmdbDetailResponse?> GetDetailAsync(string apiKey, string mediaType, int id, string language, CancellationToken ct)
    {
        var append = "credits,images,external_ids,videos,release_dates,content_ratings,keywords,translations,alternative_titles";
        var url = $"{TmdbBase}/{mediaType}/{id}?{TmdbAuth.Query(apiKey)}&language={language}&append_to_response={append}";
        return SendWithRetryAsync<TmdbDetailResponse>(url, apiKey, ct);
    }

    private async Task<T?> SendWithRetryAsync<T>(string url, string credential, CancellationToken ct)
        where T : class
    {
        const int maxAttempts = 5;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _rateLimiter.WaitTurnAsync(ct).ConfigureAwait(false);

            try
            {
                Metrics.IncrementRequests();
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.ParseAdd("application/json");
                TmdbAuth.Apply(req, credential);

                using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    return await resp.Content.ReadFromJsonAsync<T>(JsonCaseInsensitive, ct).ConfigureAwait(false);
                }

                if (!Retryable.Contains(resp.StatusCode) || attempt == maxAttempts)
                {
                    return null;
                }

                var delay = ComputeBackoffWithJitter(attempt);
                Metrics.IncrementRetries();
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[JellyTV] TMDB retry attempt={Attempt} status={Status} waitMs={Delay}", attempt, (int)resp.StatusCode, (int)delay.TotalMilliseconds);
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) when (attempt < maxAttempts)
            {
                var delay = ComputeBackoffWithJitter(attempt);
                Metrics.IncrementRetries();
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch
            {
                if (attempt == maxAttempts)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private TimeSpan ComputeBackoffWithJitter(int attempt)
    {
        var baseMs = Math.Min(6000, 250 * Math.Pow(2, attempt - 1));
        var jitterMs = Random.Shared.Next(50, 350);
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }
}
