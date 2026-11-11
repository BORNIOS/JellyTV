using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Códecs reales de cada fuente, aprendidos del <c>CODECS=</c> de su master HLS (al abrir el canal o en el health check).
/// Jellyfin decide Direct Play, remux o transcode con lo que declara el MediaSource: si se declara AAC y la fuente es AC-3, el
/// navegador intenta Direct Play, hls.js la rechaza al instante y Jellyfin reintenta re-codificando el vídeo entero.
/// </summary>
internal static class SourceCodecs
{
    private static readonly ConcurrentDictionary<string, Codecs> ByUrl = new(StringComparer.Ordinal);

    /// <summary>Códecs conocidos de una fuente, o <c>null</c> si aún no se han visto.</summary>
    /// <param name="sourceUrl">URL de la fuente tal como está configurada.</param>
    /// <returns>Códecs o <c>null</c>.</returns>
    internal static Codecs? Get(string sourceUrl) => ByUrl.TryGetValue(sourceUrl, out var codecs) ? codecs : null;

    /// <summary>Aprende los códecs de un master HLS (la primera variante que declara <c>CODECS</c>).</summary>
    /// <param name="sourceUrl">URL de la fuente configurada (clave).</param>
    /// <param name="masterPlaylist">Contenido del master.</param>
    internal static void RecordFromMaster(string sourceUrl, string masterPlaylist)
    {
        var line = masterPlaylist.Split('\n').FirstOrDefault(static l =>
            l.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) && l.Contains("CODECS=", StringComparison.OrdinalIgnoreCase));
        var match = line is null ? null : Regex.Match(line, "CODECS=\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match is { Success: true } && Parse(match.Groups[1].Value) is { } codecs)
        {
            ByUrl[sourceUrl] = codecs;
        }
    }

    /// <summary>Traduce una cadena CODECS de HLS a nombres de Jellyfin.</summary>
    /// <param name="codecsAttribute">Valor de CODECS, p. ej. <c>avc1.640028,ac-3</c>.</param>
    /// <returns>Códecs, o <c>null</c> si no hay vídeo ni audio reconocibles.</returns>
    internal static Codecs? Parse(string codecsAttribute)
    {
        string? video = null;
        string? audio = null;
        string? profile = null;
        foreach (var token in codecsAttribute.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.ToLowerInvariant();
            if (t.StartsWith("avc1", StringComparison.Ordinal) || t.StartsWith("avc3", StringComparison.Ordinal))
            {
                video = "h264";
                profile = t.Length >= 7 ? t[5..7] switch { "64" => "High", "4d" => "Main", "42" => "Baseline", _ => null } : null;
            }
            else if (t.StartsWith("hvc1", StringComparison.Ordinal) || t.StartsWith("hev1", StringComparison.Ordinal))
            {
                video = "hevc";
            }
            else if (t.StartsWith("av01", StringComparison.Ordinal))
            {
                video = "av1";
            }
            else if (t.StartsWith("vp09", StringComparison.Ordinal))
            {
                video = "vp9";
            }
            else if (t.StartsWith("mp4a.40.34", StringComparison.Ordinal) || t.StartsWith("mp4a.6b", StringComparison.Ordinal))
            {
                audio = "mp3";
            }
            else if (t.StartsWith("mp4a", StringComparison.Ordinal))
            {
                audio = "aac";
            }
            else if (t is "ac-3")
            {
                audio = "ac3";
            }
            else if (t is "ec-3")
            {
                audio = "eac3";
            }
            else if (t is "opus" or "flac")
            {
                audio = t;
            }
        }

        // Si la lista solo trae uno de los dos, el otro se asume como siempre (h264 / aac).
        return video is null && audio is null ? null : new Codecs(video ?? "h264", audio ?? "aac", profile);
    }

    /// <summary>Códecs normalizados con los nombres que usa Jellyfin.</summary>
    /// <param name="Video">Códec de vídeo (h264, hevc, av1, vp9).</param>
    /// <param name="Audio">Códec de audio (aac, ac3, eac3, mp3, opus, flac).</param>
    /// <param name="VideoProfile">High/Main/Baseline cuando es H.264 (<c>null</c> si no se conoce).</param>
    internal sealed record Codecs(string Video, string Audio, string? VideoProfile);
}
