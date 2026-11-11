using System.Collections.Generic;

namespace Jellyfin.Plugin.JellyTV.Services.Storage;

/// <summary>
/// Persists the plugin-owned snapshot in a selectable backend.
/// </summary>
public interface IJellyTvStore
{
    /// <summary>Gets the short backend identifier ("json" or "postgres").</summary>
    string BackendName { get; }

    /// <summary>Gets the human-readable backend description.</summary>
    string Description { get; }

    /// <summary>Gets the schema/format version managed by the backend.</summary>
    int SchemaVersion { get; }

    /// <summary>
    /// Loads the stored snapshot.
    /// </summary>
    /// <returns>The snapshot, or <c>null</c> when the backend has no data yet.</returns>
    JellyTvStoreSnapshot? Load();

    /// <summary>
    /// Persists the snapshot, replacing the stored entities.
    /// </summary>
    /// <param name="snapshot">Snapshot to store.</param>
    void Save(JellyTvStoreSnapshot snapshot);

    /// <summary>
    /// Appends source health events and keeps the current status of each source.
    /// </summary>
    /// <param name="records">Health outcomes of the last run.</param>
    void SaveHealth(IReadOnlyList<SourceHealthRecord> records);

    /// <summary>
    /// Loads the most recent health events.
    /// </summary>
    /// <param name="limit">Maximum number of events.</param>
    /// <returns>Events ordered from newest to oldest.</returns>
    IReadOnlyList<SourceHealthRecord> LoadHealth(int limit);

    /// <summary>
    /// Persists the active source of each channel.
    /// </summary>
    /// <param name="records">Active source pointers.</param>
    void SaveActiveSources(IReadOnlyList<ActiveSourceRecord> records);

    /// <summary>
    /// Loads the persisted active source pointers.
    /// </summary>
    /// <returns>Active source pointers.</returns>
    IReadOnlyList<ActiveSourceRecord> LoadActiveSources();

    /// <summary>
    /// Replaces the EPG identities and programmes of one source.
    /// </summary>
    /// <param name="state">Sync outcome of the source.</param>
    /// <param name="channels">EPG identities provided by the source.</param>
    /// <param name="programmes">Programmes provided by the source.</param>
    void SaveEpgSync(EpgSourceState state, IReadOnlyList<EpgChannelRecord> channels, IReadOnlyList<EpgProgrammeRecord> programmes);

    /// <summary>
    /// Loads every stored EPG identity.
    /// </summary>
    /// <returns>EPG identities with their channel match.</returns>
    IReadOnlyList<EpgChannelRecord> LoadEpgChannels();

    /// <summary>
    /// Loads stored programmes ordered by start.
    /// </summary>
    /// <param name="limit">Maximum number of programmes.</param>
    /// <returns>Programmes ordered by start.</returns>
    IReadOnlyList<EpgProgrammeRecord> LoadEpgProgrammes(int limit);

    /// <summary>
    /// Loads the sync state of every EPG source.
    /// </summary>
    /// <returns>EPG source states ordered by priority.</returns>
    IReadOnlyList<EpgSourceState> LoadEpgSources();
}
