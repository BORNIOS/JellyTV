using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyTV.Services;

namespace Jellyfin.Plugin.JellyTV.Services.Tmdb;

/// <summary>
/// Un &lt;programme&gt; filtrado de una fuente XMLTV con su XML crudo (editable), metadatos
/// extraídos y el contexto del canal al que pertenece. <see cref="Xml"/> lo reescribe
/// <c>TmdbEnrichmentService.EnrichXmlProgrammesAsync</c> (categorías por tipo de canal y, si
/// aplica, inyección TMDB de imágenes/metadata).
/// </summary>
internal sealed class XmlProgrammeRecord
{
    internal XmlProgrammeRecord(string xml, string channel, string start, string? stop, EpgProgrammeData data, ChannelKind kind, string channelName, string? country = null)
    {
        Country = country;
        Xml = xml;
        Channel = channel;
        Start = start;
        Stop = stop;
        Data = data;
        Kind = kind;
        ChannelName = channelName;
    }

    /// <summary>Gets or sets XML serializado del &lt;programme&gt; (puede reescribirse al enriquecer).</summary>
    internal string Xml { get; set; }

    /// <summary>Gets id de canal (atributo channel), usado para deduplicar el merge.</summary>
    internal string Channel { get; }

    /// <summary>Gets atributo start, usado para deduplicar el merge.</summary>
    internal string Start { get; }

    /// <summary>Gets atributo stop del programa (fin de emisión), si estaba presente.</summary>
    internal string? Stop { get; }

    /// <summary>Gets or sets metadatos ricos extraídos del fragmento.</summary>
    internal EpgProgrammeData Data { get; set; }

    /// <summary>Gets tipo del canal (sports/news/kids/movies/general).</summary>
    internal ChannelKind Kind { get; }

    /// <summary>Gets or sets cuántas otras fuentes describen esta misma emisión con un título equivalente.</summary>
    internal int Corroborated { get; set; }

    /// <summary>Gets or sets los otros títulos con los que las demás fuentes llaman a este programa.</summary>
    internal IReadOnlyList<string> AlternateTitles { get; set; } = Array.Empty<string>();

    /// <summary>Gets país del canal (ISO 3166-1 alfa-2), si el usuario lo definió.</summary>
    internal string? Country { get; }

    /// <summary>Gets nombre del canal (para contexto/placeholder).</summary>
    internal string ChannelName { get; }
}
