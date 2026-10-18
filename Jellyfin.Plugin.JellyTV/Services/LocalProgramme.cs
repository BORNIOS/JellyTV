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
        string? iconUrl,
        string? rating)
    {
        Channel = channel;
        StartUtc = startUtc;
        EndUtc = endUtc;
        Title = title;
        Description = description;
        Categories = categories;
        IconUrl = iconUrl;
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

    /// <summary>Gets URL del icono/arte (backdrop o póster inyectado por TMDB).</summary>
    internal string? IconUrl { get; }

    /// <summary>Gets rating oficial (p. ej. TV-14, AA), si existe.</summary>
    internal string? Rating { get; }
}
