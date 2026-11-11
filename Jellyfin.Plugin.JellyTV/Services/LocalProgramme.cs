using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Un programa de la guía local (parseado del XMLTV fusionado) con su ventana UTC y metadata.
/// Inmutable; lo produce <see cref="LocalEpgReader"/>.
/// </summary>
internal sealed class LocalProgramme
{
    internal LocalProgramme(
        string channel,
        DateTime startUtc,
        DateTime endUtc,
        string title,
        string? description,
        IReadOnlyList<string> categories,
        string? posterUrl,
        string? backdropUrl,
        string? rating)
    {
        Channel = channel;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Title = title;
        Description = description;
        Categories = categories;
        PosterUrl = posterUrl;
        BackdropUrl = backdropUrl;
        Rating = rating;
    }

    /// <summary>Gets id de canal XMLTV (tvg-id).</summary>
    internal string Channel { get; }

    /// <summary>Gets inicio en UTC.</summary>
    internal DateTime StartUtc { get; }

    /// <summary>Gets fin en UTC.</summary>
    internal DateTime EndUtc { get; }

    /// <summary>Gets título (se prefiere el idioma no-inglés).</summary>
    internal string Title { get; }

    /// <summary>Gets descripción (overview), si existe.</summary>
    internal string? Description { get; }

    /// <summary>Gets categorías del programa (para clasificación y géneros).</summary>
    internal IReadOnlyList<string> Categories { get; }

    /// <summary>Gets URL de la imagen vertical (póster), la que Jellyfin usa como imagen principal.</summary>
    internal string? PosterUrl { get; }

    /// <summary>Gets URL de la imagen horizontal (fondo), la que Jellyfin usa en las filas apaisadas.</summary>
    internal string? BackdropUrl { get; }

    /// <summary>Gets rating oficial (p. ej. TV-14, AA), si existe.</summary>
    internal string? Rating { get; }
}
