using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyTV.Models;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Corrige la respuesta de PlaybackInfo para los canales de JellyTV.
/// Jellyfin marca como entrelazado todo vídeo que no viene de su servicio Live TV integrado
/// (<c>LiveTvMediaSourceProvider.Normalize</c>), y los perfiles de los clientes rechazan
/// <c>IsInterlaced=true</c>, así que cada canal acaba en transcode aunque el cliente pueda
/// reproducir el HLS/TS tal cual. Este filtro decide el DirectPlay con lo que sí importa: que el
/// perfil del cliente declare un DirectPlay compatible con el contenedor y los códecs del canal.
/// </summary>
public sealed class PlaybackInfoDirectFilter : IAsyncResultFilter
{
    /// <summary>Clave de HttpContext.Items donde <see cref="PlaybackInfoProfileCaptureFilter"/> deja el perfil.</summary>
    internal const string ProfileItemKey = "JellyTV.DeviceProfile";

    /// <summary>Clave de HttpContext.Items que marca una petición que no admite Direct Play.</summary>
    internal const string NoDirectPlayItemKey = "JellyTV.NoDirectPlay";

    /// <inheritdoc/>
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        try
        {
            if (context.Result is ObjectResult { Value: PlaybackInfoResponse response }
                && response.MediaSources is { Count: > 0 })
            {
                if (!context.HttpContext.Items.ContainsKey(NoDirectPlayItemKey))
                {
                    Apply(response, FindDeviceProfile(context.HttpContext));
                }

                LogNegotiation(context, response);
            }
        }
        catch (Exception ex)
        {
            // Nunca romper PlaybackInfo por esta mejora: si falla se conserva la respuesta original.
            PluginLog.Warn($"[PLAYBACKINFO] No se pudo aplicar DirectPlay: {ex.Message}");
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Marca las fuentes del plugin como DirectPlay cuando el perfil del cliente lo permite.
    /// </summary>
    /// <param name="response">Respuesta de PlaybackInfo a corregir.</param>
    /// <param name="profile">Perfil del cliente (<c>null</c> si la petición no lo envió).</param>
    internal static void Apply(PlaybackInfoResponse response, DeviceProfile? profile)
    {
        var channels = Plugin.Instance?.Configuration?.Channels;
        if (channels is null || profile is null)
        {
            return;
        }

        foreach (var source in response.MediaSources)
        {
            if (!IsJellyTvSource(source, channels) || source.SupportsDirectPlay)
            {
                continue;
            }

            if (!CanDirectPlay(source, profile))
            {
                continue;
            }

            source.SupportsDirectPlay = true;
            source.SupportsDirectStream = true;
            source.TranscodingUrl = null;
            source.TranscodingContainer = null;
        }
    }

    private static void LogNegotiation(ResultExecutingContext context, PlaybackInfoResponse response)
    {
        var ids = Plugin.Instance?.Configuration?.Channels?.Select(static c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ids is null || !response.MediaSources.Any(source => !string.IsNullOrEmpty(source.Id) && ids.Contains(source.Id)))
        {
            return;
        }

        if (context.HttpContext.Items.TryGetValue(PlaybackInfoProfileCaptureFilter.RequestLogItemKey, out var request) && request is string text)
        {
            PluginLog.Info($"[PLAYBACKINFO] {text} · reintento sin DirectPlay={context.HttpContext.Items.ContainsKey(NoDirectPlayItemKey)}");
        }

        PlaybackDiagnostics.LogResponse(context.HttpContext, response.MediaSources, source => !string.IsNullOrEmpty(source.Id) && ids.Contains(source.Id));
    }

    private static bool IsJellyTvSource(MediaSourceInfo source, List<ChannelEntry> channels) =>
        !string.IsNullOrEmpty(source.Id)
        && source.Protocol == MediaProtocol.Http
        && !source.RequiresOpening
        && channels.Any(c => string.Equals(c.Id, source.Id, StringComparison.Ordinal));

    private static bool CanDirectPlay(MediaSourceInfo source, DeviceProfile profile)
    {
        var container = source.Container;
        var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (string.IsNullOrEmpty(container) || video is null)
        {
            return false;
        }

        return profile.DirectPlayProfiles.Any(p =>
            p.Type == DlnaProfileType.Video
            && p.SupportsContainer(container)
            && p.SupportsVideoCodec(video.Codec)
            && (audio is null || p.SupportsAudioCodec(audio.Codec)));
    }

    private static DeviceProfile? FindDeviceProfile(Microsoft.AspNetCore.Http.HttpContext httpContext) =>
        httpContext.Items.TryGetValue(ProfileItemKey, out var stored) ? stored as DeviceProfile : null;
}
