namespace Jellyfin.Plugin.JellyTV.Controllers;

/// <summary>
/// A proxy to test from the server, with the address to download through it.
/// </summary>
/// <param name="Host">Host or IP of the proxy.</param>
/// <param name="Port">Port of the proxy.</param>
/// <param name="UseHttps">Whether the proxy speaks TLS.</param>
/// <param name="Username">User of the proxy, if it asks for one.</param>
/// <param name="Password">Password of the proxy, if it asks for one.</param>
/// <param name="Url">Address to download through the proxy; empty only tests the connection.</param>
public sealed record ProxyTestRequest(string? Host, int Port, bool UseHttps, string? Username, string? Password, string? Url);
