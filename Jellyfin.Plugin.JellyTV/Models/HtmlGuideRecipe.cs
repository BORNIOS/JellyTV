namespace Jellyfin.Plugin.JellyTV.Models;

/// <summary>
/// Receta para leer la programación de una web HTML: no es código de un sitio, es un conjunto de datos que cualquiera
/// puede escribir o compartir. Se aplica a las páginas cuyo dominio coincide con <see cref="Host"/>.
/// </summary>
public class HtmlGuideRecipe
{
    /// <summary>Gets or sets el nombre con el que se muestra la receta.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets el dominio al que se aplica (por ejemplo <c>gatotv.com</c>); también vale para sus subdominios.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets cómo se construye la URL de cada día a partir de la que el usuario pegó. <c>{url}</c> es esa URL sin
    /// barra final y <c>{date:formato}</c> el día. Sin marcador de fecha solo se pide una página.
    /// </summary>
    public string DayUrl { get; set; } = "{url}";

    /// <summary>
    /// Gets or sets una expresión regular opcional que se aplica a la URL que el usuario pegó. Sus grupos con nombre quedan
    /// disponibles en <see cref="DayUrl"/> como <c>{nombre}</c>. Sirve para sitios cuya página de canal carga la programación
    /// desde otra dirección que se puede deducir de la primera.
    /// </summary>
    public string? UrlFrom { get; set; }

    /// <summary>
    /// Gets or sets la expresión regular que reconoce una fila de programación. Debe llevar los grupos con nombre
    /// <c>start</c> y <c>title</c>, y puede llevar <c>stop</c>, <c>desc</c> e <c>image</c>.
    /// </summary>
    public string Row { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets una expresión regular opcional con el grupo <c>logo</c> para sacar la URL del logo del canal de la
    /// página. Sirve de identidad si el canal no tiene logo.
    /// </summary>
    public string? Logo { get; set; }

    /// <summary>Gets or sets la zona horaria (IANA) en la que están escritas las horas de la página.</summary>
    public string Zone { get; set; } = "UTC";

    /// <summary>
    /// Gets or sets una expresión regular opcional con el grupo <c>zone</c> para leer de la propia página la zona en que muestra
    /// las horas (útil en sitios que la eligen por la IP de quien consulta). Si no encuentra una zona válida se usa <see cref="Zone"/>.
    /// </summary>
    public string? ZoneFrom { get; set; }

    /// <summary>Gets or sets cuántos días, empezando por hoy, se piden cuando la URL cambia por día.</summary>
    public int Days { get; set; } = 3;

    /// <summary>
    /// Gets or sets cuántos días anteriores a hoy se piden además (0 a 2). Hace falta cuando el "día" de la página no empieza a
    /// medianoche (por ejemplo a las 06:00): entre la medianoche y esa hora el programa en emisión está en la página de ayer.
    /// </summary>
    public int PastDays { get; set; }

    /// <summary>Gets or sets la pausa, en milisegundos, entre una página y la siguiente del mismo sitio.</summary>
    public int DelayMs { get; set; } = 700;
}
