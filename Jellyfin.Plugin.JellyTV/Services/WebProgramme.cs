using System;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Un programa extraído de la web oficial de un canal.</summary>
/// <param name="Start">Inicio de la emisión.</param>
/// <param name="Stop">Fin de la emisión, si la web lo declara.</param>
/// <param name="Title">Título.</param>
/// <param name="Description">Sinopsis, si la hay.</param>
/// <param name="Image">URL de una imagen, si la hay.</param>
internal sealed record WebProgramme(DateTimeOffset Start, DateTimeOffset? Stop, string Title, string? Description, string? Image);
