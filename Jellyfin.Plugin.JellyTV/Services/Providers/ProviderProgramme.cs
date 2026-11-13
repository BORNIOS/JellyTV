using System;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Programa de la guía de un proveedor.
/// </summary>
/// <param name="ChannelId">Id del canal en el proveedor.</param>
/// <param name="Start">Inicio.</param>
/// <param name="Stop">Fin.</param>
/// <param name="Title">Título (la serie, si es un episodio).</param>
/// <param name="SubTitle">Título del episodio.</param>
/// <param name="Description">Sinopsis.</param>
/// <param name="Category">Género.</param>
/// <param name="Image">Imagen del programa.</param>
internal sealed record ProviderProgramme(
    string ChannelId,
    DateTimeOffset Start,
    DateTimeOffset Stop,
    string Title,
    string? SubTitle,
    string? Description,
    string? Category,
    string? Image);
