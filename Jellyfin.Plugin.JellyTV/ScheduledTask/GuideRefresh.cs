using System;
using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyTV.ScheduledTask;

/// <summary>
/// Helper para encolar la tarea nativa "Refresh Guide" de Jellyfin. Es la tarea homóloga tanto del
/// tuner M3U (re-escanea canales del tuner tras regenerar tuner.m3u) como del proveedor XMLTV
/// (re-importa la guía local tras regenerar epg.xml): Jellyfin la usa para ambas cosas.
/// </summary>
public static class GuideRefresh
{
    /// <summary>
    /// Encola "Refresh Guide" si existe. Fire-and-forget: no bloquea a la tarea del plugin.
    /// </summary>
    /// <param name="taskManager">Task manager de Jellyfin.</param>
    public static void Queue(ITaskManager taskManager)
    {
        if (taskManager is null)
        {
            PluginLog.Warn("[REFRESH] ITaskManager no disponible; refresco de guía omitido.");
            return;
        }

        try
        {
            var worker = taskManager.ScheduledTasks
                .FirstOrDefault(w => string.Equals(w.ScheduledTask.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase));

            if (worker is null)
            {
                PluginLog.Warn("[REFRESH] Tarea 'RefreshGuide' de Jellyfin no encontrada; refresco omitido.");
                return;
            }

            taskManager.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
            PluginLog.Info($"[REFRESH] 'Refresh Guide' de Jellyfin encolado: {worker.Name}");
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[REFRESH] No se pudo encolar 'Refresh Guide': {ex.Message}");
        }
    }
}
