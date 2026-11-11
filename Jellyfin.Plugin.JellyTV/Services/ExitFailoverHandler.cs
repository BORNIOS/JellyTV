using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Sends each request through the proxies of one country, in the order that stream always uses them. When a proxy cannot
/// be reached the request moves on to the next one of the same country instead of failing, so a dead proxy in the pool
/// costs one short attempt and is then left alone for a few minutes.
/// </summary>
internal sealed class ExitFailoverHandler : HttpMessageHandler
{
    private readonly ProxyPoolService _pool;
    private readonly string _country;
    private readonly string _stickyKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExitFailoverHandler"/> class.
    /// </summary>
    /// <param name="pool">Pool the proxies come from.</param>
    /// <param name="country">Country the requests must exit through.</param>
    /// <param name="stickyKey">Stable identifier of the stream, which decides which proxy it starts with.</param>
    public ExitFailoverHandler(ProxyPoolService pool, string country, string stickyKey)
    {
        _pool = pool;
        _country = country;
        _stickyKey = stickyKey;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var candidates = _pool.GetCandidates(_country, _stickyKey);
        if (candidates.Count == 0)
        {
            throw new HttpRequestException($"No hay un proxy disponible para la salida {_country}.", null, HttpStatusCode.BadGateway);
        }

        Exception? last = null;
        foreach (var (key, client) in candidates)
        {
            // A request can only be sent once, so each attempt goes out as a copy.
            var attempt = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers)
            {
                attempt.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            try
            {
                var response = await client.SendAsync(attempt, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                _pool.MarkOk(key);
                return response;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null)
            {
                // The proxy (or the way to it) failed: try the next one of the same country.
                last = ex;
                _pool.MarkFailed(key);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The proxy took too long to answer.
                last = ex;
                _pool.MarkFailed(key);
            }
        }

        throw new HttpRequestException($"Ningún proxy de la salida {_country} respondió.", last, HttpStatusCode.BadGateway);
    }
}
