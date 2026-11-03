using Dormant.Core.Configuration;
using Dormant.Store.Ingestion;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.History;

/// <param name="RawIntervals">Raw intervals removed (with their deltas).</param>
/// <param name="HourlyRows">Hourly rollup rows removed.</param>
/// <param name="DailyRows">Daily rollup, event, size and collection rows removed.</param>
public sealed record PruneResult(int RawIntervals, int HourlyRows, int DailyRows);

/// <summary>
/// Removes history older than the configured retention. Raw intervals are removed only once rolled up, so pruning
/// never loses activity or coverage that the hourly and daily levels have not already absorbed (decision D-045).
/// </summary>
internal sealed class RetentionPruner
{
    private const string OldRaw = "SELECT interval_id FROM usage_interval WHERE to_ms < $cutoff AND rolled_up = 1";

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public RetentionPruner(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<PruneResult> PruneAsync(DateTimeOffset nowUtc, RetentionOptions retention, CancellationToken ct)
    {
        var rawCutoff = Sql.Ms(nowUtc - retention.Raw);
        var hourCutoff = Sql.Ms(nowUtc - retention.Hourly) / RollupWriter.MsPerHour;
        var dayCutoffMs = Sql.Ms(nowUtc - retention.Daily);
        var dayCutoff = dayCutoffMs / RollupWriter.MsPerDay;

        await DeleteAsync($"DELETE FROM index_delta WHERE interval_id IN ({OldRaw})", rawCutoff, ct);
        await DeleteAsync($"DELETE FROM part_delta WHERE interval_id IN ({OldRaw})", rawCutoff, ct);
        var raw = await DeleteAsync("DELETE FROM usage_interval WHERE to_ms < $cutoff AND rolled_up = 1", rawCutoff, ct);
        await DeleteAsync(
            "DELETE FROM db_observation WHERE collection_id IN (SELECT collection_id FROM collection WHERE started_ms < $cutoff)",
            rawCutoff,
            ct);

        var hourly = 0;
        foreach (var table in (string[])["db_hour", "index_hour", "part_hour"])
        {
            hourly += await DeleteAsync($"DELETE FROM {table} WHERE hour < $cutoff", hourCutoff, ct);
        }

        var daily = 0;
        foreach (var table in (string[])["db_day", "index_day", "part_day", "part_size"])
        {
            daily += await DeleteAsync($"DELETE FROM {table} WHERE day < $cutoff", dayCutoff, ct);
        }

        daily += await DeleteAsync("DELETE FROM history_event WHERE to_ms < $cutoff", dayCutoffMs, ct);

        // A database's history now starts where the retained history starts, so coverage is measured over what is kept.
        await DeleteAsync("UPDATE db SET first_seen_ms = $cutoff WHERE first_seen_ms < $cutoff", dayCutoff * RollupWriter.MsPerDay, ct);
        daily += await DeleteAsync("DELETE FROM part_unknown WHERE to_ms < $cutoff", dayCutoffMs, ct);
        const string OldPlans = "SELECT database_key, plan_id, plan_hash FROM qs_plan WHERE last_execution_ms < $cutoff";
        await DeleteAsync($"DELETE FROM qs_plan_object WHERE (database_key, plan_id, plan_hash) IN ({OldPlans})", dayCutoffMs, ct);
        daily += await DeleteAsync("DELETE FROM qs_plan WHERE last_execution_ms < $cutoff", dayCutoffMs, ct);
        daily += await DeleteAsync(
            "DELETE FROM collection WHERE started_ms < $cutoff AND collection_id NOT IN (SELECT collection_id FROM usage_interval)",
            dayCutoffMs,
            ct);
        return new PruneResult(raw, hourly, daily);
    }

    private Task<int> DeleteAsync(string sql, long cutoff, CancellationToken ct) =>
        Sql.ExecuteAsync(_connection, _transaction, sql, ct, ("$cutoff", cutoff));
}
