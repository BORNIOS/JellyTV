namespace Jellyfin.Plugin.JellyTV.Controllers;

/// <summary>Petición para comprobar una dirección de guía.</summary>
public sealed class GuideTestRequest
{
    /// <summary>Gets or sets la dirección de la guía (admite el marcador de fecha).</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets el nombre del canal, usado para elegir canal si la guía trae varios.</summary>
    public string? Name { get; set; }
}
