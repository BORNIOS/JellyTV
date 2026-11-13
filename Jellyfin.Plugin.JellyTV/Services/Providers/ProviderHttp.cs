using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Peticiones HTTP de los proveedores: un navegador de escritorio normal, con tiempo máximo y sin seguir más lógica que la del proveedor.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>Navegador con el que se presenta el plugin ante los proveedores.</summary>
    internal const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Descarga un texto (JSON o HTML) con las cabeceras indicadas.</summary>
    /// <param name="client">Cliente por el que se pregunta.</param>
    /// <param name="url">Dirección.</param>
    /// <param name="headers">Cabeceras adicionales, o <c>null</c>.</param>
    /// <param name="method">Método HTTP.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>El cuerpo de la respuesta.</returns>
    internal static async Task<string> SendAsync(HttpClient client, string url, IReadOnlyDictionary<string, string>? headers, HttpMethod method, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Envía un formulario (<c>application/x-www-form-urlencoded</c>) con POST y devuelve el texto de la respuesta.</summary>
    /// <param name="client">Cliente por el que se pregunta.</param>
    /// <param name="url">Dirección.</param>
    /// <param name="form">Campos ya codificados, como <c>a=1&amp;b=2</c>.</param>
    /// <param name="headers">Cabeceras adicionales, o <c>null</c>.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>El cuerpo de la respuesta.</returns>
    internal static async Task<string> PostFormAsync(HttpClient client, string url, string form, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Descarga un texto con GET.</summary>
    /// <param name="client">Cliente por el que se pregunta.</param>
    /// <param name="url">Dirección.</param>
    /// <param name="headers">Cabeceras adicionales, o <c>null</c>.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    /// <returns>El cuerpo de la respuesta.</returns>
    internal static Task<string> GetAsync(HttpClient client, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        => SendAsync(client, url, headers, HttpMethod.Get, cancellationToken);
}
