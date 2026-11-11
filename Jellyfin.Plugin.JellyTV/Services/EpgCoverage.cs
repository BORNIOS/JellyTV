using System;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>Programación que la guía local tiene de un canal.</summary>
/// <param name="Count">Número de programas.</param>
/// <param name="FirstStartUtc">Inicio del primer programa.</param>
/// <param name="LastEndUtc">Fin del último programa.</param>
internal sealed record EpgCoverage(int Count, DateTime? FirstStartUtc, DateTime? LastEndUtc);
