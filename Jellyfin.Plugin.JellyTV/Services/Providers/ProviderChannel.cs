namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Canal que ofrece el catálogo de un proveedor FAST.
/// </summary>
/// <param name="Id">Id del canal en el proveedor; es también el id del canal en su guía (el tvg-id).</param>
/// <param name="Name">Nombre del canal.</param>
/// <param name="Number">Número de canal del proveedor, si lo tiene.</param>
/// <param name="Logo">Dirección del logo.</param>
/// <param name="Group">Categoría del canal en el proveedor.</param>
/// <param name="Description">Descripción del canal.</param>
/// <param name="Country">País del catálogo (el que el proveedor dedujo de la IP), si lo declara.</param>
internal sealed record ProviderChannel(
    string Id,
    string Name,
    int? Number,
    string? Logo,
    string? Group,
    string? Description,
    string? Country);
