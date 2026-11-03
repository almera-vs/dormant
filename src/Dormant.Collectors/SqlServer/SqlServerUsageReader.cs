using Dormant.Core.Collection;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

/// <summary>
/// Reads identity, index metadata, usage counters, partition layout and size, and operational counters for one
/// database in a single round trip. Only catalog views and DMVs are read; no user table is touched.
/// </summary>
/// <remarks>
/// DMVs are always read in statements of their own and joined here, never joined to catalog views in SQL: the optimizer
/// may evaluate a DMV once per joined row (measured: 26 s instead of 16 ms for 1,600 partitions). Joining in the agent
/// also avoids temporary tables, which would be writes to tempdb (decision D-040).
/// </remarks>
internal static class SqlServerUsageReader
{
    // database_guid survives renames and detach/attach (docs/sqlserver-internals.md). Filtering on DB_ID() inside the
    // database's own context keeps the read to the database Dormant is already in.
    private const string IdentityByGuid = """
        SELECT CONVERT(nvarchar(36), rs.database_guid), SYSUTCDATETIME(), DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME())
        FROM sys.database_recovery_status AS rs
        WHERE rs.database_id = DB_ID();
        """;

    // Azure SQL Database has no sys.database_recovery_status. create_date is kept across renames there and changes
    // on restore or copy, which is the identity Dormant wants.
    private const string IdentityByCreateDate = """
        SELECT CONVERT(nvarchar(30), d.create_date, 126), SYSUTCDATETIME(), DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME())
        FROM sys.databases AS d
        WHERE d.name = DB_NAME();
        """;

    private const string UserObjects = "o.is_ms_shipped = 0 AND o.type IN ('U', 'V')";

    // Every index of every user table and indexed view.
    private const string Indexes = $"""
        SELECT o.object_id, o.create_date, s.name, o.name, o.type,
               i.index_id, i.name, i.type_desc, i.is_primary_key, i.is_unique, i.is_unique_constraint, i.is_disabled,
               CAST(COALESCE(t.is_memory_optimized, 0) AS bit)
        FROM sys.indexes AS i
        JOIN sys.objects AS o ON o.object_id = i.object_id
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.tables AS t ON t.object_id = o.object_id
        WHERE {UserObjects} AND i.is_hypothetical = 0
        ORDER BY o.object_id, i.index_id;
        """;

    private const string Usage = """
        SELECT u.object_id, u.index_id,
               u.user_seeks, u.user_scans, u.user_lookups, u.user_updates,
               u.system_seeks, u.system_scans, u.system_lookups, u.system_updates,
               u.last_user_seek, u.last_user_scan, u.last_user_lookup, u.last_user_update
        FROM sys.dm_db_index_usage_stats AS u
        WHERE u.database_id = DB_ID();
        """;

    // Every partition with its compression and boundary values. Boundary k separates partitions k and k+1, so a
    // partition's lower boundary is boundary (n - 1) and its upper boundary is boundary n. Values are converted to exact,
    // culture-free text per type, because they become part of the partition's identity.
    private static readonly string Partitions = $"""
        SELECT p.object_id, p.index_id, p.partition_number, p.partition_id, p.data_compression_desc, p.rows,
               CASE WHEN pf.function_id IS NULL THEN 0 ELSE 1 END, CAST(COALESCE(pf.boundary_value_on_right, 1) AS bit),
               CAST(SQL_VARIANT_PROPERTY(lo.value, 'BaseType') AS nvarchar(128)), {CanonicalValue("lo.value")},
               CAST(SQL_VARIANT_PROPERTY(hi.value, 'BaseType') AS nvarchar(128)), {CanonicalValue("hi.value")},
               c.name
        FROM sys.partitions AS p
        JOIN sys.objects AS o ON o.object_id = p.object_id
        JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
        LEFT JOIN sys.partition_schemes AS sch ON sch.data_space_id = i.data_space_id
        LEFT JOIN sys.partition_functions AS pf ON pf.function_id = sch.function_id
        LEFT JOIN sys.partition_range_values AS lo ON lo.function_id = pf.function_id AND lo.boundary_id = p.partition_number - 1
        LEFT JOIN sys.partition_range_values AS hi ON hi.function_id = pf.function_id AND hi.boundary_id = p.partition_number
        LEFT JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.partition_ordinal = 1
        LEFT JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE {UserObjects} AND i.is_hypothetical = 0;
        """;

    private const string Sizes = """
        SELECT ps.partition_id, ps.row_count, ps.reserved_page_count, ps.used_page_count
        FROM sys.dm_db_partition_stats AS ps;
        """;

    // Operational counters exist only while an object's metadata is cached. Columnstore internal rowsets are summed into
    // their partition: activity there is activity on the partition.
    private const string Operations = """
        SELECT os.object_id, os.index_id, os.partition_number,
               SUM(os.range_scan_count), SUM(os.singleton_lookup_count),
               SUM(os.leaf_insert_count), SUM(os.leaf_update_count), SUM(os.leaf_delete_count + os.leaf_ghost_count)
        FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS os
        GROUP BY os.object_id, os.index_id, os.partition_number;
        """;

    /// <param name="database">The database to run in over a shared connection, or null when connected to it directly.</param>
    public static async Task<DatabaseSnapshot> ReadAsync(
        SqlConnection connection,
        string? database,
        DatabaseRow row,
        bool identityByCreateDate,
        EngineIncarnation engine,
        CancellationToken ct)
    {
        var sql = (identityByCreateDate ? IdentityByCreateDate : IdentityByGuid) + Indexes + Usage + Partitions + Sizes + Operations;
        await using var command = SqlServerConnections.CreateCommandIn(connection, database, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0))
        {
            throw new InvalidOperationException($"Database {row.Name} returned no identity.");
        }

        var identity = identityByCreateDate ? "azure:" + reader.GetString(0) : "guid:" + reader.GetString(0).ToLowerInvariant();
        var observedUtc = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
        var localOffset = RoundToQuarterHour(reader.GetInt32(2));

        await reader.NextResultAsync(ct);
        var indexes = await ReadIndexesAsync(reader, ct);

        await reader.NextResultAsync(ct);
        var usage = await ReadUsageAsync(reader, localOffset, ct);

        await reader.NextResultAsync(ct);
        var partitions = await ReadPartitionsAsync(reader, ct);

        await reader.NextResultAsync(ct);
        var sizes = await ReadSizesAsync(reader, ct);

        await reader.NextResultAsync(ct);
        var operations = await ReadOperationsAsync(reader, ct);

        var observations = indexes
            .Select(index =>
            {
                var key = (index.Table.ObjectId, index.IndexId);
                var parts = partitions.TryGetValue(key, out var list)
                    ? list.OrderBy(p => p.PartitionNumber)
                        .Select(p => WithSize(p, sizes) with { Operations = operations.GetValueOrDefault((key.ObjectId, key.IndexId, p.PartitionNumber)) })
                        .ToList()
                    : [];
                return index with { Usage = usage.GetValueOrDefault(key), Partitions = parts };
            })
            .ToList();

        return new DatabaseSnapshot(identity, row.Name, row.DatabaseId, DatabaseOutcome.Collected, SkipReason.None, null, observedUtc, engine, observations);
    }

    private static async Task<List<IndexObservation>> ReadIndexesAsync(SqlDataReader reader, CancellationToken ct)
    {
        var indexes = new List<IndexObservation>();
        ObjectRef? current = null;
        while (await reader.ReadAsync(ct))
        {
            var objectId = reader.GetInt32(0);
            if (current is null || current.ObjectId != objectId)
            {
                current = new ObjectRef(objectId, reader.GetDateTime(1), reader.GetString(2), reader.GetString(3), reader.GetString(4).Trim(), reader.GetBoolean(12));
            }

            indexes.Add(new IndexObservation(
                current,
                IndexId: reader.GetInt32(5),
                IndexName: reader.IsDBNull(6) ? null : reader.GetString(6),
                IndexType: reader.GetString(7),
                IsPrimaryKey: reader.GetBoolean(8),
                IsUnique: reader.GetBoolean(9),
                IsUniqueConstraint: reader.GetBoolean(10),
                IsDisabled: reader.GetBoolean(11),
                Usage: null,
                Partitions: []));
        }

        return indexes;
    }

    private static async Task<Dictionary<(int, int), UsageCounters>> ReadUsageAsync(SqlDataReader reader, TimeSpan localOffset, CancellationToken ct)
    {
        var result = new Dictionary<(int, int), UsageCounters>();
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetInt32(0), reader.GetInt32(1))] = new UsageCounters(
                UserSeeks: reader.GetInt64(2),
                UserScans: reader.GetInt64(3),
                UserLookups: reader.GetInt64(4),
                UserUpdates: reader.GetInt64(5),
                SystemSeeks: reader.GetInt64(6),
                SystemScans: reader.GetInt64(7),
                SystemLookups: reader.GetInt64(8),
                SystemUpdates: reader.GetInt64(9),
                LastUserReadUtc: Latest(ToUtc(reader, 10, localOffset), ToUtc(reader, 11, localOffset), ToUtc(reader, 12, localOffset)),
                LastUserUpdateUtc: ToUtc(reader, 13, localOffset));
        }

        return result;
    }

    private static async Task<Dictionary<(int ObjectId, int IndexId), List<PartitionObservation>>> ReadPartitionsAsync(SqlDataReader reader, CancellationToken ct)
    {
        var result = new Dictionary<(int, int), List<PartitionObservation>>();
        while (await reader.ReadAsync(ct))
        {
            var key = (reader.GetInt32(0), reader.GetInt32(1));
            var range = reader.GetInt32(6) == 0
                ? PartitionRange.Whole
                : new PartitionRange(
                    IsPartitioned: true,
                    RangeRight: reader.GetBoolean(7),
                    Lower: Boundary(reader, 8),
                    Upper: Boundary(reader, 10),
                    Column: reader.IsDBNull(12) ? null : reader.GetString(12));

            var partition = new PartitionObservation(
                PartitionNumber: reader.GetInt32(2),
                PartitionId: reader.GetInt64(3),
                range,
                RowCount: reader.GetInt64(5),
                ReservedPages: 0,
                UsedPages: 0,
                Compression: reader.IsDBNull(4) ? "NONE" : reader.GetString(4),
                Operations: null);

            if (!result.TryGetValue(key, out var list))
            {
                result[key] = list = [];
            }

            list.Add(partition);
        }

        return result;
    }

    private static async Task<Dictionary<long, (long Rows, long Reserved, long Used)>> ReadSizesAsync(SqlDataReader reader, CancellationToken ct)
    {
        var result = new Dictionary<long, (long, long, long)>();
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }

        return result;
    }

    // A partition without a size row keeps the row count from sys.partitions and zero pages.
    private static PartitionObservation WithSize(PartitionObservation partition, Dictionary<long, (long Rows, long Reserved, long Used)> sizes) =>
        sizes.TryGetValue(partition.PartitionId, out var size)
            ? partition with { RowCount = size.Rows, ReservedPages = size.Reserved, UsedPages = size.Used }
            : partition;

    private static async Task<Dictionary<(int, int, int), OperationalCounters>> ReadOperationsAsync(SqlDataReader reader, CancellationToken ct)
    {
        var result = new Dictionary<(int, int, int), OperationalCounters>();
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))] = new OperationalCounters(
                RangeScans: reader.GetInt64(3),
                SingletonLookups: reader.GetInt64(4),
                LeafInserts: reader.GetInt64(5),
                LeafUpdates: reader.GetInt64(6),
                LeafDeletes: reader.GetInt64(7));
        }

        return result;
    }

    private static BoundaryValue? Boundary(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || reader.IsDBNull(ordinal + 1)
            ? null
            : new BoundaryValue(reader.GetString(ordinal), reader.GetString(ordinal + 1));

    /// <summary>T-SQL that turns a sql_variant boundary value into exact, culture-free text according to its base type.</summary>
    internal static string CanonicalValue(string value) => $"""
        CASE CAST(SQL_VARIANT_PROPERTY({value}, 'BaseType') AS nvarchar(128))
            WHEN N'date' THEN CONVERT(nvarchar(4000), CONVERT(date, {value}), 23)
            WHEN N'smalldatetime' THEN CONVERT(nvarchar(4000), CONVERT(datetime2(0), {value}), 126)
            WHEN N'datetime' THEN CONVERT(nvarchar(4000), CONVERT(datetime2(3), {value}), 126)
            WHEN N'datetime2' THEN CONVERT(nvarchar(4000), CONVERT(datetime2(7), {value}), 126)
            WHEN N'datetimeoffset' THEN CONVERT(nvarchar(4000), CONVERT(datetimeoffset(7), {value}), 126)
            WHEN N'time' THEN CONVERT(nvarchar(4000), CONVERT(time(7), {value}))
            WHEN N'float' THEN CONVERT(nvarchar(4000), CONVERT(float, {value}), 3)
            WHEN N'real' THEN CONVERT(nvarchar(4000), CONVERT(real, {value}), 3)
            WHEN N'money' THEN CONVERT(nvarchar(4000), CONVERT(money, {value}), 2)
            WHEN N'smallmoney' THEN CONVERT(nvarchar(4000), CONVERT(smallmoney, {value}), 2)
            WHEN N'binary' THEN CONVERT(nvarchar(4000), CONVERT(varbinary(8000), {value}), 1)
            WHEN N'varbinary' THEN CONVERT(nvarchar(4000), CONVERT(varbinary(8000), {value}), 1)
            ELSE CONVERT(nvarchar(4000), {value})
        END
        """;

    // The last_user_* columns are server-local datetime. Converting with today's offset can be off by the daylight-saving
    // difference for timestamps from the other half of the year; they are supporting evidence, not the counters.
    private static DateTimeOffset? ToUtc(SqlDataReader reader, int ordinal, TimeSpan localOffset) =>
        reader.IsDBNull(ordinal)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Unspecified), localOffset).ToUniversalTime();

    private static DateTimeOffset? Latest(params DateTimeOffset?[] values) => values.Max();

    private static TimeSpan RoundToQuarterHour(int minutes) =>
        TimeSpan.FromMinutes(Math.Round(minutes / 15.0, MidpointRounding.AwayFromZero) * 15);
}
