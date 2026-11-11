using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Aprovecha que varias fuentes describan la misma emisión: completa en el programa principal lo que le falta con lo que
/// traen las demás y devuelve cuántas fuentes coinciden y qué otros títulos usan, que sirven como evidencia y como
/// consultas alternativas al enriquecer.
/// </summary>
internal static class EpgConsolidator
{
    private const int ShortDescription = 40;

    /// <summary>
    /// Completa un programa con lo que dicen las otras fuentes.
    /// </summary>
    /// <param name="item">Programa principal, con las coincidencias de las demás fuentes en <see cref="GuideItem.Related"/>.</param>
    /// <returns>El XML completado, cuántas fuentes más coinciden y los títulos distintos que usan.</returns>
    internal static (string Xml, int Corroborated, IReadOnlyList<string> AlternateTitles) Consolidate(GuideItem item)
    {
        if (item.Related is not { Count: > 0 } related)
        {
            return (item.Xml, 0, Array.Empty<string>());
        }

        XElement primary;
        try
        {
            primary = XElement.Parse(item.Xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return (item.Xml, related.Count, Array.Empty<string>());
        }

        var changed = false;
        var ownTitle = primary.Element("title")?.Value.Trim() ?? string.Empty;
        var alternates = new List<string>();
        foreach (var other in related)
        {
            XElement element;
            try
            {
                element = XElement.Parse(other.Xml, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var title = element.Element("title")?.Value.Trim() ?? string.Empty;
            if (title.Length > 0 && !string.Equals(title, ownTitle, StringComparison.OrdinalIgnoreCase) && !alternates.Contains(title, StringComparer.OrdinalIgnoreCase))
            {
                alternates.Add(title);
            }

            // Sinopsis: la de la otra fuente solo reemplaza a una ausente o claramente más pobre.
            var otherDesc = element.Elements("desc").Select(static d => d.Value.Trim()).FirstOrDefault(static d => d.Length > 0);
            var ownDescElement = primary.Elements("desc").FirstOrDefault();
            var ownDesc = ownDescElement?.Value.Trim() ?? string.Empty;
            if (otherDesc is not null && ownDesc.Length < ShortDescription && otherDesc.Length > ownDesc.Length)
            {
                if (ownDescElement is null)
                {
                    primary.Add(new XElement("desc", new XAttribute("lang", "es"), otherDesc));
                }
                else
                {
                    ownDescElement.Value = otherDesc;
                }

                changed = true;
            }

            changed |= CopyIfMissing(primary, element, "date");
            changed |= CopyIfMissing(primary, element, "category");
            changed |= CopyIfMissing(primary, element, "episode-num");
            changed |= CopyIfMissing(primary, element, "rating");
            changed |= CopyIfMissing(primary, element, "icon");
        }

        return (changed ? primary.ToString(SaveOptions.DisableFormatting) : item.Xml, related.Count, alternates);
    }

    private static bool CopyIfMissing(XElement target, XElement source, string name)
    {
        if (target.Element(name) is not null || source.Element(name) is null)
        {
            return false;
        }

        foreach (var element in source.Elements(name))
        {
            target.Add(new XElement(element));
        }

        return true;
    }
}
