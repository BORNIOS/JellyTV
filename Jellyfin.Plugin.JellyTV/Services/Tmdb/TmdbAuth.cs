using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// TMDB gives two credentials: the v3 API key (query string) and the v4 read access token (a JWT sent as
/// a Bearer header). The settings page accepts either one.
/// </summary>
internal static class TmdbAuth
{
    /// <summary>
    /// Gets the query-string fragment that authenticates a request.
    /// </summary>
    /// <param name="credential">API key or read access token.</param>
    /// <returns><c>api_key=…</c> for a v3 key; a neutral parameter for a v4 token (sent as header).</returns>
    public static string Query(string credential) =>
        IsReadToken(credential) ? "v=4" : "api_key=" + Uri.EscapeDataString(credential);

    /// <summary>
    /// Adds the Bearer header when the credential is a v4 read access token.
    /// </summary>
    /// <param name="request">Request to authenticate.</param>
    /// <param name="credential">API key or read access token.</param>
    public static void Apply(HttpRequestMessage request, string credential)
    {
        if (IsReadToken(credential))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
    }

    private static bool IsReadToken(string credential) =>
        credential.StartsWith("eyJ", StringComparison.Ordinal) || credential.Length > 60;
}
