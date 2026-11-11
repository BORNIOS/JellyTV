namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Clasificación del tipo de canal, derivada de su nombre/categoría (group-title). Se usa para
/// dar contexto a la EPG: si un canal es deportivo, sus programas llevan categoría sports y no
/// se deja que TMDB los reclasifique como movie/series.
/// </summary>
internal enum ChannelKind
{
    /// <summary>Canal general / entretenimiento (sin perfil claro).</summary>
    General = 0,

    /// <summary>Canal deportivo (Fox Sports, ESPN, TDN, etc.).</summary>
    Sports = 1,

    /// <summary>Canal de noticias (CNN, Milenio, Foro TV, etc.).</summary>
    News = 2,

    /// <summary>Canal infantil (Nick, Cartoon, Disney, etc.).</summary>
    Kids = 3,

    /// <summary>Canal de cine / películas.</summary>
    Movies = 4
}
