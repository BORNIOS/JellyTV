using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Deja en el registro del plugin lo que el cliente pidió y lo que el servidor respondió al negociar la reproducción de un
/// canal (PlaybackInfo y los pedidos de transcodificación), para poder explicar por qué un cliente acaba transcodificando.
/// Solo se escribe para canales de JellyTV; nunca se registran tokens ni cabeceras de autenticación.
/// </summary>
internal static partial class PlaybackDiagnostics
{
    // Parámetros del pedido de transcodificación que explican cómo se arma el stream.
    private static readonly string[] TranscodeQueryKeys =
    [
        "MediaSourceId", "VideoCodec", "AudioCodec", "Container", "SegmentContainer", "AllowVideoStreamCopy",
        "AllowAudioStreamCopy", "DeInterlace", "TranscodeReasons", "EnableAdaptiveBitrateStreaming", "MaxStreamingBitrate",
        "VideoBitRate", "AudioBitRate", "MaxWidth", "MaxHeight", "Static", "Profile", "Level", "RequireAvc", "BreakOnNonKeyFrames"
    ];

    [GeneratedRegex("(Client|Device|Version)=\"([^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationFields();

    /// <summary>Quién hace el pedido (aplicación, dispositivo y versión), sin el token.</summary>
    internal static string Who(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            header = http.Request.Headers["X-Emby-Authorization"].ToString();
        }

        var fields = AuthorizationFields().Matches(header).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var client = fields.GetValueOrDefault("Client") ?? "?";
        var device = fields.GetValueOrDefault("Device") ?? http.Request.Query["DeviceId"].ToString();
        var version = fields.GetValueOrDefault("Version") ?? string.Empty;
        return $"{client} {version} · {device}".Trim();
    }

    /// <summary>Resumen corto del perfil de reproducción que declara el cliente.</summary>
    internal static string Profile(DeviceProfile? profile)
    {
        if (profile is null)
        {
            return "sin perfil";
        }

        static string Join(IEnumerable<string?> values) => string.Join(',', values.Where(v => !string.IsNullOrEmpty(v)));

        var direct = Join(profile.DirectPlayProfiles.Where(p => p.Type == DlnaProfileType.Video)
            .Select(p => $"{p.Container}:{p.VideoCodec}/{p.AudioCodec}"));
        var transcode = Join(profile.TranscodingProfiles.Where(p => p.Type == DlnaProfileType.Video)
            .Select(p => $"{p.Container}({p.Protocol}):{p.VideoCodec}/{p.AudioCodec}"));
        var conditions = Join(profile.CodecProfiles.Where(c => c.Type == CodecType.Video)
            .SelectMany(c => c.Conditions.Select(x => $"{c.Codec}.{x.Property}{(x.IsRequired ? "!" : string.Empty)}")).Distinct());
        return $"directPlay[{direct}] transcode[{transcode}] condiciones[{conditions}]";
    }

    internal static void LogRequest(HttpContext http, string action, string flags, DeviceProfile? profile)
        => PluginLog.Info($"[PLAYBACKINFO] pedido {action} de {Who(http)} · {flags} · {Profile(profile)}");

    internal static void LogResponse(HttpContext http, IEnumerable<MediaSourceInfo> sources, Func<MediaSourceInfo, bool> isOurs)
    {
        foreach (var source in sources.Where(isOurs))
        {
            var url = source.TranscodingUrl is null ? "no" : "sí";
            PluginLog.Info(
                $"[PLAYBACKINFO] respuesta para {source.Id} a {Who(http)} · directPlay={source.SupportsDirectPlay} "
                + $"directStream={source.SupportsDirectStream} transcoding={source.SupportsTranscoding} urlTranscode={url} "
                + $"container={source.Container} interlaced={source.MediaStreams?.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video)?.IsInterlaced}");
        }
    }

    /// <summary>Registra el pedido de un stream de transcodificación/remux de un canal del plugin.</summary>
    internal static void LogStreamRequest(HttpContext http, string action, IReadOnlySet<string> channelIds)
    {
        var query = http.Request.Query;
        var mediaSourceId = query["MediaSourceId"].ToString();
        if (string.IsNullOrEmpty(mediaSourceId) || !channelIds.Contains(mediaSourceId))
        {
            return;
        }

        var parts = TranscodeQueryKeys
            .Where(key => query.ContainsKey(key) && key != "MediaSourceId")
            .Select(key => $"{key}={query[key]}");
        PluginLog.Info($"[PLAYBACKINFO] stream {action} de {Who(http)} · canal {mediaSourceId} · {string.Join(' ', parts)}");
    }
}
