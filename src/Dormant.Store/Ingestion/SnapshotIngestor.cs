using Dormant.Core.Collection;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>Writes one collection into the store, inside the caller's transaction.</summary>
internal sealed class SnapshotIngestor
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly BaselineRepository _baselines;
    private readonly PartitionHistoryWriter _partitionHistory;

    public SnapshotIngestor(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
        _baselines = new BaselineRepository(connection, transaction);
        _partitionHistory = new PartitionHistoryWriter(connection, transaction);
    }

    public async Task<IngestResult> IngestAsync(string instanceName, Snapshot snapshot, IngestOptions options, CancellationToken ct)
    {
        var instanceKey = await UpsertInstanceAsync(instanceName, snapshot.StartedUtc, ct);
        var outcome = snapshot.Databases.Any(d => d.Outcome == DatabaseOutcome.Failed) ? "partial" : "ok";
        var collectionId = await Sql.InsertAsync(
            _connection, _transaction,
            """
            INSERT INTO collection (instance_key, started_ms, duration_ms, outcome, error, server_name, platform, product_version,
                                    edition, engine_start_ms, database_count, index_count, synthetic)
            VALUES ($instance, $started, $duration, $outcome, NULL, $server, $platform, $version, $edition, $engine, $databases, $indexes,
                    $synthetic)
            """,
            ct,
            ("$synthetic", options.Synthetic),
            ("$instance", instanceKey),
            ("$started", Sql.Ms(snapshot.StartedUtc)),
            ("$duration", (long)snapshot.Duration.TotalMilliseconds),
            ("$outcome", outcome),
            ("$server", snapshot.Instance.ServerName),
            ("$platform", snapshot.Instance.Platform),
            ("$version", snapshot.Instance.ProductVersion),
            ("$edition", snapshot.Instance.Edition),
            ("$engine", Sql.Ms(snapshot.Instance.StartTimeUtc)),
            ("$databases", snapshot.Databases.Count),
            ("$indexes", snapshot.IndexCount));

        var catalog = new CatalogWriter(_connection, _transaction);
        var partitionWriter = new PartitionWriter(_connection, _transaction);
        var seen = new HashSet<long>();
        var results = new List<DatabaseIngest>();
        foreach (var database in snapshot.Databases)
        {
            if (database.Outcome == DatabaseOutcome.Collected)
            {
                var (databaseKey, interval, partitions) = await IngestCollectedAsync(instanceKey, collectionId, database, catalog, partitionWriter, options, ct);
                seen.Add(databaseKey);
                results.Add(new DatabaseIngest(database.Name, database.Outcome, database.Indexes.Count, interval, null)
                {
                    PartitionCount = database.Indexes.Sum(i => i.Partitions.Count),
                    ReservedBytes = database.Indexes.Sum(i => i.Partitions.Sum(p => p.ReservedPages)) * 8192,
                    Partitions = partitions,
                });
                continue;
            }

            var key = await FindDatabaseAsync(instanceKey, database, ct);
            if (key is { } known)
            {
                seen.Add(known);
                await InsertObservationAsync(collectionId, known, database.Outcome, database.Detail, ct);
                if (database.CountersMayHaveReset)
                {
                    await _baselines.MarkInterruptedAsync(known, $"database was not readable at one point ({database.Detail})", ct);
                }
            }

            results.Add(new DatabaseIngest(database.Name, database.Outcome, 0, null, database.Detail));
        }

        // A database this instance had before but that the collection did not list at all was dropped, detached,
        // renamed out of the filter or hidden from the login. If it comes back, its counters cannot be compared.
        foreach (var missing in await _baselines.DatabasesOfInstanceAsync(instanceKey, ct))
        {
            if (!seen.Contains(missing))
            {
                await _baselines.MarkInterruptedAsync(missing, "database was absent from a collection (detached, dropped or excluded)", ct);
            }
        }

        await new RollupWriter(_connection, _transaction).ApplyPendingAsync(ct);
        return new IngestResult(results);
    }

    public async Task RecordFailureAsync(string instanceName, DateTimeOffset startedUtc, TimeSpan duration, string error, CancellationToken ct)
    {
        var instanceKey = await UpsertInstanceAsync(instanceName, startedUtc, ct);
        await Sql.ExecuteAsync(
            _connection, _transaction,
            """
            INSERT INTO collection (instance_key, started_ms, duration_ms, outcome, error, database_count, index_count)
            VALUES ($instance, $started, $duration, 'failed', $error, 0, 0)
            """,
            ct,
            ("$instance", instanceKey),
            ("$started", Sql.Ms(startedUtc)),
            ("$duration", (long)duration.TotalMilliseconds),
            ("$error", error));
    }

    private async Task<(long DatabaseKey, IntervalResult? Interval, PartitionIntervalResult? Partitions)> IngestCollectedAsync(
        long instanceKey, long collectionId, DatabaseSnapshot database, CatalogWriter catalog, PartitionWriter partitionWriter,
        IngestOptions options, CancellationToken ct)
    {
        var observedUtc = database.ObservedUtc ?? throw new ArgumentException($"Collected database {database.Name} has no observation time.", nameof(database));
        var engine = database.Engine ?? throw new ArgumentException($"Collected database {database.Name} has no engine incarnation.", nameof(database));
        var identity = database.Identity ?? throw new ArgumentException($"Collected database {database.Name} has no identity.", nameof(database));
        var observedMs = Sql.Ms(observedUtc);

        var databaseKey = await Sql.ScalarAsync(
            _connection, _transaction,
            "SELECT database_key FROM db WHERE instance_key = $instance AND identity = $identity",
            ct, ("$instance", instanceKey), ("$identity", identity));
        if (databaseKey is null)
        {
            databaseKey = await Sql.InsertAsync(
                _connection, _transaction,
                "INSERT INTO db (instance_key, identity, name, first_seen_ms, last_seen_ms) VALUES ($instance, $identity, $name, $t, $t)",
                ct, ("$instance", instanceKey), ("$identity", identity), ("$name", database.Name), ("$t", observedMs));
        }
        else
        {
            // Renames keep the identity; the current name is shown everywhere.
            await Sql.ExecuteAsync(
                _connection, _transaction,
                "UPDATE db SET name = $name, last_seen_ms = $t WHERE database_key = $key",
                ct, ("$name", database.Name), ("$t", observedMs), ("$key", databaseKey));
        }

        var indexKeys = await catalog.WriteAsync(databaseKey.Value, database.Indexes, observedMs, ct);
        var usage = new Dictionary<long, UsageCounters?>(indexKeys.Count);
        for (var i = 0; i < indexKeys.Count; i++)
        {
            usage[indexKeys[i]] = database.Indexes[i].Usage;
        }

        var partitionReadings = await partitionWriter.WriteAsync(databaseKey.Value, database.Indexes, indexKeys, observedMs, ct);

        var reading = new DatabaseReading(observedUtc, database.DatabaseId, engine, usage);
        var baseline = await _baselines.LoadAsync(databaseKey.Value, ct);
        IntervalResult? interval = null;
        PartitionIntervalResult? partitions = null;
        if (baseline is not null)
        {
            interval = DeltaEngine.Compare(baseline, reading, options.MaxObservedSpan);
            var maintenance = options.IsMaintenance(interval.ResetAtUtc ?? interval.FromUtc, interval.ToUtc);
            var intervalId = await WriteIntervalAsync(databaseKey.Value, collectionId, interval, maintenance, ct);
            var previousPartitions = await _partitionHistory.LoadBaselineAsync(databaseKey.Value, ct);
            partitions = PartitionDeltaEngine.Compare(interval.Kind, previousPartitions, partitionReadings);
            await _partitionHistory.WriteAsync(intervalId, interval, partitions, ct);
        }

        await _baselines.SaveAsync(databaseKey.Value, reading, ct);
        await _partitionHistory.SaveBaselineAsync(databaseKey.Value, partitionReadings, ct);
        await InsertObservationAsync(collectionId, databaseKey.Value, database.Outcome, null, ct);
        return (databaseKey.Value, interval, partitions);
    }

    private async Task<long> WriteIntervalAsync(long databaseKey, long collectionId, IntervalResult interval, bool maintenance, CancellationToken ct)
    {
        if (interval.Kind != IntervalKind.Continuous)
        {
            await Sql.ExecuteAsync(
                _connection, _transaction,
                """
                INSERT INTO history_event (database_key, kind, at_ms, from_ms, to_ms, reason)
                VALUES ($db, $kind, $at, $from, $to, $reason)
                """,
                ct,
                ("$db", databaseKey),
                ("$kind", KindName(interval.Kind)),
                ("$at", Sql.Ms(interval.ResetAtUtc ?? interval.ToUtc)),
                ("$from", Sql.Ms(interval.FromUtc)),
                ("$to", Sql.Ms(interval.ToUtc)),
                ("$reason", interval.Reason));
        }

        var intervalId = await Sql.InsertAsync(
            _connection, _transaction,
            """
            INSERT INTO usage_interval (database_key, collection_id, from_ms, to_ms, observed_from_ms, kind, reset_at_ms, reason, maintenance)
            VALUES ($db, $collection, $from, $to, $observed, $kind, $reset, $reason, $maintenance)
            """,
            ct,
            ("$db", databaseKey),
            ("$collection", collectionId),
            ("$from", Sql.Ms(interval.FromUtc)),
            ("$to", Sql.Ms(interval.ToUtc)),
            ("$observed", Sql.Ms(interval.ObservedFromUtc)),
            ("$kind", KindName(interval.Kind)),
            ("$reset", Sql.Ms(interval.ResetAtUtc)),
            ("$reason", interval.Reason),
            ("$maintenance", maintenance ? 1 : 0));

        if (interval.Deltas.Count == 0)
        {
            return intervalId;
        }

        await using var command = Sql.Command(
            _connection, _transaction,
            """
            INSERT INTO index_delta (interval_id, index_key, user_seeks, user_scans, user_lookups, user_updates,
                                     system_seeks, system_scans, system_lookups, system_updates, lower_bound,
                                     last_user_read_ms, last_user_update_ms)
            VALUES ($interval, $index, $us, $usc, $ul, $uu, $ss, $ssc, $sl, $su, $lb, $lr, $lu)
            """,
            ("$interval", intervalId));
        var p = command.Parameters;
        string[] names = ["$index", "$us", "$usc", "$ul", "$uu", "$ss", "$ssc", "$sl", "$su", "$lb", "$lr", "$lu"];
        foreach (var name in names)
        {
            p.Add(new SqliteParameter(name, null));
        }

        foreach (var d in interval.Deltas)
        {
            p["$index"].Value = d.IndexKey;
            p["$us"].Value = d.UserSeeks;
            p["$usc"].Value = d.UserScans;
            p["$ul"].Value = d.UserLookups;
            p["$uu"].Value = d.UserUpdates;
            p["$ss"].Value = d.SystemSeeks;
            p["$ssc"].Value = d.SystemScans;
            p["$sl"].Value = d.SystemLookups;
            p["$su"].Value = d.SystemUpdates;
            p["$lb"].Value = d.LowerBound ? 1 : 0;
            p["$lr"].Value = (object?)Sql.Ms(d.LastUserReadUtc) ?? DBNull.Value;
            p["$lu"].Value = (object?)Sql.Ms(d.LastUserUpdateUtc) ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }

        return intervalId;
    }

    internal static string KindName(IntervalKind kind) => kind switch
    {
        IntervalKind.Continuous => "continuous",
        IntervalKind.Gap => "gap",
        IntervalKind.InstanceRestart => "instance_restart",
        IntervalKind.Reset => "reset",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private async Task<long?> FindDatabaseAsync(long instanceKey, DatabaseSnapshot database, CancellationToken ct) =>
        database.Identity is not null
            ? await Sql.ScalarAsync(
                _connection, _transaction,
                "SELECT database_key FROM db WHERE instance_key = $instance AND identity = $identity",
                ct, ("$instance", instanceKey), ("$identity", database.Identity))
            : await Sql.ScalarAsync(
                _connection, _transaction,
                "SELECT database_key FROM db WHERE instance_key = $instance AND name = $name ORDER BY last_seen_ms DESC LIMIT 1",
                ct, ("$instance", instanceKey), ("$name", database.Name));

    private Task<int> InsertObservationAsync(long collectionId, long databaseKey, DatabaseOutcome outcome, string? detail, CancellationToken ct) =>
        Sql.ExecuteAsync(
            _connection, _transaction,
            "INSERT OR REPLACE INTO db_observation (collection_id, database_key, outcome, detail) VALUES ($c, $d, $o, $detail)",
            ct,
            ("$c", collectionId),
            ("$d", databaseKey),
            ("$o", outcome switch
            {
                DatabaseOutcome.Collected => "collected",
                DatabaseOutcome.Skipped => "skipped",
                _ => "failed",
            }),
            ("$detail", detail));

    private async Task<long> UpsertInstanceAsync(string name, DateTimeOffset nowUtc, CancellationToken ct) =>
        await Sql.ScalarAsync(_connection, _transaction, "SELECT instance_key FROM instance WHERE name = $name", ct, ("$name", name))
        ?? await Sql.InsertAsync(
            _connection, _transaction,
            "INSERT INTO instance (name, created_ms) VALUES ($name, $t)",
            ct, ("$name", name), ("$t", Sql.Ms(nowUtc)));
}
