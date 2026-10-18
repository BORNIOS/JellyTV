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
    internal XmlProgrammeRecord(string xml, string channel, string start, EpgProgrammeData data, ChannelKind kind, string channelName)
    {
        Xml = xml;
        Channel = channel;
        Start = start;
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

    /// <summary>Gets metadatos ricos extraídos del fragmento.</summary>
    internal EpgProgrammeData Data { get; }

    /// <summary>Gets tipo del canal (sports/news/kids/movies/general).</summary>
    internal ChannelKind Kind { get; }

    /// <summary>Gets nombre del canal (para contexto/placeholder).</summary>
    internal string ChannelName { get; }
}
