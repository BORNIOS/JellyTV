using System;
using System.Buffers.Binary;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Lee el ancho y el alto de una imagen mirando solo su cabecera (JPEG, PNG, GIF y WebP), para saber si es vertical
/// (póster) u horizontal (fondo) sin descargarla entera.
/// </summary>
internal static class ImageSize
{
    /// <summary>
    /// Intenta leer las dimensiones de una imagen a partir de sus primeros bytes.
    /// </summary>
    /// <param name="data">Primeros bytes del archivo.</param>
    /// <param name="width">Ancho en píxeles.</param>
    /// <param name="height">Alto en píxeles.</param>
    /// <returns><c>true</c> si se reconoció el formato y se leyeron las dimensiones.</returns>
    internal static bool TryRead(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // PNG: firma de 8 bytes y chunk IHDR con ancho y alto de 4 bytes.
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            return width > 0 && height > 0;
        }

        // GIF: "GIF8" y dimensiones de 2 bytes little-endian.
        if (data.Length >= 10 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
            return width > 0 && height > 0;
        }

        // WebP: RIFF....WEBP y uno de los tres formatos de cabecera.
        if (data.Length >= 30 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return TryReadWebP(data, out width, out height);
        }

        // JPEG: se recorren los marcadores hasta el SOF, que trae alto y ancho.
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < data.Length)
            {
                if (data[i] != 0xFF)
                {
                    i++;
                    continue;
                }

                var marker = data[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }

                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 7, 2));
                    return width > 0 && height > 0;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(i + 2, 2));
                i += 2 + length;
            }
        }

        return false;
    }

    private static bool TryReadWebP(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        var kind = data.Slice(12, 4);
        if (kind[0] == (byte)'V' && kind[1] == (byte)'P' && kind[2] == (byte)'8' && kind[3] == (byte)' ')
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3FFF;
        }
        else if (kind[3] == (byte)'L')
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
        }
        else if (kind[3] == (byte)'X')
        {
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
        }

        return width > 0 && height > 0;
    }
}
