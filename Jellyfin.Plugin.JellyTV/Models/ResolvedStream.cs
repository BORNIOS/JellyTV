using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Models;

public class ResolvedStream
{
    /// <summary>
    /// Gets or sets the original URL provided by the user/Jellyfin.
    /// </summary>
    public string OriginalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the final resolved URL after following all redirects.
    /// </summary>
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL used for resolving relative HLS segment paths.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the content type of the resolved stream.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when this resolution was performed.
    /// </summary>
    public DateTimeOffset ResolvedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the estimated token expiry time, if detected.
    /// </summary>
    public DateTimeOffset? TokenExpiry { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether gets or sets whether this URL points to an HLS playlist.
    /// </summary>
    public bool IsHls { get; set; }

    /// <summary>
    /// Gets or sets if present, the token value extracted from the final URL's query string.
    /// </summary>
    public string? TokenValue { get; set; }

    /// <summary>
    /// Gets or sets the chain of redirects followed to reach the final URL, in order.
    /// </summary>
    public List<string> RedirectChain { get; set; } = new();

    /// <summary>
    /// Gets or sets the URL the BroadcastPump should use for each playlist poll.
    /// For tokenized streams this is the penultimate URL in the redirect chain
    /// (for example, an intermediate redirect node) so that each poll follows a fresh token-generating
    /// redirect rather than re-using a single-use query-string token.
    /// For non-tokenized streams this equals <see cref="FinalUrl"/>.
    /// </summary>
    public string PlaylistPollUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets body text fetched during the resolver's final GET.
    /// Available exactly once: the BroadcastPump consumes it to avoid a second
    /// rate-limited request to a single-use provider token.
    /// </summary>
    public string? InitialPlaylistContent { get; set; }

    /// <summary>
    /// Gets or sets the redirect node responsible for rotating the token.
    /// Detected as the last 3xx hop whose Location URL contains a token signal (<c>?token=</c>,
    /// <c>/hlsr/</c>, or a query string longer than 80 characters).
    /// Diagnostics only — renewal must always start from <see cref="OriginalUrl"/>.
    /// </summary>
    public string? RotationNodeUrl { get; set; }

    /// <summary>
    /// Gets or sets proxy key used for the successful resolve chain (Host:Port), when resolution used proxy.
    /// </summary>
    public string? ResolverProxyKey { get; set; }

    /// <summary>
    /// Gets or sets uRL of the hop whose redirect emitted the token.
    /// </summary>
    public string? TokenGeneratorHopUrl { get; set; }
}
