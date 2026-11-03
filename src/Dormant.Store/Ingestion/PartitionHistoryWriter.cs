using Dormant.Core.Collection;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>Partition baselines, and the activity and unknown stretches each interval adds.</summary>
internal sealed class PartitionHistoryWriter
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public PartitionHistoryWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<Dictionary<long, PartitionReading>> LoadBaselineAsync(long databaseKey, CancellationToken ct)
    {
        var result = new Dictionary<long, PartitionReading>();
        await using var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT partition_key, partition_id, has_stats, range_scans, singleton_lookups, leaf_inserts, leaf_updates, leaf_deletes
            FROM part_baseline WHERE database_key = $db
            """,
            ("$db", databaseKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetInt64(0)] = new PartitionReading(
                reader.GetInt64(1),
                reader.GetBoolean(2)
                    ? new OperationalCounters(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7))
                    : null);
        }

        return result;
    }

    public async Task SaveBaselineAsync(long databaseKey, IReadOnlyDictionary<long, PartitionReading> readings, CancellationToken ct)
    {
        await Sql.ExecuteAsync(_connection, _transaction, "DELETE FROM part_baseline WHERE database_key = $db", ct, ("$db", databaseKey));

        await using var command = Sql.Command(
            _connection, _transaction,
            """
            INSERT INTO part_baseline (partition_key, database_key, partition_id, has_stats, range_scans, singleton_lookups,
                                       leaf_inserts, leaf_updates, leaf_deletes)
            VALUES ($key, $db, $id, $has, $rs, $sl, $li, $lu, $ld)
            """,
            ("$db", databaseKey));
        var p = command.Parameters;
        foreach (var name in (string[])["$key", "$id", "$has", "$rs", "$sl", "$li", "$lu", "$ld"])
        {
            p.Add(new SqliteParameter(name, null));
        }

        foreach (var (key, reading) in readings)
        {
            var ops = reading.Operations;
            p["$key"].Value = key;
            p["$id"].Value = reading.PartitionId;
            p["$has"].Value = ops is null ? 0 : 1;
            p["$rs"].Value = ops?.RangeScans ?? 0;
            p["$sl"].Value = ops?.SingletonLookups ?? 0;
            p["$li"].Value = ops?.LeafInserts ?? 0;
            p["$lu"].Value = ops?.LeafUpdates ?? 0;
            p["$ld"].Value = ops?.LeafDeletes ?? 0;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task WriteAsync(long intervalId, IntervalResult interval, PartitionIntervalResult partitions, CancellationToken ct)
    {
        if (partitions.Deltas.Count > 0)
        {
            await using var command = Sql.Command(
                _connection, _transaction,
                """
                INSERT INTO part_delta (interval_id, partition_key, range_scans, singleton_lookups, leaf_inserts, leaf_updates,
                                        leaf_deletes, lower_bound)
                VALUES ($interval, $key, $rs, $sl, $li, $lu, $ld, $lb)
                """,
                ("$interval", intervalId));
            var p = command.Parameters;
            foreach (var name in (string[])["$key", "$rs", "$sl", "$li", "$lu", "$ld", "$lb"])
            {
                p.Add(new SqliteParameter(name, null));
            }

            foreach (var d in partitions.Deltas)
            {
                p["$key"].Value = d.PartitionKey;
                p["$rs"].Value = d.RangeScans;
                p["$sl"].Value = d.SingletonLookups;
                p["$li"].Value = d.LeafInserts;
                p["$lu"].Value = d.LeafUpdates;
                p["$ld"].Value = d.LeafDeletes;
                p["$lb"].Value = d.LowerBound ? 1 : 0;
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        if (partitions.Unknown.Count == 0)
        {
            return;
        }

        // Extend the stretch that ends where this interval starts; otherwise start a new one. A partition that stays
        // uncached for months is one row, not one row per collection.
        var from = Sql.Ms(interval.FromUtc);
        var to = Sql.Ms(interval.ToUtc);
        await using var extend = Sql.Command(
            _connection, _transaction,
            "UPDATE part_unknown SET to_ms = $to WHERE partition_key = $key AND to_ms = $from",
            ("$to", to), ("$from", from), ("$key", 0L));
        await using var insert = Sql.Command(
            _connection, _transaction,
            "INSERT OR REPLACE INTO part_unknown (partition_key, from_ms, to_ms) VALUES ($key, $from, $to)",
            ("$to", to), ("$from", from), ("$key", 0L));
        foreach (var key in partitions.Unknown)
        {
            extend.Parameters["$key"].Value = key;
            if (await extend.ExecuteNonQueryAsync(ct) == 0)
            {
                insert.Parameters["$key"].Value = key;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
    }
}
