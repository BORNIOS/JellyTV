using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Stores each entity in its own JSON file inside a folder. This is the default backend so the plugin
/// never requires the PostgreSQL provider to be installed.
/// </summary>
public sealed class JsonFileStore : IJellyTvStore
{
    private const string ChannelsFileName = "channels.json";
    private const string ProxiesFileName = "proxies.json";
    private const string EpgSourcesFileName = "epg-sources.json";
    private const string HealthFileName = "health.json";
    private const string ActiveSourcesFileName = "active-sources.json";
    private const string EpgChannelsFileName = "epg-channels.json";
    private const string EpgProgrammesFileName = "epg-programmes.json";
    private const string EpgStatusFileName = "epg-status.json";
    private const string LegacyFileName = "store.json";
    private const int MaxHealthEvents = 5000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonFileStore"/> class.
    /// </summary>
    /// <param name="folder">Absolute folder that holds the JSON files.</param>
    public JsonFileStore(string folder)
    {
        Folder = folder;
    }

    /// <summary>Gets the folder that holds the JSON files.</summary>
    public string Folder { get; }

    /// <inheritdoc/>
    public string BackendName => "json";

    /// <inheritdoc/>
    public string Description => $"Archivos JSON ({Folder})";

    /// <inheritdoc/>
    public int SchemaVersion => 1;

    /// <summary>
    /// Lists the JSON files currently present in the folder.
    /// </summary>
    /// <returns>The file names, or an empty list when the folder does not exist.</returns>
    public IReadOnlyList<string> Files()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.json")
                    .Select(Path.GetFileName)
                    .Where(name => name is not null)
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[STORAGE] Could not list JSON files: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc/>
    public JellyTvStoreSnapshot? Load()
    {
        lock (_lock)
        {
            try
            {
                var channels = Read<List<ChannelEntry>>(ChannelsFileName);
                var proxies = Read<List<ProxyEntry>>(ProxiesFileName);
                var epgSources = Read<List<EpgSource>>(EpgSourcesFileName);
                if (channels is null && proxies is null && epgSources is null)
                {
                    // Formato anterior de un solo archivo; se migra a los archivos por entidad al guardar.
                    return Read<JellyTvStoreSnapshot>(LegacyFileName);
                }

                return new JellyTvStoreSnapshot
                {
                    Channels = channels ?? new List<ChannelEntry>(),
                    Proxies = proxies ?? new List<ProxyEntry>(),
                    EpgSources = epgSources ?? new List<EpgSource>()
                };
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[STORAGE] Could not read the JSON store: {ex.Message}");
                return null;
            }
        }
    }

    /// <inheritdoc/>
    public void Save(JellyTvStoreSnapshot snapshot)
    {
        lock (_lock)
        {
            Write(ChannelsFileName, snapshot.Channels);
            Write(ProxiesFileName, snapshot.Proxies);
            Write(EpgSourcesFileName, snapshot.EpgSources);
        }
    }

    /// <inheritdoc/>
    public void SaveHealth(IReadOnlyList<SourceHealthRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            var events = Read<List<SourceHealthRecord>>(HealthFileName) ?? new List<SourceHealthRecord>();
            events.AddRange(records);
            if (events.Count > MaxHealthEvents)
            {
                events.RemoveRange(0, events.Count - MaxHealthEvents);
            }

            Write(HealthFileName, events);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SourceHealthRecord> LoadHealth(int limit)
    {
        lock (_lock)
        {
            var events = Read<List<SourceHealthRecord>>(HealthFileName) ?? new List<SourceHealthRecord>();
            return events.AsEnumerable().Reverse().Take(limit).ToArray();
        }
    }

    /// <inheritdoc/>
    public void SaveActiveSources(IReadOnlyList<ActiveSourceRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            var byChannel = (Read<List<ActiveSourceRecord>>(ActiveSourcesFileName) ?? new List<ActiveSourceRecord>())
                .GroupBy(record => record.ChannelId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            foreach (var record in records)
            {
                byChannel[record.ChannelId] = record;
            }

            Write(ActiveSourcesFileName, byChannel.Values.OrderBy(record => record.ChannelId, StringComparer.Ordinal).ToList());
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActiveSourceRecord> LoadActiveSources()
        => Read<List<ActiveSourceRecord>>(ActiveSourcesFileName) ?? new List<ActiveSourceRecord>();

    /// <inheritdoc/>
    public void SaveEpgSync(EpgSourceState state, IReadOnlyList<EpgChannelRecord> channels, IReadOnlyList<EpgProgrammeRecord> programmes)
    {
        lock (_lock)
        {
            var states = (Read<List<EpgSourceState>>(EpgStatusFileName) ?? new List<EpgSourceState>())
                .Where(item => !string.Equals(item.EpgSourceId, state.EpgSourceId, StringComparison.Ordinal))
                .Append(state)
                .OrderBy(item => item.Priority)
                .ToList();
            Write(EpgStatusFileName, states);

            var identities = (Read<List<EpgChannelRecord>>(EpgChannelsFileName) ?? new List<EpgChannelRecord>())
                .Where(item => !string.Equals(item.EpgSourceId, state.EpgSourceId, StringComparison.Ordinal))
                .Concat(channels)
                .ToList();
            Write(EpgChannelsFileName, identities);

            var existing = (Read<List<EpgProgrammeRecord>>(EpgProgrammesFileName) ?? new List<EpgProgrammeRecord>())
                .Where(item => !string.Equals(item.EpgSourceId, state.EpgSourceId, StringComparison.Ordinal))
                .Concat(programmes)
                .Where(item => item.Stop > DateTimeOffset.UtcNow.AddDays(-7))
                .OrderBy(item => item.Start)
                .ToList();
            Write(EpgProgrammesFileName, existing);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<EpgChannelRecord> LoadEpgChannels()
        => Read<List<EpgChannelRecord>>(EpgChannelsFileName) ?? new List<EpgChannelRecord>();

    /// <inheritdoc/>
    public IReadOnlyList<EpgProgrammeRecord> LoadEpgProgrammes(int limit)
        => (Read<List<EpgProgrammeRecord>>(EpgProgrammesFileName) ?? new List<EpgProgrammeRecord>())
            .OrderBy(item => item.Start)
            .Take(limit)
            .ToArray();

    /// <inheritdoc/>
    public IReadOnlyList<EpgSourceState> LoadEpgSources()
        => (Read<List<EpgSourceState>>(EpgStatusFileName) ?? new List<EpgSourceState>())
            .OrderBy(item => item.Priority)
            .ToArray();

    private T? Read<T>(string fileName)
        where T : class
    {
        var path = Path.Combine(Folder, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path, Encoding.UTF8);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
    }

    private void Write<T>(string fileName, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, fileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
