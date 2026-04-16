using System.Text.Json;

using Tsavorite.core;

namespace NightElf.Database.Tsavorite;

public sealed class TsavoriteStateCheckpointStore<TContext> : IStateCheckpointStore<TContext>, IDisposable
    where TContext : KeyValueDbContext<TContext>
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TsavoriteDatabase<TContext> _database;
    private readonly TsavoriteStateCheckpointStoreOptions _options;

    public TsavoriteStateCheckpointStore(
        TsavoriteDatabase<TContext> database,
        TsavoriteStateCheckpointStoreOptions? options = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _options = options ?? new TsavoriteStateCheckpointStoreOptions();
        _options.Validate();

        if (_options.RetainedCheckpointCount > 1 && _database.RemoveOutdatedCheckpoints)
        {
            throw new InvalidOperationException(
                "Retaining more than one Tsavorite checkpoint requires RemoveOutdatedCheckpoints to be disabled.");
        }

        MetadataPath = Path.Combine(_database.CheckpointPath, _options.MetadataFileName);
        Directory.CreateDirectory(_database.CheckpointPath);
    }

    public string MetadataPath { get; }

    public async Task ApplyChangesAsync(
        StateCommitVersion version,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyCollection<string>? deletes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);

        var deleteKeys = deletes?.ToArray() ?? [];
        EnsureDisjointKeys(writes.Keys, deleteKeys);

        if (writes.Count == 0 && deleteKeys.Length == 0)
        {
            return;
        }

        var serializedChanges = new Dictionary<string, byte[]>(writes.Count + deleteKeys.Length, StringComparer.Ordinal);

        foreach (var pair in writes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            serializedChanges[pair.Key] = VersionedStateRecord.Create(version, pair.Value, isDeleted: false).Serialize();
        }

        foreach (var key in deleteKeys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            serializedChanges[key] = VersionedStateRecord.Create(version, Array.Empty<byte>(), isDeleted: true).Serialize();
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.SetAllAsync(serializedChanges, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<VersionedStateRecord?> GetVersionedStateAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await _database.GetAsync(key, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : VersionedStateRecord.Deserialize(bytes);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<StateCheckpointDescriptor> AdvanceCheckpointAsync(
        StateCommitVersion version,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferIncremental = _options.PreferIncrementalSnapshots;
            _database.GetLatestCheckpointTokens(out _, out var latestIndexCheckpointToken, out _);

            var hasBaseIndexCheckpoint = latestIndexCheckpointToken != Guid.Empty;
            var isIncremental = hasBaseIndexCheckpoint &&
                                preferIncremental &&
                                _database.CanTakeIncrementalCheckpoint(_options.CheckpointType, out _);

            var (success, _) = hasBaseIndexCheckpoint
                ? await _database.TakeHybridLogCheckpointAsync(
                        _options.CheckpointType,
                        preferIncremental,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _database.TakeFullCheckpointAsync(
                        _options.CheckpointType,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (!success)
            {
                throw new InvalidOperationException("Tsavorite could not initiate the requested state checkpoint.");
            }

            await _database.CompleteCheckpointAsync(cancellationToken).ConfigureAwait(false);
            _database.GetLatestCheckpointTokens(
                out var hybridLogCheckpointToken,
                out var indexCheckpointToken,
                out _);

            var checkpointedStoreVersion = _database.LastCheckpointedVersion;

            if (hybridLogCheckpointToken == Guid.Empty || indexCheckpointToken == Guid.Empty || checkpointedStoreVersion < 0)
            {
                throw new InvalidOperationException("Tsavorite checkpoint completed without valid checkpoint tokens.");
            }

            var descriptor = new StateCheckpointDescriptor
            {
                Name = BuildCheckpointName(version),
                BlockHeight = version.BlockHeight,
                BlockHash = version.BlockHash,
                StoreVersion = checkpointedStoreVersion,
                HybridLogCheckpointToken = hybridLogCheckpointToken,
                IndexCheckpointToken = indexCheckpointToken,
                IsIncremental = isIncremental,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            var checkpoints = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
            checkpoints.Add(descriptor);
            await SaveCatalogAsync(TrimRetention(checkpoints), cancellationToken).ConfigureAwait(false);

            return descriptor;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task RecoverToCheckpointAsync(
        StateCheckpointDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.IndexCheckpointToken == Guid.Empty || descriptor.HybridLogCheckpointToken == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Checkpoint '{descriptor.Name}' at height {descriptor.BlockHeight} is missing Tsavorite recovery tokens.");
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long recoveredVersion;
            try
            {
                recoveredVersion = await _database.RecoverAsync(
                        descriptor.IndexCheckpointToken,
                        descriptor.HybridLogCheckpointToken,
                        _options.RecoveryPreloadPages,
                        undoNextVersion: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Failed to recover checkpoint '{descriptor.Name}' at height {descriptor.BlockHeight}.",
                    exception);
            }

            if (descriptor.StoreVersion >= 0 && recoveredVersion < descriptor.StoreVersion)
            {
                throw new InvalidOperationException(
                    $"Checkpoint '{descriptor.Name}' at height {descriptor.BlockHeight} recovered to store version {recoveredVersion}, expected at least {descriptor.StoreVersion}.");
            }

            var checkpoints = await LoadCatalogAsync(cancellationToken).ConfigureAwait(false);
            var retained = checkpoints
                .Where(checkpoint => checkpoint.BlockHeight <= descriptor.BlockHeight)
                .OrderBy(checkpoint => checkpoint.BlockHeight)
                .ThenBy(checkpoint => checkpoint.CreatedAtUtc)
                .ToList();

            if (retained.All(checkpoint => checkpoint.HybridLogCheckpointToken != descriptor.HybridLogCheckpointToken))
            {
                retained.Add(descriptor);
            }

            await SaveCatalogAsync(TrimRetention(retained), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<StateCheckpointDescriptor>> GetCheckpointsAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadCatalogAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        _mutex.Dispose();
    }

    private static void EnsureDisjointKeys(
        IEnumerable<string> writeKeys,
        IEnumerable<string> deleteKeys)
    {
        var writtenKeys = new HashSet<string>(writeKeys, StringComparer.Ordinal);
        foreach (var key in deleteKeys)
        {
            if (writtenKeys.Contains(key))
            {
                throw new InvalidOperationException(
                    $"State key '{key}' cannot be written and deleted in the same commit.");
            }
        }
    }

    private string BuildCheckpointName(StateCommitVersion version)
    {
        var safeHash = new string(version.BlockHash
            .Take(16)
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());

        if (string.IsNullOrWhiteSpace(safeHash))
        {
            safeHash = "checkpoint";
        }

        return $"{_options.CheckpointNamePrefix}-{version.BlockHeight:D20}-{safeHash}";
    }

    private async Task<List<StateCheckpointDescriptor>> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(MetadataPath))
        {
            return [];
        }

        TsavoriteStateCheckpointCatalog? catalog;
        try
        {
            await using var stream = File.OpenRead(MetadataPath);
            catalog = await JsonSerializer.DeserializeAsync(
                    stream,
                    TsavoriteStateCheckpointStoreJsonSerializerContext.Default.TsavoriteStateCheckpointCatalog,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new InvalidOperationException(
                $"Failed to read checkpoint catalog '{MetadataPath}'.",
                exception);
        }

        return catalog?.Checkpoints?
            .OrderBy(checkpoint => checkpoint.BlockHeight)
            .ThenBy(checkpoint => checkpoint.CreatedAtUtc)
            .ToList() ?? [];
    }

    private async Task SaveCatalogAsync(
        IReadOnlyCollection<StateCheckpointDescriptor> checkpoints,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(MetadataPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{MetadataPath}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            var catalog = new TsavoriteStateCheckpointCatalog
            {
                Checkpoints = checkpoints
                    .OrderBy(checkpoint => checkpoint.BlockHeight)
                    .ThenBy(checkpoint => checkpoint.CreatedAtUtc)
                    .ToArray()
            };

            await JsonSerializer.SerializeAsync(
                    stream,
                    catalog,
                    TsavoriteStateCheckpointStoreJsonSerializerContext.Default.TsavoriteStateCheckpointCatalog,
                    cancellationToken)
                .ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, MetadataPath, overwrite: true);
    }

    private IReadOnlyList<StateCheckpointDescriptor> TrimRetention(
        IReadOnlyList<StateCheckpointDescriptor> checkpoints)
    {
        if (checkpoints.Count <= _options.RetainedCheckpointCount)
        {
            return checkpoints;
        }

        return checkpoints
            .OrderByDescending(checkpoint => checkpoint.BlockHeight)
            .ThenByDescending(checkpoint => checkpoint.CreatedAtUtc)
            .Take(_options.RetainedCheckpointCount)
            .OrderBy(checkpoint => checkpoint.BlockHeight)
            .ThenBy(checkpoint => checkpoint.CreatedAtUtc)
            .ToArray();
    }
}
