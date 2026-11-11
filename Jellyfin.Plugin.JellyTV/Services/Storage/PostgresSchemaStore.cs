using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.JellyTV.Models;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Stores the plugin snapshot in an isolated schema managed by the PostgreSQL provider.
/// </summary>
/// <remarks>
/// The provider assembly is optional and never referenced at compile time. The host contract is
/// discovered from the running plugin container, and every value is sent as a parameter confined to
/// the JellyTV-owned schema. The host rejects qualified identifiers, so statements upsert through
/// <c>EXCLUDED</c> and assemble their projections in code.
/// </remarks>
public sealed class PostgresSchemaStore : IJellyTvStore
{
    private const int CurrentSchemaVersion = 2;
    private const int HealthRetentionDays = 30;
    private const int ProgrammeRetentionDays = 7;
    private const int BatchSize = 1000;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNameCaseInsensitive = true };

    private readonly object _host;
    private readonly Type _hostContract;
    private readonly Type _requestType;
    private readonly Type _migrationType;
    private readonly Type _parameterType;
    private object? _schema;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresSchemaStore"/> class.
    /// </summary>
    /// <param name="host">PostgreSQL schema-host instance.</param>
    /// <param name="contract">Schema-host contract type.</param>
    /// <param name="pluginId">Owning plugin identifier.</param>
    public PostgresSchemaStore(object host, Type contract, Guid pluginId)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _hostContract = contract ?? throw new ArgumentNullException(nameof(contract));
        var assembly = contract.Assembly;
        _requestType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSchemaRequest");
        _migrationType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSchemaMigration");
        _parameterType = RequireType(assembly, "Jellyfin.Database.Providers.Postgres.Api.PluginSqlParameter");
        SchemaName = "jellytv_" + pluginId.ToString("N");
        PluginId = pluginId;
    }

    /// <summary>Gets the private schema name owned by JellyTV.</summary>
    public string SchemaName { get; }

    /// <summary>Gets the owning plugin identifier.</summary>
    public Guid PluginId { get; }

    /// <inheritdoc/>
    public string BackendName => "postgres";

    /// <inheritdoc/>
    public string Description => $"PostgreSQL (PG Provider), esquema {SchemaName}";

    /// <inheritdoc/>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>
    /// Opens (or creates) the private schema and applies migrations.
    /// </summary>
    /// <returns><c>true</c> when the schema is ready.</returns>
    public bool EnsureSchema()
    {
        try
        {
            var request = Activator.CreateInstance(_requestType, PluginId, SchemaName, false)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _schema = OpenOrMigrate(request, timeout.Token);
            ApplyMigrations();
            Execute("INSERT INTO schema_version (version) VALUES (@version) ON CONFLICT (version) DO NOTHING", Param("version", CurrentSchemaVersion));
            MigrateLegacyData();
            return true;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[STORAGE] PostgreSQL backend unavailable: {ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public JellyTvStoreSnapshot? Load()
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT jsonb_build_object(" +
            "'Channels', coalesce((SELECT jsonb_agg(jsonb_build_object('id', channel_id, 'name', name, 'category', category, 'logoUrl', logo_url, 'tvgId', tvg_id, 'tvgName', tvg_name, 'country', country, 'epgPageUrl', epg_page_url, 'isEnabled', is_enabled, 'autoDisabled', auto_disabled, 'userAgent', user_agent, 'customHeaders', custom_headers, 'type', default_type, 'sortOrder', sort_order, 'identity', identity)) FROM channel), '[]'::jsonb), " +
            "'Sources', coalesce((SELECT jsonb_agg(jsonb_build_object('id', source_id, 'channelId', channel_id, 'priority', priority, 'label', label, 'url', url, 'type', source_type, 'sessionToken', session_token, 'isEnabled', is_enabled, 'exitCountry', exit_country)) FROM channel_source), '[]'::jsonb), " +
            "'Statuses', coalesce((SELECT jsonb_agg(jsonb_build_object('sourceId', source_id, 'isAlive', is_alive, 'lastError', last_error, 'lastCheckedAt', last_checked_at)) FROM source_status), '[]'::jsonb), " +
            "'Proxies', coalesce((SELECT jsonb_agg(payload) FROM jellytv_proxy), '[]'::jsonb), " +
            "'EpgSources', coalesce((SELECT jsonb_agg(jsonb_build_object('id', source_id, 'name', name, 'url', url, 'isEnabled', is_enabled, 'lastSyncedAt', synced_at, 'lastSyncSummary', summary, 'lastSyncError', error)) FROM epg_source_state), '[]'::jsonb))::text",
            static reader => reader.Read() ? reader.GetString(0) : string.Empty);

        return BuildSnapshot(json);
    }

    /// <inheritdoc/>
    public void Save(JellyTvStoreSnapshot snapshot)
    {
        EnsureReady();
        SaveChannels(snapshot.Channels);
        SaveProxies(snapshot.Proxies);
        SaveEpgSourceStates(snapshot.EpgSources);
    }

    /// <inheritdoc/>
    public void SaveHealth(IReadOnlyList<SourceHealthRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        EnsureReady();
        var rows = JsonSerializer.Serialize(records.Select(record => new
        {
            channel_id = record.ChannelId,
            source_id = record.SourceId,
            checked_at = record.CheckedAt,
            is_alive = record.IsAlive,
            error = record.Error
        }));

        Execute(
            "INSERT INTO health_event (channel_id, source_id, checked_at, is_alive, error) SELECT channel_id, source_id, checked_at, is_alive, error FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(channel_id text, source_id text, checked_at timestamptz, is_alive boolean, error text)",
            Param("rows", rows));

        Execute(
            "WITH input AS (SELECT DISTINCT ON (source_id) source_id, is_alive, error, checked_at FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(channel_id text, source_id text, checked_at timestamptz, is_alive boolean, error text) ORDER BY source_id, checked_at DESC) " +
            "INSERT INTO source_status (source_id, is_alive, last_error, last_checked_at) SELECT source_id, is_alive, error, checked_at FROM input " +
            "ON CONFLICT (source_id) DO UPDATE SET is_alive = EXCLUDED.is_alive, last_error = EXCLUDED.last_error, last_checked_at = EXCLUDED.last_checked_at, updated_at = now()",
            Param("rows", rows));

        Execute($"DELETE FROM health_event WHERE checked_at < now() - interval '{HealthRetentionDays} days'");
    }

    /// <inheritdoc/>
    public IReadOnlyList<SourceHealthRecord> LoadHealth(int limit)
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT coalesce(jsonb_agg(entry), '[]'::jsonb)::text FROM (SELECT jsonb_build_object('channelId', channel_id, 'sourceId', source_id, 'checkedAt', checked_at, 'isAlive', is_alive, 'error', error) AS entry FROM health_event ORDER BY checked_at DESC LIMIT @limit) recent",
            static reader => reader.Read() ? reader.GetString(0) : "[]",
            Param("limit", Math.Clamp(limit, 1, 5000)));
        return JsonSerializer.Deserialize<List<SourceHealthRecord>>(json, WebJson) ?? new List<SourceHealthRecord>();
    }

    /// <inheritdoc/>
    public void SaveActiveSources(IReadOnlyList<ActiveSourceRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        EnsureReady();
        var rows = JsonSerializer.Serialize(records.Select(record => new
        {
            channel_id = record.ChannelId,
            source_id = record.SourceId,
            reason = record.Reason,
            switched_at = record.SwitchedAt
        }));

        Execute(
            "INSERT INTO channel_active_source (channel_id, source_id, reason, switched_at) SELECT channel_id, source_id, reason, switched_at FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(channel_id text, source_id text, reason text, switched_at timestamptz) " +
            "ON CONFLICT (channel_id) DO UPDATE SET source_id = EXCLUDED.source_id, reason = EXCLUDED.reason, switched_at = EXCLUDED.switched_at",
            Param("rows", rows));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ActiveSourceRecord> LoadActiveSources()
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT coalesce(jsonb_agg(jsonb_build_object('channelId', channel_id, 'sourceId', source_id, 'reason', reason, 'switchedAt', switched_at)), '[]'::jsonb)::text FROM channel_active_source",
            static reader => reader.Read() ? reader.GetString(0) : "[]");
        return JsonSerializer.Deserialize<List<ActiveSourceRecord>>(json, WebJson) ?? new List<ActiveSourceRecord>();
    }

    /// <inheritdoc/>
    public void SaveEpgSync(EpgSourceState state, IReadOnlyList<EpgChannelRecord> channels, IReadOnlyList<EpgProgrammeRecord> programmes)
    {
        EnsureReady();
        Execute(
            "INSERT INTO epg_source_state (source_id, name, url, is_enabled, priority, synced_at, summary, error, channel_count, programme_count) VALUES (@source, @name, @url, @enabled, @priority, @synced, @summary, @error, @channels, @programmes) " +
            "ON CONFLICT (source_id) DO UPDATE SET name = EXCLUDED.name, url = EXCLUDED.url, is_enabled = EXCLUDED.is_enabled, priority = EXCLUDED.priority, synced_at = EXCLUDED.synced_at, summary = EXCLUDED.summary, error = EXCLUDED.error, channel_count = EXCLUDED.channel_count, programme_count = EXCLUDED.programme_count, updated_at = now()",
            Param("source", state.EpgSourceId),
            Param("name", state.Name),
            Param("url", state.Url),
            Param("enabled", state.IsEnabled),
            Param("priority", state.Priority),
            Param("synced", state.SyncedAt),
            Param("summary", state.Summary),
            Param("error", state.Error),
            Param("channels", state.ChannelCount),
            Param("programmes", state.ProgrammeCount));

        Execute("DELETE FROM epg_channel WHERE source_id = @source", Param("source", state.EpgSourceId));
        foreach (var chunk in Chunk(channels, BatchSize))
        {
            Execute(
                "INSERT INTO epg_channel (source_id, xmltv_id, channel_id, display_name, icon_url, match_method) SELECT source_id, xmltv_id, channel_id, display_name, icon_url, match_method FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text, xmltv_id text, channel_id text, display_name text, icon_url text, match_method text) " +
                "ON CONFLICT (source_id, xmltv_id) DO UPDATE SET channel_id = EXCLUDED.channel_id, display_name = EXCLUDED.display_name, icon_url = EXCLUDED.icon_url, match_method = EXCLUDED.match_method, updated_at = now()",
                Param("rows", SerializeEpgChannels(chunk)));
        }

        Execute("DELETE FROM epg_programme WHERE source_id = @source", Param("source", state.EpgSourceId));
        foreach (var chunk in Chunk(programmes, BatchSize))
        {
            Execute(
                "INSERT INTO epg_programme (source_id, xmltv_id, channel_id, start_at, stop_at, title, description, category, icon_url, release_year) SELECT source_id, xmltv_id, channel_id, start_at, stop_at, title, description, category, icon_url, release_year FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text, xmltv_id text, channel_id text, start_at timestamptz, stop_at timestamptz, title text, description text, category text, icon_url text, release_year integer) " +
                "ON CONFLICT (source_id, xmltv_id, start_at) DO UPDATE SET channel_id = EXCLUDED.channel_id, stop_at = EXCLUDED.stop_at, title = EXCLUDED.title, description = EXCLUDED.description, category = EXCLUDED.category, icon_url = EXCLUDED.icon_url, release_year = EXCLUDED.release_year",
                Param("rows", SerializeProgrammes(chunk)));
        }

        Execute($"DELETE FROM epg_programme WHERE stop_at < now() - interval '{ProgrammeRetentionDays} days'");
    }

    /// <inheritdoc/>
    public IReadOnlyList<EpgChannelRecord> LoadEpgChannels()
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT coalesce(jsonb_agg(jsonb_build_object('epgSourceId', source_id, 'xmltvId', xmltv_id, 'channelId', channel_id, 'displayName', display_name, 'iconUrl', icon_url, 'matchMethod', match_method)), '[]'::jsonb)::text FROM epg_channel",
            static reader => reader.Read() ? reader.GetString(0) : "[]");
        return JsonSerializer.Deserialize<List<EpgChannelRecord>>(json, WebJson) ?? new List<EpgChannelRecord>();
    }

    /// <inheritdoc/>
    public IReadOnlyList<EpgProgrammeRecord> LoadEpgProgrammes(int limit)
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT coalesce(jsonb_agg(entry), '[]'::jsonb)::text FROM (SELECT jsonb_build_object('epgSourceId', source_id, 'xmltvId', xmltv_id, 'channelId', channel_id, 'start', start_at, 'stop', stop_at, 'title', title, 'description', description, 'category', category, 'iconUrl', icon_url, 'year', release_year) AS entry FROM epg_programme ORDER BY start_at LIMIT @limit) recent",
            static reader => reader.Read() ? reader.GetString(0) : "[]",
            Param("limit", Math.Clamp(limit, 1, 50000)));
        return JsonSerializer.Deserialize<List<EpgProgrammeRecord>>(json, WebJson) ?? new List<EpgProgrammeRecord>();
    }

    /// <inheritdoc/>
    public IReadOnlyList<EpgSourceState> LoadEpgSources()
    {
        EnsureReady();
        var json = Query<string>(
            "SELECT coalesce(jsonb_agg(jsonb_build_object('epgSourceId', source_id, 'name', name, 'url', url, 'isEnabled', is_enabled, 'priority', priority, 'syncedAt', synced_at, 'summary', summary, 'error', error, 'channelCount', channel_count, 'programmeCount', programme_count) ORDER BY priority), '[]'::jsonb)::text FROM epg_source_state",
            static reader => reader.Read() ? reader.GetString(0) : "[]");
        return JsonSerializer.Deserialize<List<EpgSourceState>>(json, WebJson) ?? new List<EpgSourceState>();
    }

    private static (string Name, object? Value) Param(string name, object? value) => (name, value);

    private static Type RequireType(Assembly assembly, string name) => assembly.GetType(name, throwOnError: true)!;

    private static JsonElement ReadJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var index = 0; index < items.Count; index += size)
        {
            yield return items.Skip(index).Take(size).ToList();
        }
    }

    private static string SerializeEpgChannels(IReadOnlyList<EpgChannelRecord> channels)
        => JsonSerializer.Serialize(channels.Select(record => new
        {
            source_id = record.EpgSourceId,
            xmltv_id = record.XmltvId,
            channel_id = record.ChannelId,
            display_name = record.DisplayName,
            icon_url = record.IconUrl,
            match_method = record.MatchMethod
        }));

    private static string SerializeProgrammes(IReadOnlyList<EpgProgrammeRecord> programmes)
        => JsonSerializer.Serialize(programmes.Select(record => new
        {
            source_id = record.EpgSourceId,
            xmltv_id = record.XmltvId,
            channel_id = record.ChannelId,
            start_at = record.Start,
            stop_at = record.Stop,
            title = record.Title,
            description = record.Description,
            category = record.Category,
            icon_url = record.IconUrl,
            release_year = record.Year
        }));

    private static JellyTvStoreSnapshot? BuildSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<SnapshotPayload>(json, WebJson);
        if (payload is null || (payload.Channels.Count == 0 && payload.Proxies.Count == 0 && payload.EpgSources.Count == 0))
        {
            return null;
        }

        var statuses = payload.Statuses
            .GroupBy(item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var sourcesByChannel = payload.Sources
            .GroupBy(item => item.ChannelId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Priority).ToList(), StringComparer.Ordinal);

        var channels = new List<ChannelEntry>(payload.Channels.Count);
        foreach (var row in payload.Channels.OrderBy(item => item.SortOrder).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var channel = new ChannelEntry
            {
                Id = row.Id,
                Name = row.Name ?? string.Empty,
                Category = row.Category,
                LogoUrl = row.LogoUrl,
                TvgId = row.TvgId,
                TvgName = row.TvgName,
                Country = row.Country,
                EpgPageUrl = row.EpgPageUrl,
                IsEnabled = row.IsEnabled,
                AutoDisabled = row.AutoDisabled,
                UserAgent = row.UserAgent,
                CustomHeaders = row.CustomHeaders,
                Identity = row.Identity,
                Type = row.Type
            };

            if (sourcesByChannel.TryGetValue(channel.Id, out var sources))
            {
                foreach (var source in sources)
                {
                    channel.Sources.Add(new ChannelSource
                    {
                        Id = source.Id,
                        Label = source.Label,
                        Url = source.Url ?? string.Empty,
                        Type = source.Type,
                        SessionToken = source.SessionToken,
                        IsEnabled = source.IsEnabled,
                        ExitCountry = source.ExitCountry
                    });
                }
            }

            foreach (var source in channel.Sources)
            {
                if (statuses.TryGetValue(source.Id, out var status))
                {
                    source.IsAlive = status.IsAlive;
                    source.LastError = status.LastError;
                    source.LastCheckedAt = status.LastCheckedAt;
                }
            }

            var enabled = channel.Sources.Where(source => source.IsEnabled && !string.IsNullOrWhiteSpace(source.Url)).ToList();

            // Sin comprobar no es lo mismo que caído: solo se declara caído un canal cuyas fuentes ya se probaron y fallaron.
            channel.LastCheckAlive = enabled.Any(source => source.IsAlive == true) ? true : enabled.Any(source => source.IsAlive == false) ? false : null;
            channel.LastCheckedAt = enabled.Where(source => source.LastCheckedAt.HasValue).Select(source => source.LastCheckedAt).Max();
            channel.LastCheckError = enabled.FirstOrDefault(source => source.IsAlive == false)?.LastError;
            channels.Add(channel);
        }

        return new JellyTvStoreSnapshot
        {
            Channels = channels,
            Proxies = payload.Proxies,
            EpgSources = payload.EpgSources
        };
    }

    private void EnsureReady()
    {
        if (_schema is null && !EnsureSchema())
        {
            throw new InvalidOperationException("The JellyTV PostgreSQL schema is not available.");
        }
    }

    private object OpenOrMigrate(object request, CancellationToken cancellationToken)
    {
        try
        {
            return InvokeTask("MigratePreRegistrySchemaAsync", request, cancellationToken)!;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist", StringComparison.Ordinal)
            || ex.Message.Contains("already exists", StringComparison.Ordinal))
        {
            return InvokeTask("OpenSchemaAsync", request, cancellationToken)!;
        }
    }

    private void ApplyMigrations()
    {
        var migrations = new[]
        {
            // v1 — legacy JSONB payload tables; kept so existing installs keep their data.
            ("jellytv-v01-00", "CREATE TABLE IF NOT EXISTS schema_version (version integer PRIMARY KEY, applied_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v01-01", "CREATE TABLE IF NOT EXISTS jellytv_channel (id text PRIMARY KEY, payload jsonb NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v01-02", "CREATE TABLE IF NOT EXISTS jellytv_proxy (id text PRIMARY KEY, payload jsonb NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v01-03", "CREATE TABLE IF NOT EXISTS jellytv_epg_source (id text PRIMARY KEY, payload jsonb NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now())"),

            // v2 — normalized model: channels, their ordered backups and every runtime signal.
            ("jellytv-v02-00", "CREATE TABLE IF NOT EXISTS channel (channel_id text PRIMARY KEY, name text NOT NULL, category text, logo_url text, tvg_id text, tvg_name text, is_enabled boolean NOT NULL DEFAULT true, auto_disabled boolean NOT NULL DEFAULT false, user_agent text, custom_headers text, default_type smallint NOT NULL DEFAULT 0, sort_order integer NOT NULL DEFAULT 0, created_at timestamp with time zone NOT NULL DEFAULT now(), updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-01", "CREATE INDEX IF NOT EXISTS ix_jellytv_channel_tvg ON channel (lower(tvg_id))"),
            ("jellytv-v02-02", "CREATE INDEX IF NOT EXISTS ix_jellytv_channel_enabled ON channel (is_enabled)"),
            ("jellytv-v02-03", "CREATE TABLE IF NOT EXISTS channel_source (source_id text PRIMARY KEY, channel_id text NOT NULL REFERENCES channel(channel_id) ON DELETE CASCADE, priority smallint NOT NULL, label text, url text NOT NULL, source_type smallint NOT NULL DEFAULT 0, session_token boolean NOT NULL DEFAULT false, is_enabled boolean NOT NULL DEFAULT true, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-04", "CREATE UNIQUE INDEX IF NOT EXISTS ux_jellytv_source_id ON channel_source (source_id)"),
            ("jellytv-v02-05", "CREATE INDEX IF NOT EXISTS ix_jellytv_source_channel ON channel_source (channel_id, priority)"),
            ("jellytv-v02-06", "CREATE TABLE IF NOT EXISTS source_status (source_id text PRIMARY KEY, is_alive boolean, last_error text, last_checked_at timestamp with time zone, consecutive_failures integer NOT NULL DEFAULT 0, next_check_at timestamp with time zone, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-07", "CREATE INDEX IF NOT EXISTS ix_jellytv_status_alive ON source_status (is_alive, next_check_at)"),
            ("jellytv-v02-08", "CREATE TABLE IF NOT EXISTS channel_active_source (channel_id text PRIMARY KEY REFERENCES channel(channel_id) ON DELETE CASCADE, source_id text NOT NULL, reason text NOT NULL, switched_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-09", "CREATE TABLE IF NOT EXISTS health_event (event_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, channel_id text NOT NULL, source_id text NOT NULL, checked_at timestamp with time zone NOT NULL DEFAULT now(), is_alive boolean NOT NULL, error text)"),
            ("jellytv-v02-10", "CREATE INDEX IF NOT EXISTS ix_jellytv_health_source ON health_event (channel_id, checked_at DESC)"),
            ("jellytv-v02-11", "CREATE TABLE IF NOT EXISTS epg_source_state (source_id text PRIMARY KEY, name text NOT NULL, url text NOT NULL, is_enabled boolean NOT NULL DEFAULT true, priority smallint NOT NULL DEFAULT 0, synced_at timestamp with time zone, summary text, error text, channel_count integer NOT NULL DEFAULT 0, programme_count integer NOT NULL DEFAULT 0, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-12", "CREATE TABLE IF NOT EXISTS epg_channel (source_id text NOT NULL REFERENCES epg_source_state(source_id) ON DELETE CASCADE, xmltv_id text NOT NULL, channel_id text, display_name text, icon_url text, match_method text NOT NULL DEFAULT 'none', updated_at timestamp with time zone NOT NULL DEFAULT now(), PRIMARY KEY (source_id, xmltv_id))"),
            ("jellytv-v02-13", "CREATE INDEX IF NOT EXISTS ix_jellytv_epg_channel_channel ON epg_channel (channel_id)"),
            ("jellytv-v02-14", "CREATE TABLE IF NOT EXISTS epg_programme (source_id text NOT NULL, xmltv_id text NOT NULL, channel_id text, start_at timestamp with time zone NOT NULL, stop_at timestamp with time zone NOT NULL, title text NOT NULL, description text, category text, icon_url text, release_year integer, PRIMARY KEY (source_id, xmltv_id, start_at))"),
            ("jellytv-v02-15", "CREATE INDEX IF NOT EXISTS ix_jellytv_epg_programme_channel ON epg_programme (channel_id, start_at)"),
            ("jellytv-v02-16", "CREATE INDEX IF NOT EXISTS ix_jellytv_epg_programme_window ON epg_programme (start_at, stop_at)"),
            ("jellytv-v02-17", "CREATE TABLE IF NOT EXISTS tmdb_cache (normalized_title text PRIMARY KEY, payload jsonb NOT NULL, expires_at timestamp with time zone NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-18", "CREATE TABLE IF NOT EXISTS tmdb_negative (normalized_title text PRIMARY KEY, attempts integer NOT NULL DEFAULT 1, expires_at timestamp with time zone NOT NULL, updated_at timestamp with time zone NOT NULL DEFAULT now())"),
            ("jellytv-v02-19", "ALTER TABLE channel ADD COLUMN IF NOT EXISTS identity jsonb"),
            ("jellytv-v02-20", "ALTER TABLE channel ADD COLUMN IF NOT EXISTS country text"),
            ("jellytv-v02-21", "ALTER TABLE channel ADD COLUMN IF NOT EXISTS epg_page_url text"),
            ("jellytv-v02-22", "ALTER TABLE channel_source ADD COLUMN IF NOT EXISTS exit_country text")
        };

        var array = Array.CreateInstance(_migrationType, migrations.Length);
        for (var index = 0; index < migrations.Length; index++)
        {
            array.SetValue(Activator.CreateInstance(_migrationType, migrations[index].Item1, migrations[index].Item2), index);
        }

        InvokeTask("ApplyMigrationsAsync", _schema!, array, CancellationToken.None);
    }

    private void MigrateLegacyData()
    {
        var legacy = Query<int>("SELECT count(*)::int FROM jellytv_channel", static reader => reader.Read() ? reader.GetInt32(0) : 0);
        var current = Query<int>("SELECT count(*)::int FROM channel", static reader => reader.Read() ? reader.GetInt32(0) : 0);
        if (legacy == 0 || current > 0)
        {
            return;
        }

        var channelsJson = Query<string>("SELECT coalesce(jsonb_agg(payload), '[]'::jsonb)::text FROM jellytv_channel", static reader => reader.Read() ? reader.GetString(0) : "[]");
        var epgJson = Query<string>("SELECT coalesce(jsonb_agg(payload), '[]'::jsonb)::text FROM jellytv_epg_source", static reader => reader.Read() ? reader.GetString(0) : "[]");
        var channels = JsonSerializer.Deserialize<List<ChannelEntry>>(channelsJson, SnapshotJson) ?? new List<ChannelEntry>();
        var epgSources = JsonSerializer.Deserialize<List<EpgSource>>(epgJson, SnapshotJson) ?? new List<EpgSource>();

        SaveChannels(channels);
        SaveEpgSourceStates(epgSources);
        Execute("DELETE FROM jellytv_channel");
        Execute("DELETE FROM jellytv_epg_source");
        PluginLog.Info($"[STORAGE] Migrated {channels.Count} channels and {epgSources.Count} EPG sources to schema v{CurrentSchemaVersion}.");
    }

    private void SaveChannels(IReadOnlyList<ChannelEntry> channels)
    {
        var channelRows = JsonSerializer.Serialize(channels.Select((channel, index) => new
        {
            channel_id = channel.Id,
            name = channel.Name,
            category = channel.Category,
            logo_url = channel.LogoUrl,
            tvg_id = channel.TvgId,
            tvg_name = channel.TvgName,
            country = channel.Country,
            epg_page_url = channel.EpgPageUrl,
            is_enabled = channel.IsEnabled,
            auto_disabled = channel.AutoDisabled,
            user_agent = channel.UserAgent,
            custom_headers = channel.CustomHeaders,
            default_type = (short)channel.Type,
            sort_order = index,
            identity = channel.Identity
        }));

        Execute(
            "WITH input AS (SELECT channel_id, name, category, logo_url, tvg_id, tvg_name, country, epg_page_url, is_enabled, auto_disabled, user_agent, custom_headers, default_type, sort_order, identity FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(channel_id text, name text, category text, logo_url text, tvg_id text, tvg_name text, country text, epg_page_url text, is_enabled boolean, auto_disabled boolean, user_agent text, custom_headers text, default_type smallint, sort_order integer, identity jsonb)) " +
            "INSERT INTO channel (channel_id, name, category, logo_url, tvg_id, tvg_name, country, epg_page_url, is_enabled, auto_disabled, user_agent, custom_headers, default_type, sort_order, identity) SELECT channel_id, name, category, logo_url, tvg_id, tvg_name, country, epg_page_url, is_enabled, auto_disabled, user_agent, custom_headers, default_type, sort_order, identity FROM input " +
            "ON CONFLICT (channel_id) DO UPDATE SET name = EXCLUDED.name, category = EXCLUDED.category, logo_url = EXCLUDED.logo_url, tvg_id = EXCLUDED.tvg_id, tvg_name = EXCLUDED.tvg_name, country = EXCLUDED.country, epg_page_url = EXCLUDED.epg_page_url, is_enabled = EXCLUDED.is_enabled, auto_disabled = EXCLUDED.auto_disabled, user_agent = EXCLUDED.user_agent, custom_headers = EXCLUDED.custom_headers, default_type = EXCLUDED.default_type, sort_order = EXCLUDED.sort_order, identity = EXCLUDED.identity, updated_at = now()",
            Param("rows", channelRows));

        var sources = new List<object>();
        foreach (var channel in channels)
        {
            channel.EnsureMigratedSources();
            for (var index = 0; index < channel.Sources.Count; index++)
            {
                var source = channel.Sources[index];
                sources.Add(new
                {
                    source_id = source.Id,
                    channel_id = channel.Id,
                    priority = (short)index,
                    label = source.Label,
                    url = source.Url,
                    source_type = (short)source.Type,
                    session_token = source.SessionToken,
                    is_enabled = source.IsEnabled,
                    exit_country = string.IsNullOrWhiteSpace(source.ExitCountry) ? null : source.ExitCountry
                });
            }
        }

        var sourceRows = JsonSerializer.Serialize(sources);
        Execute(
            "WITH input AS (SELECT source_id, channel_id, priority, label, url, source_type, session_token, is_enabled, exit_country FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text, channel_id text, priority smallint, label text, url text, source_type smallint, session_token boolean, is_enabled boolean, exit_country text)) " +
            "INSERT INTO channel_source (source_id, channel_id, priority, label, url, source_type, session_token, is_enabled, exit_country) SELECT source_id, channel_id, priority, label, url, source_type, session_token, is_enabled, exit_country FROM input " +
            "ON CONFLICT (source_id) DO UPDATE SET channel_id = EXCLUDED.channel_id, priority = EXCLUDED.priority, label = EXCLUDED.label, url = EXCLUDED.url, source_type = EXCLUDED.source_type, session_token = EXCLUDED.session_token, is_enabled = EXCLUDED.is_enabled, exit_country = EXCLUDED.exit_country, updated_at = now()",
            Param("rows", sourceRows));

        Execute(
            "DELETE FROM channel WHERE channel_id NOT IN (SELECT channel_id FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(channel_id text))",
            Param("rows", channelRows));
        Execute(
            "DELETE FROM channel_source WHERE source_id NOT IN (SELECT source_id FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text))",
            Param("rows", sourceRows));
        Execute(
            "DELETE FROM source_status WHERE source_id NOT IN (SELECT source_id FROM channel_source)");
    }

    private void SaveProxies(IReadOnlyList<ProxyEntry> proxies)
    {
        var rows = JsonSerializer.Serialize(proxies.Select(proxy => new
        {
            id = proxy.Id,
            payload = ReadJson(JsonSerializer.Serialize(proxy))
        }));
        Execute(
            "WITH input AS (SELECT id, payload FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(id text, payload jsonb)), upsert AS (INSERT INTO jellytv_proxy (id, payload, updated_at) SELECT id, payload, now() FROM input ON CONFLICT (id) DO UPDATE SET payload = EXCLUDED.payload, updated_at = now() RETURNING id) DELETE FROM jellytv_proxy WHERE id NOT IN (SELECT id FROM input)",
            Param("rows", rows));
    }

    private void SaveEpgSourceStates(IReadOnlyList<EpgSource> sources)
    {
        var rows = JsonSerializer.Serialize(sources.Select((source, index) => new
        {
            source_id = source.Id,
            name = source.Name,
            url = source.Url,
            is_enabled = source.IsEnabled,
            priority = (short)index,
            synced_at = source.LastSyncedAt,
            summary = source.LastSyncSummary,
            error = source.LastSyncError
        }));

        Execute(
            "WITH input AS (SELECT source_id, name, url, is_enabled, priority, synced_at, summary, error FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text, name text, url text, is_enabled boolean, priority smallint, synced_at timestamptz, summary text, error text)) " +
            "INSERT INTO epg_source_state (source_id, name, url, is_enabled, priority, synced_at, summary, error) SELECT source_id, name, url, is_enabled, priority, synced_at, summary, error FROM input " +
            "ON CONFLICT (source_id) DO UPDATE SET name = EXCLUDED.name, url = EXCLUDED.url, is_enabled = EXCLUDED.is_enabled, priority = EXCLUDED.priority, synced_at = EXCLUDED.synced_at, summary = EXCLUDED.summary, error = EXCLUDED.error, updated_at = now()",
            Param("rows", rows));

        Execute(
            "DELETE FROM epg_source_state WHERE source_id NOT IN (SELECT source_id FROM jsonb_to_recordset(CAST(@rows AS jsonb)) AS x(source_id text))",
            Param("rows", rows));
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
        => (int)(InvokeTask("ExecuteAsync", _schema!, sql, BuildParameters(parameters), CancellationToken.None) ?? 0);

    private T Query<T>(string sql, Func<DbDataReader, T> project, params (string Name, object? Value)[] parameters)
    {
        var method = _hostContract.GetMethods().Single(candidate => candidate.Name == "QueryAsync" && candidate.IsGenericMethodDefinition);
        var task = (System.Threading.Tasks.Task)method.MakeGenericMethod(typeof(T)).Invoke(_host, [_schema!, sql, project, BuildParameters(parameters), CancellationToken.None])!;
        task.GetAwaiter().GetResult();
        return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private object? InvokeTask(string name, params object?[] arguments)
    {
        var method = _hostContract.GetMethods().Single(candidate =>
        {
            var parameters = candidate.GetParameters();
            return candidate.Name == name
                && parameters.Length == arguments.Length
                && parameters.Select(static parameter => parameter.ParameterType)
                    .Zip(arguments, static (parameterType, argument) => argument is null ? !parameterType.IsValueType : parameterType.IsInstanceOfType(argument))
                    .All(static compatible => compatible);
        });
        var task = (System.Threading.Tasks.Task)method.Invoke(_host, arguments)!;
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private Array BuildParameters(IEnumerable<(string Name, object? Value)> parameters)
    {
        var values = parameters.ToArray();
        var array = Array.CreateInstance(_parameterType, values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            array.SetValue(Activator.CreateInstance(_parameterType, values[index].Name, values[index].Value), index);
        }

        return array;
    }

    private sealed class SnapshotPayload
    {
        public List<ChannelRow> Channels { get; set; } = new();

        public List<SourceRow> Sources { get; set; } = new();

        public List<StatusRow> Statuses { get; set; } = new();

        public List<ProxyEntry> Proxies { get; set; } = new();

        public List<EpgSource> EpgSources { get; set; } = new();
    }

    private sealed class ChannelRow
    {
        public ChannelIdentity? Identity { get; set; }

        public string Id { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Category { get; set; }

        public string? LogoUrl { get; set; }

        public string? TvgId { get; set; }

        public string? TvgName { get; set; }

        public string? Country { get; set; }

        public string? EpgPageUrl { get; set; }

        public bool IsEnabled { get; set; } = true;

        public bool AutoDisabled { get; set; }

        public string? UserAgent { get; set; }

        public string? CustomHeaders { get; set; }

        public int Type { get; set; }

        public int SortOrder { get; set; }
    }

    private sealed class SourceRow
    {
        public string Id { get; set; } = string.Empty;

        public string ChannelId { get; set; } = string.Empty;

        public int Priority { get; set; }

        public string? Label { get; set; }

        public string? Url { get; set; }

        public int Type { get; set; }

        public bool SessionToken { get; set; }

        public bool IsEnabled { get; set; } = true;

        public string? ExitCountry { get; set; }
    }

    private sealed class StatusRow
    {
        public string SourceId { get; set; } = string.Empty;

        public bool? IsAlive { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset? LastCheckedAt { get; set; }
    }
}
