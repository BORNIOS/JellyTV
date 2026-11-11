using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.JellyTV.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Selects and owns the active JellyTV storage backend.
/// </summary>
/// <remarks>
/// PostgreSQL is only used when the PG Provider plugin offers its private-schema host. Otherwise the
/// plugin transparently keeps working with local JSON files, so no database provider is required.
/// </remarks>
public sealed class StorageBackend
{
    private const string PostgresContractName = "Jellyfin.Database.Providers.Postgres.Api.IPluginSchemaHost";

    private readonly JsonFileStore _json;
    private readonly bool _writesJsonBackup;

    private StorageBackend(IJellyTvStore store, JsonFileStore json, bool writesJsonBackup)
    {
        Store = store;
        _json = json;
        _writesJsonBackup = writesJsonBackup;
    }

    /// <summary>Gets a value indicating whether the active store is a database (not JSON files).</summary>
    public bool UsesDatabase => !ReferenceEquals(Store, _json);

    /// <summary>Gets the active store.</summary>
    public IJellyTvStore Store { get; }

    /// <summary>Gets the active backend identifier.</summary>
    public string BackendName => Store.BackendName;

    /// <summary>Gets the active backend description.</summary>
    public string Description => Store.Description;

    /// <summary>Gets the schema/format version of the active backend.</summary>
    public int SchemaVersion => Store.SchemaVersion;

    /// <summary>Gets the folder used for the JSON files, whether active or used as a backup.</summary>
    public string JsonFolder => _json.Folder;

    /// <summary>Gets the JSON files currently present in <see cref="JsonFolder"/>.</summary>
    public IReadOnlyList<string> JsonFiles => _json.Files();

    /// <summary>Gets a value indicating whether JSON files are written while PostgreSQL is active.</summary>
    public bool WritesJsonBackup => _writesJsonBackup;

    /// <summary>
    /// Creates the active backend according to <see cref="PluginConfiguration.StorageBackend"/>.
    /// </summary>
    /// <param name="services">Server service provider.</param>
    /// <param name="configuration">Plugin configuration.</param>
    /// <param name="dataDirectory">JellyTV data directory used by the default JSON folder.</param>
    /// <param name="pluginId">Owning plugin identifier.</param>
    /// <returns>The selected backend.</returns>
    public static StorageBackend Create(IServiceProvider services, PluginConfiguration configuration, string dataDirectory, Guid pluginId)
    {
        var mode = (configuration.StorageBackend ?? "Auto").Trim();
        var json = new JsonFileStore(ResolveJsonFolder(configuration, dataDirectory));

        if (!string.Equals(mode, "Json", StringComparison.OrdinalIgnoreCase))
        {
            var postgres = TryCreatePostgres(services, pluginId);
            if (postgres is not null)
            {
                var backup = configuration.EnableJsonCourtesyBackup;
                PluginLog.Info(backup
                    ? $"[STORAGE] Using {postgres.Description}; JSON backup enabled at {json.Folder}."
                    : $"[STORAGE] Using {postgres.Description}.");
                return new StorageBackend(postgres, json, backup);
            }

            if (string.Equals(mode, "Postgres", StringComparison.OrdinalIgnoreCase))
            {
                PluginLog.Warn("[STORAGE] PostgreSQL was requested but PG Provider is not available; using JSON files instead.");
            }
        }

        PluginLog.Info($"[STORAGE] Using {json.Description}.");
        return new StorageBackend(json, json, false);
    }

    /// <summary>
    /// Persists the snapshot in the active store and, when enabled, in the JSON backup.
    /// </summary>
    /// <param name="snapshot">Snapshot to store.</param>
    public void Save(JellyTvStoreSnapshot snapshot)
    {
        Store.Save(snapshot);
        WriteJsonBackup(snapshot);
    }

    /// <summary>
    /// Writes the JSON backup when it is enabled, without touching the active store.
    /// </summary>
    /// <param name="snapshot">Snapshot to copy.</param>
    public void WriteJsonBackup(JellyTvStoreSnapshot snapshot)
    {
        if (_writesJsonBackup && !ReferenceEquals(Store, _json))
        {
            _json.Save(snapshot);
        }
    }

    private static string ResolveJsonFolder(PluginConfiguration configuration, string dataDirectory)
    {
        var configured = configuration.JsonDataPath?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured))
            {
                return configured;
            }

            PluginLog.Warn($"[STORAGE] JsonDataPath '{configured}' is not an absolute path; using {dataDirectory} instead.");
        }

        return dataDirectory;
    }

    private static PostgresSchemaStore? TryCreatePostgres(IServiceProvider services, Guid pluginId)
    {
        var found = FindSchemaHost(services);
        if (found is null)
        {
            return null;
        }

        try
        {
            var store = new PostgresSchemaStore(found.Value.Instance, found.Value.Contract, pluginId);
            return store.EnsureSchema() ? store : null;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"[STORAGE] PostgreSQL backend could not be created: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static (object Instance, Type Contract)? FindSchemaHost(IServiceProvider services)
    {
        foreach (var assembly in CandidateAssemblies(services))
        {
            Type? contract;
            try
            {
                contract = assembly.GetType(PostgresContractName, throwOnError: false);
            }
            catch
            {
                continue;
            }

            if (contract is null || !contract.IsInterface)
            {
                continue;
            }

            var resolved = services.GetService(contract);
            if (resolved is not null)
            {
                return (resolved, contract);
            }

            var implementation = FirstImplementation(assembly, contract);
            if (implementation is null)
            {
                continue;
            }

            try
            {
                return (ActivatorUtilities.CreateInstance(services, implementation), contract);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"[STORAGE] Could not create {implementation.FullName}: {ex.GetBaseException().Message}");
            }
        }

        return null;
    }

    private static IEnumerable<Assembly> CandidateAssemblies(IServiceProvider services)
    {
        var seen = new HashSet<Assembly>();
        var pluginManager = services.GetService<IPluginManager>();
        if (pluginManager is not null)
        {
            foreach (var plugin in pluginManager.Plugins)
            {
                var assembly = plugin.Instance?.GetType().Assembly;
                if (assembly is not null && seen.Add(assembly))
                {
                    yield return assembly;
                }
            }
        }

        foreach (var context in AssemblyLoadContext.All)
        {
            foreach (var assembly in context.Assemblies)
            {
                if (seen.Add(assembly))
                {
                    yield return assembly;
                }
            }
        }
    }

    private static Type? FirstImplementation(Assembly assembly, Type contract)
    {
        try
        {
            return assembly.GetTypes().FirstOrDefault(type => contract.IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.FirstOrDefault(type => type is not null && contract.IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false });
        }
    }
}
