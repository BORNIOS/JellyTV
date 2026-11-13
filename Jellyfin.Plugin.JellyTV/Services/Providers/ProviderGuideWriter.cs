using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Jellyfin.Plugin.JellyTV.Services.Providers;

/// <summary>
/// Escribe en XMLTV la guía de un proveedor, para que pase por el mismo flujo de sincronización que cualquier otra fuente.
/// </summary>
internal static class ProviderGuideWriter
{
    private static readonly Regex InvalidXmlChars = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F￾￿]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Escribe el archivo XMLTV.</summary>
    /// <param name="path">Archivo de destino.</param>
    /// <param name="channels">Canales de la guía.</param>
    /// <param name="programmes">Programas de esos canales.</param>
    internal static void Write(string path, IReadOnlyList<ProviderChannel> channels, IEnumerable<ProviderProgramme> programmes)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, Async = false };
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("tv");
        writer.WriteAttributeString("generator-info-name", "JellyTV");

        foreach (var channel in channels)
        {
            writer.WriteStartElement("channel");
            writer.WriteAttributeString("id", channel.Id);
            writer.WriteElementString("display-name", Clean(channel.Name));
            if (!string.IsNullOrWhiteSpace(channel.Logo))
            {
                writer.WriteStartElement("icon");
                writer.WriteAttributeString("src", channel.Logo);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        foreach (var programme in programmes.OrderBy(static p => p.ChannelId, StringComparer.Ordinal).ThenBy(static p => p.Start))
        {
            writer.WriteStartElement("programme");
            writer.WriteAttributeString("start", Format(programme.Start));
            writer.WriteAttributeString("stop", Format(programme.Stop));
            writer.WriteAttributeString("channel", programme.ChannelId);
            writer.WriteElementString("title", Clean(programme.Title));
            WriteIfAny(writer, "sub-title", programme.SubTitle);
            WriteIfAny(writer, "desc", programme.Description);
            WriteIfAny(writer, "category", programme.Category);
            if (!string.IsNullOrWhiteSpace(programme.Image))
            {
                writer.WriteStartElement("icon");
                writer.WriteAttributeString("src", programme.Image);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static string Format(DateTimeOffset moment)
        => moment.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private static string Clean(string text) => InvalidXmlChars.Replace(text, string.Empty);

    private static void WriteIfAny(XmlWriter writer, string element, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            writer.WriteElementString(element, Clean(text));
        }
    }
}
