using Microsoft.Data.Sqlite;

namespace Dormant.Store.Tests;

/// <summary>Reads the store's tables directly, read-only, so tests can check what was written.</summary>
internal sealed class StoreInspector
{
    private readonly string _connectionString;

    public StoreInspector(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString;
    }

    public sealed record IntervalRow(string Database, string Kind, DateTimeOffset From, DateTimeOffset To, DateTimeOffset? ObservedFrom, string? Reason);

    public sealed record DeltaRow(string Database, string Table, long ObjectKey, string? Index, long IndexKey, long UserReads, bool LowerBound, string Kind);

    public sealed record ObjectRow(long ObjectKey, string Database, string Schema, string Name, int ObjectId, string CreateDate);

    public Task<List<IntervalRow>> IntervalsAsync(string database) =>
        QueryAsync(
            """
            SELECT d.name, i.kind, i.from_ms, i.to_ms, i.observed_from_ms, i.reason
            FROM usage_interval AS i JOIN db AS d ON d.database_key = i.database_key
            WHERE d.name = $name ORDER BY i.to_ms
            """,
            r => new IntervalRow(
                r.GetString(0), r.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
                r.IsDBNull(5) ? null : r.GetString(5)),
            ("$name", database));

    public Task<List<DeltaRow>> DeltasAsync(string database) =>
        QueryAsync(
            """
            SELECT d.name, o.name, o.object_key, x.name, x.index_key, t.user_seeks + t.user_scans + t.user_lookups, t.lower_bound, i.kind
            FROM index_delta AS t
            JOIN usage_interval AS i ON i.interval_id = t.interval_id
            JOIN idx AS x ON x.index_key = t.index_key
            JOIN obj AS o ON o.object_key = x.object_key
            JOIN db AS d ON d.database_key = o.database_key
            WHERE d.name = $name ORDER BY i.to_ms, o.name, x.index_id
            """,
            r => new DeltaRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetBoolean(6), r.GetString(7)),
            ("$name", database));

    public Task<List<ObjectRow>> ObjectsAsync() =>
        QueryAsync(
            """
            SELECT o.object_key, d.name, o.schema_name, o.name, o.object_id, o.create_date
            FROM obj AS o JOIN db AS d ON d.database_key = o.database_key ORDER BY o.object_key
            """,
            r => new ObjectRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5)));

    public sealed record PartitionRow(long PartitionKey, string Table, string? Index, string Label, string RangeText, int Number, long RowCount, long ReservedPages, DateTimeOffset LastSeen);

    public sealed record PartitionDeltaRow(long PartitionKey, string Label, long Reads, long Inserts, bool LowerBound);

    public sealed record UnknownRow(long PartitionKey, DateTimeOffset From, DateTimeOffset To);

    public Task<List<PartitionRow>> PartitionsAsync() =>
        QueryAsync(
            """
            SELECT p.partition_key, o.name, x.name, p.label, p.range_text, p.partition_number, p.row_count, p.reserved_pages, p.last_seen_ms
            FROM part AS p JOIN idx AS x ON x.index_key = p.index_key JOIN obj AS o ON o.object_key = x.object_key
            ORDER BY p.partition_key
            """,
            r => new PartitionRow(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetInt32(5), r.GetInt64(6), r.GetInt64(7), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8))));

    public Task<List<PartitionDeltaRow>> PartitionDeltasAsync() =>
        QueryAsync(
            """
            SELECT d.partition_key, p.label, d.range_scans + d.singleton_lookups, d.leaf_inserts, d.lower_bound
            FROM part_delta AS d JOIN part AS p ON p.partition_key = d.partition_key
            JOIN usage_interval AS i ON i.interval_id = d.interval_id
            ORDER BY i.to_ms, d.partition_key
            """,
            r => new PartitionDeltaRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetBoolean(4)));

    public Task<List<UnknownRow>> UnknownAsync() =>
        QueryAsync(
            "SELECT partition_key, from_ms, to_ms FROM part_unknown ORDER BY partition_key, from_ms",
            r => new UnknownRow(r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))));

    public sealed record ObservedRow(long Bucket, TimeSpan Observed, TimeSpan Maintenance);

    public sealed record IndexBucketRow(string? Index, long Bucket, long UserReads, long MaintUserReads, long SystemReads, bool LowerBound);

    public sealed record EventRow(string Database, string Kind, DateTimeOffset At, TimeSpan Duration, string? Reason);

    /// <param name="level">"hour" or "day".</param>
    public Task<List<ObservedRow>> ObservedAsync(string level) =>
        QueryAsync(
            $"SELECT {level}, observed_ms, maintenance_ms FROM db_{level} ORDER BY {level}",
            r => new ObservedRow(r.GetInt64(0), TimeSpan.FromMilliseconds(r.GetInt64(1)), TimeSpan.FromMilliseconds(r.GetInt64(2))));

    /// <param name="level">"hour" or "day".</param>
    public Task<List<IndexBucketRow>> IndexBucketsAsync(string level) =>
        QueryAsync(
            $"""
            SELECT x.name, b.{level}, b.user_reads, b.maint_user_reads, b.system_reads, b.lower_bound
            FROM index_{level} AS b JOIN idx AS x ON x.index_key = b.index_key ORDER BY b.{level}, x.name
            """,
            r => new IndexBucketRow(r.IsDBNull(0) ? null : r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetBoolean(5)));

    public Task<List<EventRow>> EventsAsync() =>
        QueryAsync(
            """
            SELECT d.name, e.kind, e.at_ms, e.to_ms - e.from_ms, e.reason
            FROM history_event AS e JOIN db AS d ON d.database_key = e.database_key ORDER BY e.at_ms
            """,
            r => new EventRow(r.GetString(0), r.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                TimeSpan.FromMilliseconds(r.GetInt64(3)), r.IsDBNull(4) ? null : r.GetString(4)));

    public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
