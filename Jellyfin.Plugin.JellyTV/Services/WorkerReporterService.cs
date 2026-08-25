using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JellyTV.Models;
using Microsoft.Extensions.Logging;

namespace JellyTV.Services;

/// <summary>
/// Reports channel health check results to the JellyTV Cloudflare Worker
/// so the Worker can serve backup URLs when a primary stream is dead.
/// Calls POST {WorkerBaseUrl}/api/health/report with x-api-key authentication.
/// </summary>
public class WorkerReporterService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WorkerReporterService> _logger;

    public WorkerReporterService(IHttpClientFactory httpClientFactory, ILogger<WorkerReporterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Extracts the Worker channel ID from a jellytv.hmovies.tv upstream URL.
    /// e.g. https://jellytv.hmovies.tv/live/ch_kanal-d-drama_8n8hm → ch_kanal-d-drama_8n8hm
    /// Returns null if the last path segment does not start with "ch_".
    /// </summary>
    private static string? ExtractChannelId(string channelUrl)
    {
        if (string.IsNullOrWhiteSpace(channelUrl))
            return null;

        try
        {
            var uri = new Uri(channelUrl);
            var last = uri.AbsolutePath.TrimEnd('/')
                          .Split('/', StringSplitOptions.RemoveEmptyEntries)
                          .LastOrDefault();
            return last?.StartsWith("ch_", StringComparison.OrdinalIgnoreCase) == true ? last : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sends health check results to the Worker in a single batched POST.
    /// Silently skips channels whose URL doesn't contain a Worker channel ID.
    /// No-ops if WorkerBaseUrl or WorkerApiKey are not configured.
    /// </summary>
    public async Task ReportAsync(IEnumerable<ChannelHealthStatus> results, CancellationToken ct = default)
    {
        var workerBaseUrl = Plugin.Instance?.Configuration?.WorkerBaseUrl?.TrimEnd('/');
        var apiKey = Plugin.Instance?.Configuration?.WorkerApiKey;

        if (string.IsNullOrWhiteSpace(workerBaseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            PluginLog.Info("[WORKER] WorkerBaseUrl/WorkerApiKey no configurados — saltando reporte.");
            return;
        }

        var resultList = results.ToList();
        PluginLog.Info($"[WORKER] ReportAsync recibió {resultList.Count} canales. Worker: {workerBaseUrl}");

        // Log first few URLs so we can verify format
        foreach (var r in resultList.Take(3))
        {
            var extracted = ExtractChannelId(r.ChannelUrl);
            PluginLog.Info($"[WORKER] URL muestra: '{r.ChannelUrl}' → id='{extracted ?? "NULL"}'  status={r.Status}");
        }

        // Build update list — skip any channel that isn't from the Worker
        var updates = resultList
            .Select(r => (Id: ExtractChannelId(r.ChannelUrl), Status: r))
            .Where(x => x.Id is not null)
            .Select(x => new
            {
                channelId = x.Id!,
                isAlive = x.Status.IsAlive,
                activeUrlIndex = 0,  // Plugin always checks primary URL (index 0)
                checkedAt = x.Status.CheckedAt.ToUnixTimeSeconds()
            })
            .ToList();

        if (updates.Count == 0)
        {
            PluginLog.Warn($"[WORKER] Ningún canal tiene ch_ en su URL — sin reporte. Revisa que el M3U tenga URLs de jellytv.hmovies.tv/live/ch_xxx");
            return;
        }

        var payload = JsonSerializer.Serialize(new { updates });
        var url = $"{workerBaseUrl}/api/health/report";

        try
        {
            var client = _httpClientFactory.CreateClient("JellyTV");
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            var aliveCount = updates.Count(u => u.isAlive);
            var deadCount  = updates.Count - aliveCount;

            if (response.IsSuccessStatusCode)
            {
                PluginLog.Info($"[WORKER] Reported {updates.Count} canales → {aliveCount} vivos, {deadCount} muertos.");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                PluginLog.Warn($"[WORKER] Report respondió HTTP {(int)response.StatusCode}: {body}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[JellyTV] WorkerReporter: report cancelled.");
        }
        catch (Exception ex)
        {
            PluginLog.Error("[WORKER] Error al reportar health al Worker", ex);
        }
    }
}
