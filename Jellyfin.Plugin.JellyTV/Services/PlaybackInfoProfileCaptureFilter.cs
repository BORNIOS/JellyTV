using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.JellyTV.Services;

/// <summary>
/// Captura el <see cref="DeviceProfile"/> que el cliente envía a PlaybackInfo para que
/// <see cref="PlaybackInfoDirectFilter"/> lo use al corregir la respuesta.
/// </summary>
public sealed class PlaybackInfoProfileCaptureFilter : IAsyncActionFilter
{
    /// <summary>Clave de HttpContext.Items con el resumen del pedido, que el filtro de respuesta registra si el canal es nuestro.</summary>
    internal const string RequestLogItemKey = "JellyTV.PlaybackRequestLog";

    // Pedidos de stream que se registran una vez por reproducción (no cada segmento).
    private static readonly HashSet<string> StreamActions = new(StringComparer.Ordinal)
    {
        "GetMasterHlsVideoPlaylist", "GetVariantHlsVideoPlaylist", "GetVideoStream", "GetLiveHlsStream"
    };

    // Campos del pedido de PlaybackInfo que explican la decisión del servidor.
    private static readonly string[] RequestFlagNames =
    [
        "EnableDirectPlay", "EnableDirectStream", "EnableTranscoding", "AllowVideoStreamCopy", "AllowAudioStreamCopy",
        "AutoOpenLiveStream", "MaxStreamingBitrate", "MediaSourceId"
    ];

    /// <inheritdoc/>
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.RouteData.Values.TryGetValue("action", out var action))
        {
            return next();
        }

        var actionName = action?.ToString();
        if (actionName is not null && StreamActions.Contains(actionName))
        {
            var ids = Plugin.Instance?.Configuration?.Channels?.Select(static c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (ids is { Count: > 0 })
            {
                PlaybackDiagnostics.LogStreamRequest(context.HttpContext, actionName, ids);
            }

            return next();
        }

        if (!string.Equals(actionName, "GetPostedPlaybackInfo", StringComparison.Ordinal))
        {
            return next();
        }

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null)
            {
                continue;
            }

            var property = argument.GetType().GetProperty("DeviceProfile");
            if (property?.GetValue(argument) is DeviceProfile profile)
            {
                context.HttpContext.Items[PlaybackInfoDirectFilter.ProfileItemKey] = profile;
                context.HttpContext.Items[RequestLogItemKey] = DescribeRequest(context, argument, profile);

                // Tras un fallo el cliente vuelve a pedir con EnableDirectPlay=false: ahí hay que dejar la
                // respuesta estándar del servidor (con su URL de transcode), no volver a forzar Direct Play.
                if (argument.GetType().GetProperty("EnableDirectPlay")?.GetValue(argument) is false)
                {
                    context.HttpContext.Items[PlaybackInfoDirectFilter.NoDirectPlayItemKey] = true;
                }

                break;
            }
        }

        return next();
    }

    private static string DescribeRequest(ActionExecutingContext context, object request, DeviceProfile profile)
    {
        static string Read(object source, string name) => source.GetType().GetProperty(name)?.GetValue(source)?.ToString() ?? "-";
        var flags = string.Join(
            ' ',
            RequestFlagNames.Select(name => $"{name}={Read(request, name)}"));
        return $"pedido PlaybackInfo de {PlaybackDiagnostics.Who(context.HttpContext)} · {flags} · {PlaybackDiagnostics.Profile(profile)}";
    }
}
