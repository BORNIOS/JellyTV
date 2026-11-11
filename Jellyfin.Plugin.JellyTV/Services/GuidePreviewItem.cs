using System;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Un programa de la vista previa de una guía.</summary>
/// <param name="Start">Inicio de la emisión.</param>
/// <param name="Stop">Fin de la emisión.</param>
/// <param name="Title">Título.</param>
internal sealed record GuidePreviewItem(DateTimeOffset Start, DateTimeOffset Stop, string Title);
