using Dormant.Store.Ingestion;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.History;

/// <summary>
/// Folds raw intervals into hourly and daily buckets, each interval exactly once (decision D-043):
/// <list type="bullet">
/// <item>observed time is split exactly at bucket boundaries, so coverage is preserved at every level;</item>
/// <item>activity cannot be split (counters are cumulative), so it goes to the bucket in which its interval ended: the
/// later bucket, which errs towards "recently used";</item>
/// <item>lower-bound flags and last-read times survive as a maximum; maintenance activity stays in its own columns.</item>
/// </list>
/// </summary>
internal sealed class RollupWriter
{
    public const long MsPerHour = 3_600_000;
    public const long MsPerDay = 86_400_000;

    private static readonly (string Table, string Column, long Size)[] Buckets = [("hour", "hour", MsPerHour), ("day", "day", MsPerDay)];

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public RollupWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>Folds every interval not yet rolled up, oldest first.</summary>
    public async Task ApplyPendingAsync(CancellationToken ct)
    {
        var pending = new List<(long Id, long Db, long To, long? ObservedFrom, bool Maintenance)>();
        await using (var command = Sql.Command(
            _connection, _transaction,
            "SELECT interval_id, database_key, to_ms, observed_from_ms, maintenance FROM usage_interval WHERE rolled_up = 0 ORDER BY interval_id"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                pending.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetBoolean(4)));
            }
        }

        foreach (var interval in pending)
        {
            if (interval.ObservedFrom is { } observedFrom && observedFrom < interval.To)
            {
                await AddObservedAsync("db_hour", "hour", MsPerHour, interval.Db, observedFrom, interval.To, interval.Maintenance, ct);
                await AddObservedAsync("db_day", "day", MsPerDay, interval.Db, observedFrom, interval.To, interval.Maintenance, ct);
            }

            foreach (var (table, column, size) in Buckets)
            {
                var bucket = (interval.To - 1) / size;
                await Sql.ExecuteAsync(_connection, _transaction, IndexRollup(table, column), ct, ("$id", interval.Id), ("$bucket", bucket), ("$m", interval.Maintenance ? 1 : 0));
                await Sql.ExecuteAsync(_connection, _transaction, PartitionRollup(table, column), ct, ("$id", interval.Id), ("$bucket", bucket), ("$m", interval.Maintenance ? 1 : 0));
            }

            await Sql.ExecuteAsync(_connection, _transaction, "UPDATE usage_interval SET rolled_up = 1 WHERE interval_id = $id", ct, ("$id", interval.Id));
        }
    }

    private async Task AddObservedAsync(string table, string column, long size, long databaseKey, long from, long to, bool maintenance, CancellationToken ct)
    {
        for (var bucket = from / size; bucket * size < to; bucket++)
        {
            var observed = Math.Min(to, (bucket + 1) * size) - Math.Max(from, bucket * size);
            if (observed <= 0)
            {
                continue;
            }

            await Sql.ExecuteAsync(
                _connection, _transaction,
                $"""
                INSERT INTO {table} (database_key, {column}, observed_ms, maintenance_ms) VALUES ($db, $bucket, $ms, $maint)
                ON CONFLICT (database_key, {column}) DO UPDATE SET
                    observed_ms = observed_ms + excluded.observed_ms,
                    maintenance_ms = maintenance_ms + excluded.maintenance_ms
                """,
                ct,
                ("$db", databaseKey), ("$bucket", bucket), ("$ms", observed), ("$maint", maintenance ? observed : 0));
        }
    }

    // A maintenance interval's user activity goes to maint_* and does not set the last-read time.
    private static string IndexRollup(string table, string column) => $"""
        INSERT INTO index_{table} (index_key, {column}, user_reads, user_updates, system_reads, system_updates,
                                   maint_user_reads, maint_user_updates, lower_bound, last_user_read_ms)
        SELECT index_key, $bucket,
               CASE WHEN $m = 0 THEN user_seeks + user_scans + user_lookups ELSE 0 END,
               CASE WHEN $m = 0 THEN user_updates ELSE 0 END,
               system_seeks + system_scans + system_lookups,
               system_updates,
               CASE WHEN $m = 1 THEN user_seeks + user_scans + user_lookups ELSE 0 END,
               CASE WHEN $m = 1 THEN user_updates ELSE 0 END,
               lower_bound,
               CASE WHEN $m = 0 THEN last_user_read_ms END
        FROM index_delta WHERE interval_id = $id
        ON CONFLICT (index_key, {column}) DO UPDATE SET
            user_reads = user_reads + excluded.user_reads,
            user_updates = user_updates + excluded.user_updates,
            system_reads = system_reads + excluded.system_reads,
            system_updates = system_updates + excluded.system_updates,
            maint_user_reads = maint_user_reads + excluded.maint_user_reads,
            maint_user_updates = maint_user_updates + excluded.maint_user_updates,
            lower_bound = MAX(lower_bound, excluded.lower_bound),
            last_user_read_ms = CASE
                WHEN excluded.last_user_read_ms IS NULL THEN last_user_read_ms
                WHEN last_user_read_ms IS NULL THEN excluded.last_user_read_ms
                ELSE MAX(last_user_read_ms, excluded.last_user_read_ms) END
        """;

    private static string PartitionRollup(string table, string column) => $"""
        INSERT INTO part_{table} (partition_key, {column}, reads, writes, maint_reads, maint_writes, lower_bound)
        SELECT partition_key, $bucket,
               CASE WHEN $m = 0 THEN range_scans + singleton_lookups ELSE 0 END,
               CASE WHEN $m = 0 THEN leaf_inserts + leaf_updates + leaf_deletes ELSE 0 END,
               CASE WHEN $m = 1 THEN range_scans + singleton_lookups ELSE 0 END,
               CASE WHEN $m = 1 THEN leaf_inserts + leaf_updates + leaf_deletes ELSE 0 END,
               lower_bound
        FROM part_delta WHERE interval_id = $id
        ON CONFLICT (partition_key, {column}) DO UPDATE SET
            reads = reads + excluded.reads,
            writes = writes + excluded.writes,
            maint_reads = maint_reads + excluded.maint_reads,
            maint_writes = maint_writes + excluded.maint_writes,
            lower_bound = MAX(lower_bound, excluded.lower_bound)
        """;
}
