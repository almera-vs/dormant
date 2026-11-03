using Dormant.Core.Usage;
using Dormant.Core.Attribution;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Store.History;
using Dormant.Store.Ingestion;
using Dormant.Core.History;
using Dormant.Store.Status;
using Microsoft.Data.Sqlite;

namespace Dormant.Store;

/// <summary>
/// Dormant's local history: a single SQLite file holding metadata and counters only. Every write of a collection is one
/// transaction, so a crash never leaves half a snapshot behind.
/// </summary>
public sealed class DormantStore : IAsyncDisposable, IAttributionSource
{
    private readonly SqliteConnection _connection;

    // One SQLite connection, shared by every instance's collection loop in 'run': one operation at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DormantStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Opens the store for writing, creating it (readable by the current user only) and upgrading its schema as needed.</summary>
    public static async Task<DormantStore> OpenAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        try
        {
            StoreFile.EnsureCreated(fullPath);
            var connection = await ConnectAsync(fullPath, SqliteOpenMode.ReadWrite, ct);
            try
            {
                await ExecuteAsync(connection, "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;", ct);
                await Schema.MigrateAsync(connection, ct);
                return new DormantStore(connection, fullPath);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new StoreException($"The store {fullPath} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>Opens an existing store without changing it; null when there is no store at <paramref name="path"/> yet.</summary>
    public static async Task<DormantStore?> OpenReadOnlyAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            var connection = await ConnectAsync(fullPath, SqliteOpenMode.ReadOnly, ct);
            var version = await Schema.ReadVersionAsync(connection, ct);
            if (version != Schema.CurrentVersion)
            {
                await connection.DisposeAsync();
                throw new StoreException(version > Schema.CurrentVersion
                    ? $"The store {fullPath} was written by a newer version of Dormant (schema {version}). Upgrade Dormant."
                    : $"The store {fullPath} uses an older schema ({version}). Run 'dormant collect' once to upgrade it.");
            }

            return new DormantStore(connection, fullPath);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new StoreException($"The store {fullPath} could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>Records one successful collection and turns it into history.</summary>
    public async Task<IngestResult> IngestAsync(string instanceName, Snapshot snapshot, IngestOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            var result = await new SnapshotIngestor(_connection, transaction).IngestAsync(instanceName, snapshot, options, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be written: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes history older than the retention periods, in one transaction.</summary>
    public async Task<PruneResult> PruneAsync(DateTimeOffset nowUtc, RetentionOptions retention, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            var result = await new RetentionPruner(_connection, transaction).PruneAsync(nowUtc, retention, ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be pruned: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records a collection that did not reach the instance, so that status can show it.</summary>
    public async Task RecordFailureAsync(string instanceName, DateTimeOffset startedUtc, TimeSpan duration, string error, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            await new SnapshotIngestor(_connection, transaction).RecordFailureAsync(instanceName, startedUtc, duration, error, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be written: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What to read from each collected database's Query Store next (decision D-053).</summary>
    public async Task<IReadOnlyList<QueryStoreRequest>> QueryStoreRequestsAsync(
        string instanceName, IReadOnlyList<(string Identity, string Name)> databases, DateTimeOffset nowUtc, TimeSpan lookback, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await new Store.Attribution.AttributionWriter(_connection, null).RequestsAsync(instanceName, databases, nowUtc, lookback, ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be read: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Records what was read from Query Store, in one transaction.</summary>
    public async Task WriteQueryStoreAsync(string instanceName, IReadOnlyList<QueryStoreHarvest> harvests, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
            await new Store.Attribution.AttributionWriter(_connection, transaction).WriteAsync(instanceName, harvests, nowUtc, ct);
            await transaction.CommitAsync(ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be written: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ObjectReader>> GetReadersAsync(ObjectKey key, DateTimeOffset since, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await new Store.Attribution.AttributionReader(_connection).ReadersAsync(key, since, ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be read: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Classifies every current table, partition and index from the history, as of <paramref name="nowUtc"/>.</summary>
    public Task<EstateClassification> ReadClassificationAsync(DormantConfig config, DateTimeOffset nowUtc, CancellationToken ct) =>
        ReadClassificationAsync(config, nowUtc, historyDays: 0, ct);

    /// <summary>
    /// Classifies every current table, partition and index, and reads their user reads day by day over the last
    /// <paramref name="historyDays"/> UTC days (what the report's heatmap draws).
    /// </summary>
    public async Task<EstateClassification> ReadClassificationAsync(DormantConfig config, DateTimeOffset nowUtc, int historyDays, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(historyDays);
        await _gate.WaitAsync(ct);
        try
        {
            return await new Estate.ClassificationReader(_connection, config, nowUtc).ReadAsync(nowUtc, historyDays, ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be read: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoreStatus> ReadStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await new StatusReader(_connection, Path).ReadAsync(ct);
        }
        catch (SqliteException ex)
        {
            throw new StoreException($"The store {Path} could not be read: {ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        _gate.Dispose();
    }

    private static async Task<SqliteConnection> ConnectAsync(string path, SqliteOpenMode mode, CancellationToken ct)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 60,
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;", ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal static class StoreFile
{
    /// <summary>
    /// Creates the store's directory and an empty file before SQLite opens it, so that on Linux both are private to the
    /// service account (0700 and 0600). SQLite gives its -wal and -shm files the main file's permissions. On Windows the
    /// file inherits the directory's ACL; see docs/threat-model.md.
    /// </summary>
    public static void EnsureCreated(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            using (File.Create(path))
            {
            }

            return;
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
        }
    }
}
