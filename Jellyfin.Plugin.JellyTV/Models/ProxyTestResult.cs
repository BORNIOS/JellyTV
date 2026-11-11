namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Outcome of testing a proxy from the server itself, which is where the streams are fetched from.
/// </summary>
/// <param name="Ok">Whether the proxy did what the test asked of it.</param>
/// <param name="Status">HTTP status of the test request, when it got that far.</param>
/// <param name="Milliseconds">How long the test took.</param>
/// <param name="Message">Plain-language explanation of the outcome.</param>
public sealed record ProxyTestResult(bool Ok, int? Status, long Milliseconds, string Message);
