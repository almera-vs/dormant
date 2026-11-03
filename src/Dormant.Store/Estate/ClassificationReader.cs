using Dormant.Core.Usage;
using Dormant.Core.Configuration;
using Dormant.Store.History;
using Dormant.Store.Ingestion;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Estate;

/// <summary>
/// Condenses the rollups into usage evidence for every current table, partition and index, and classifies them
/// (decisions D-048 to D-051). Only objects seen in each database's latest reading are current.
/// </summary>
internal sealed class ClassificationReader
{
    private const long MsPerDay = RollupWriter.MsPerDay;

    private readonly SqliteConnection _connection;
    private readonly DormantConfig _config;
    private readonly long _today;
    private readonly TimeSpan _elapsedToday;
    private readonly long _hotStart;
    private readonly long _warmStart;
    private readonly long _dormantStart;
    private readonly Classifier _classifier;

    public ClassificationReader(SqliteConnection connection, DormantConfig config, DateTimeOffset nowUtc)
    {
        _connection = connection;
        _config = config;
        var nowMs = Sql.Ms(nowUtc);
        _today = nowMs / MsPerDay;
        _elapsedToday = TimeSpan.FromMilliseconds(nowMs - (_today * MsPerDay));
        var options = config.Classification;
        _hotStart = _today - options.HotDays + 1;
        _warmStart = _today - options.WarmDays + 1;
        _dormantStart = _today - options.DormantDays + 1;
        _classifier = new Classifier(options);
    }

    /// <param name="historyDays">Also read user reads day by day over this many UTC days ending today; 0 for none.</param>
    public async Task<EstateClassification> ReadAsync(DateTimeOffset nowUtc, int historyDays, CancellationToken ct)
    {
        var databases = await DatabasesAsync(ct);
        var observed = await ObservedPerDayAsync(ct);
        var tables = await TablesAsync(ct);
        var indexes = await IndexesAsync(ct);
        var partitions = await PartitionsAsync(ct);
        var tableActivity = await ActivityAsync(TableActivitySql, ct);
        var indexActivity = await ActivityAsync(IndexActivitySql, ct);
        var partitionActivity = await PartitionActivityAsync(ct);
        var engine = await EngineLastActivityAsync(databases, ct);
        var tableReadDays = await TableReadDaysAsync(ct);
        var unknown = await UnknownSpansAsync(_dormantStart, ct);
        var lastReaders = await new Store.Attribution.AttributionReader(_connection).LastReadersAsync(ct);
        var daily = historyDays > 0
            ? await new DailyUsageReader(_connection, _today - historyDays + 1, _today, _elapsedToday).ReadAsync(ct)
            : null;

        var indexesByTable = indexes.ToLookup(i => i.ObjectKey);
        var partitionsByIndex = partitions.ToLookup(p => p.IndexKey);

        var classified = new List<ClassifiedTable>();
        var unused = new List<UnusedIndex>();
        foreach (var table in tables)
        {
            var database = databases[table.DatabaseKey];
            var days = observed.GetValueOrDefault(table.DatabaseKey) ?? new long[_today - _dormantStart + 1];
            var tableIndexes = indexesByTable[table.ObjectKey].ToList();
            var allPartitions = tableIndexes.SelectMany(i => partitionsByIndex[i.IndexKey]).ToList();

            var tableEvidence = Evidence(
                tableActivity.GetValueOrDefault(table.ObjectKey),
                tableIndexes.Select(i => engine.GetValueOrDefault(i.IndexKey).Read).Max(),
                tableIndexes.Select(i => engine.GetValueOrDefault(i.IndexKey).Write).Max(),
                days,
                table.FirstSeenMs);
            var tableClass = table.IsMemoryOptimized
                ? MemoryOptimized(tableEvidence)
                : _classifier.Classify(tableEvidence, _today, _elapsedToday);

            // Data partitions: those of the heap or clustered index; aligned indexes add their share of each range.
            var dataIndex = tableIndexes.Where(i => i.IndexId <= 1).OrderBy(i => i.IndexId).FirstOrDefault();
            var ranges = new List<ClassifiedPartition>();
            var dataPartitions = dataIndex is null ? [] : partitionsByIndex[dataIndex.IndexKey].OrderBy(p => p.Number).ToList();
            foreach (var data in dataPartitions)
            {
                var sameRange = allPartitions.Where(p => p.RangeKey == data.RangeKey).ToList();
                var key = (table.ObjectKey, data.RangeKey);
                var classification = tableClass;
                if (data.RangeKey != "all" && !table.IsMemoryOptimized)
                {
                    var adjusted = ObservedTime.Adjust(days, _dormantStart, tableReadDays.GetValueOrDefault(table.ObjectKey), unknown.GetValueOrDefault(key));
                    var evidence = Evidence(partitionActivity.GetValueOrDefault(key), engineLastReadMs: null, engineLastWriteMs: null, adjusted, data.FirstSeenMs);
                    classification = _classifier.Classify(evidence, _today, _elapsedToday);
                }

                ranges.Add(new ClassifiedPartition(
                    data.Label,
                    data.RangeText,
                    data.Number,
                    data.Rows,
                    sameRange.Sum(p => p.ReservedPages) * 8192,
                    classification,
                    data.RangeKey == "all" ? null : daily?.ForPartition(table.DatabaseKey, table.ObjectKey, data.RangeKey, data.FirstSeenMs)));
            }

            var classifiedIndexes = new List<ClassifiedIndex>();
            foreach (var index in tableIndexes.OrderBy(i => i.IndexId))
            {
                var (engineRead, engineWrite) = engine.GetValueOrDefault(index.IndexKey);
                var evidence = Evidence(indexActivity.GetValueOrDefault(index.IndexKey), engineRead, engineWrite, days, index.FirstSeenMs);
                var bytes = partitionsByIndex[index.IndexKey].Sum(p => p.ReservedPages) * 8192;
                var unusedCoverage = !table.IsMemoryOptimized && IsCandidateForUnused(index) ? UnusedIndexRule(evidence) : null;
                if (unusedCoverage is { } coverage)
                {
                    unused.Add(new UnusedIndex(
                        database.Instance,
                        database.Name,
                        table.Schema,
                        table.Name,
                        index.Name!,
                        bytes,
                        evidence.UpdatesInWarmWindow,
                        coverage,
                        $"DROP INDEX {Quote(index.Name!)} ON {Quote(table.Schema)}.{Quote(table.Name)};"));
                }

                classifiedIndexes.Add(new ClassifiedIndex(
                    index.Name,
                    index.IndexId,
                    index.Type,
                    index.IsPrimaryKey,
                    index.IsUnique,
                    index.IsUniqueConstraint,
                    index.IsDisabled,
                    bytes,
                    evidence.UpdatesInWarmWindow,
                    table.IsMemoryOptimized ? MemoryOptimized(evidence) : _classifier.Classify(evidence, _today, _elapsedToday),
                    unusedCoverage is not null));
            }

            classified.Add(new ClassifiedTable(
                database.Instance,
                database.Name,
                table.Schema,
                table.Name,
                dataIndex is null ? 0 : partitionsByIndex[dataIndex.IndexKey].Sum(p => p.Rows),
                allPartitions.Sum(p => p.ReservedPages) * 8192,
                tableClass,
                ranges,
                classifiedIndexes,
                lastReaders.GetValueOrDefault(table.ObjectKey),
                daily?.ForTable(table.DatabaseKey, table.ObjectKey, table.FirstSeenMs)));
        }

        return new EstateClassification(nowUtc, classified, unused, historyDays);
    }

    /// <summary>
    /// Only nonclustered indexes that enforce nothing: never primary keys, unique indexes, unique constraints, clustered
    /// indexes or heaps; never disabled indexes; never index types the usage DMV does not report (spatial) or whose
    /// usage it reports unreliably (XML). Fault scenario 11.
    /// </summary>
    private static bool IsCandidateForUnused(StoredIndex index) =>
        index.Type is "NONCLUSTERED" or "NONCLUSTERED COLUMNSTORE"
        && index.Name is not null
        && !index.IsPrimaryKey && !index.IsUnique && !index.IsUniqueConstraint && !index.IsDisabled;

    /// <summary>Unused: no user reads in the warm window, observed enough, and written to. Returns the coverage, or null.</summary>
    private double? UnusedIndexRule(UsageEvidence evidence)
    {
        var options = _config.Classification;
        var coverage = Classifier.Coverage(evidence.ObservedInWarmWindow, options.WarmDays, _elapsedToday);
        var readInWindow = evidence.LastReadDay is { } day && day >= _warmStart;
        return !readInWindow && coverage >= options.MinCoverage && evidence.UpdatesInWarmWindow > 0 ? coverage : null;
    }

    private Classification MemoryOptimized(UsageEvidence evidence)
    {
        var options = _config.Classification;
        return new Classification(
            UsageClass.InsufficientHistory,
            Classifier.Coverage(evidence.ObservedInWarmWindow, options.WarmDays, _elapsedToday),
            Classifier.Coverage(evidence.ObservedInDormantWindow, options.DormantDays, _elapsedToday),
            evidence.LastReadDay,
            WriteOnly: false,
            "memory-optimized table: SQL Server does not report its index usage, so Dormant cannot tell whether it is read",
            evidence.LastWriteDay);
    }

    private UsageEvidence Evidence(Activity? activity, long? engineLastReadMs, long? engineLastWriteMs, long[] observedPerDay, long firstSeenMs)
    {
        // Only whole days after the object was first seen count: before that it may not have existed.
        var firstDay = (firstSeenMs % MsPerDay == 0) ? firstSeenMs / MsPerDay : (firstSeenMs / MsPerDay) + 1;
        return new UsageEvidence(
            activity?.ReadDaysInHotWindow ?? 0,
            Latest(activity?.LastReadDay, activity?.LastReadMs, engineLastReadMs),
            Sum(observedPerDay, Math.Max(_warmStart, firstDay)),
            Sum(observedPerDay, Math.Max(_dormantStart, firstDay)),
            activity?.UpdatesInWarmWindow ?? 0,
            activity?.MaintenanceReads ?? 0,
            Latest(activity?.LastWriteDay, null, engineLastWriteMs));
    }

    /// <summary>The latest UTC day among a rollup day and two millisecond timestamps.</summary>
    private static long? Latest(long? day, long? ms1, long? ms2)
    {
        foreach (var ms in (long?[])[ms1, ms2])
        {
            if (ms is { } value && (day is null || value / MsPerDay > day))
            {
                day = value / MsPerDay;
            }
        }

        return day;
    }

    private TimeSpan Sum(long[] observedPerDay, long fromDay)
    {
        var total = 0L;
        for (var day = Math.Max(fromDay, _dormantStart); day <= _today; day++)
        {
            total += observedPerDay[day - _dormantStart];
        }

        return TimeSpan.FromMilliseconds(total);
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record DatabaseInfo(string Instance, string Name, IReadOnlyList<MaintenanceWindow> Windows);

    private sealed record StoredTable(long ObjectKey, long DatabaseKey, string Schema, string Name, bool IsMemoryOptimized, long FirstSeenMs);

    private sealed record StoredIndex(
        long IndexKey, long ObjectKey, int IndexId, string? Name, string Type, bool IsPrimaryKey, bool IsUnique, bool IsUniqueConstraint,
        bool IsDisabled, long FirstSeenMs);

    private sealed record StoredPartition(
        long IndexKey, string RangeKey, string Label, string RangeText, int Number, long Rows, long ReservedPages, long FirstSeenMs);

    private sealed record Activity(
        int ReadDaysInHotWindow, long? LastReadDay, long? LastReadMs, long UpdatesInWarmWindow, long MaintenanceReads, long? LastWriteDay);

    private async Task<Dictionary<long, DatabaseInfo>> DatabasesAsync(CancellationToken ct)
    {
        var windows = _config.Instances.ToDictionary(i => i.Name, i => i.MaintenanceWindows, StringComparer.Ordinal);
        var result = new Dictionary<long, DatabaseInfo>();
        await ReadAsync(
            "SELECT d.database_key, i.name, d.name FROM db AS d JOIN instance AS i ON i.instance_key = d.instance_key",
            r => result[r.GetInt64(0)] = new DatabaseInfo(r.GetString(1), r.GetString(2), windows.GetValueOrDefault(r.GetString(1)) ?? []),
            ct);
        return result;
    }

    private async Task<Dictionary<long, long[]>> ObservedPerDayAsync(CancellationToken ct)
    {
        var result = new Dictionary<long, long[]>();
        await ReadAsync(
            "SELECT database_key, day, observed_ms FROM db_day WHERE day BETWEEN $from AND $to",
            r =>
            {
                if (!result.TryGetValue(r.GetInt64(0), out var days))
                {
                    result[r.GetInt64(0)] = days = new long[_today - _dormantStart + 1];
                }

                days[r.GetInt64(1) - _dormantStart] = r.GetInt64(2);
            },
            ct,
            ("$from", _dormantStart),
            ("$to", _today));
        return result;
    }

    // Current: seen in the database's latest reading.
    private async Task<List<StoredTable>> TablesAsync(CancellationToken ct)
    {
        var result = new List<StoredTable>();
        await ReadAsync(
            """
            SELECT o.object_key, o.database_key, o.schema_name, o.name, o.is_memory_optimized, o.first_seen_ms
            FROM obj AS o JOIN db_baseline AS b ON b.database_key = o.database_key
            WHERE o.last_seen_ms >= b.observed_ms
            ORDER BY o.database_key, o.schema_name, o.name
            """,
            r => result.Add(new StoredTable(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt64(5))),
            ct);
        return result;
    }

    private async Task<List<StoredIndex>> IndexesAsync(CancellationToken ct)
    {
        var result = new List<StoredIndex>();
        await ReadAsync(
            """
            SELECT x.index_key, x.object_key, x.index_id, x.name, x.type, x.is_primary_key, x.is_unique, x.is_unique_constraint,
                   x.is_disabled, x.first_seen_ms
            FROM idx AS x JOIN obj AS o ON o.object_key = x.object_key JOIN db_baseline AS b ON b.database_key = o.database_key
            WHERE x.last_seen_ms >= b.observed_ms
            """,
            r => result.Add(new StoredIndex(
                r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8), r.GetInt64(9))),
            ct);
        return result;
    }

    private async Task<List<StoredPartition>> PartitionsAsync(CancellationToken ct)
    {
        var result = new List<StoredPartition>();
        await ReadAsync(
            """
            SELECT p.index_key, p.range_key, p.label, p.range_text, p.partition_number, p.row_count, p.reserved_pages, p.first_seen_ms
            FROM part AS p JOIN idx AS x ON x.index_key = p.index_key JOIN obj AS o ON o.object_key = x.object_key
            JOIN db_baseline AS b ON b.database_key = o.database_key
            WHERE p.last_seen_ms >= b.observed_ms
            """,
            r => result.Add(new StoredPartition(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7))),
            ct);
        return result;
    }

    private const string ActivityColumns = """
        COUNT(DISTINCT CASE WHEN b.day >= $hot AND b.user_reads > 0 THEN b.day END),
        MAX(CASE WHEN b.user_reads > 0 THEN b.day END),
        MAX(b.last_user_read_ms),
        SUM(CASE WHEN b.day >= $warm THEN b.user_updates ELSE 0 END),
        SUM(CASE WHEN b.day >= $dormant THEN b.maint_user_reads ELSE 0 END),
        MAX(CASE WHEN b.user_updates > 0 THEN b.day END)
        """;

    private const string TableActivitySql = $"""
        SELECT x.object_key, {ActivityColumns}
        FROM index_day AS b JOIN idx AS x ON x.index_key = b.index_key
        GROUP BY x.object_key
        """;

    private const string IndexActivitySql = $"""
        SELECT b.index_key, {ActivityColumns}
        FROM index_day AS b
        GROUP BY b.index_key
        """;

    private async Task<Dictionary<long, Activity>> ActivityAsync(string sql, CancellationToken ct)
    {
        var result = new Dictionary<long, Activity>();
        await ReadAsync(
            sql,
            r => result[r.GetInt64(0)] = new Activity(
                r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetInt64(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6)),
            ct,
            ("$hot", _hotStart), ("$warm", _warmStart), ("$dormant", _dormantStart));
        return result;
    }

    // Partition activity by table and range, across the table's aligned indexes. Writes stand in for updates.
    private async Task<Dictionary<(long, string), Activity>> PartitionActivityAsync(CancellationToken ct)
    {
        var result = new Dictionary<(long, string), Activity>();
        await ReadAsync(
            """
            SELECT x.object_key, p.range_key,
                   COUNT(DISTINCT CASE WHEN b.day >= $hot AND b.reads > 0 THEN b.day END),
                   MAX(CASE WHEN b.reads > 0 THEN b.day END),
                   SUM(CASE WHEN b.day >= $warm THEN b.writes ELSE 0 END),
                   SUM(CASE WHEN b.day >= $dormant THEN b.maint_reads ELSE 0 END),
                   MAX(CASE WHEN b.writes > 0 THEN b.day END)
            FROM part_day AS b JOIN part AS p ON p.partition_key = b.partition_key JOIN idx AS x ON x.index_key = p.index_key
            GROUP BY x.object_key, p.range_key
            """,
            r => result[(r.GetInt64(0), r.GetString(1))] = new Activity(
                r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), null, r.GetInt64(4), r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6)),
            ct,
            ("$hot", _hotStart), ("$warm", _warmStart), ("$dormant", _dormantStart));
        return result;
    }

    /// <summary>
    /// The engine's own last user read and write times from the latest reading, which can predate Dormant's history. A
    /// time inside a configured maintenance window is not evidence of use (scenario 9).
    /// </summary>
    private async Task<Dictionary<long, (long? Read, long? Write)>> EngineLastActivityAsync(Dictionary<long, DatabaseInfo> databases, CancellationToken ct)
    {
        var result = new Dictionary<long, (long?, long?)>();
        await ReadAsync(
            """
            SELECT ib.index_key, ib.database_key, ib.last_user_read_ms, ib.last_user_update_ms
            FROM index_baseline AS ib WHERE ib.last_user_read_ms IS NOT NULL OR ib.last_user_update_ms IS NOT NULL
            """,
            r =>
            {
                var windows = databases.TryGetValue(r.GetInt64(1), out var db) ? db.Windows : [];
                long? OutsideMaintenance(int ordinal) =>
                    r.IsDBNull(ordinal) || windows.Any(w => w.Contains(Sql.Time(r.GetInt64(ordinal)))) ? null : r.GetInt64(ordinal);
                result[r.GetInt64(0)] = (OutsideMaintenance(2), OutsideMaintenance(3));
            },
            ct);
        return result;
    }

    private async Task<Dictionary<long, HashSet<long>>> TableReadDaysAsync(CancellationToken ct)
    {
        var result = new Dictionary<long, HashSet<long>>();
        await ReadAsync(
            """
            SELECT DISTINCT x.object_key, b.day
            FROM index_day AS b JOIN idx AS x ON x.index_key = b.index_key
            WHERE b.day >= $from AND b.user_reads > 0
            """,
            r =>
            {
                if (!result.TryGetValue(r.GetInt64(0), out var days))
                {
                    result[r.GetInt64(0)] = days = [];
                }

                days.Add(r.GetInt64(1));
            },
            ct,
            ("$from", _dormantStart));
        return result;
    }

    private Task<Dictionary<(long, string), List<(long From, long To)>>> UnknownSpansAsync(long fromDay, CancellationToken ct) =>
        UnknownSpansAsync(_connection, fromDay, ct);

    /// <summary>Stretches in which each range's operational counters were unknown, by table and range, merged.</summary>
    internal static async Task<Dictionary<(long, string), List<(long From, long To)>>> UnknownSpansAsync(
        SqliteConnection connection, long fromDay, CancellationToken ct)
    {
        var result = new Dictionary<(long, string), List<(long, long)>>();
        await ReadAsync(
            connection,
            """
            SELECT x.object_key, p.range_key, u.from_ms, u.to_ms
            FROM part_unknown AS u JOIN part AS p ON p.partition_key = u.partition_key JOIN idx AS x ON x.index_key = p.index_key
            WHERE u.to_ms > $from
            ORDER BY x.object_key, p.range_key, u.from_ms
            """,
            r =>
            {
                var key = (r.GetInt64(0), r.GetString(1));
                if (!result.TryGetValue(key, out var spans))
                {
                    result[key] = spans = [];
                }

                // Merge overlapping stretches from the range's different indexes, so no time is subtracted twice.
                var (from, to) = (r.GetInt64(2), r.GetInt64(3));
                if (spans.Count > 0 && from <= spans[^1].Item2)
                {
                    spans[^1] = (spans[^1].Item1, Math.Max(spans[^1].Item2, to));
                }
                else
                {
                    spans.Add((from, to));
                }
            },
            ct,
            ("$from", fromDay * MsPerDay));
        return result;
    }

    private Task ReadAsync(string sql, Action<SqliteDataReader> row, CancellationToken ct, params (string Name, object? Value)[] parameters) =>
        ReadAsync(_connection, sql, row, ct, parameters);

    internal static async Task ReadAsync(
        SqliteConnection connection, string sql, Action<SqliteDataReader> row, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Sql.Command(connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            row(reader);
        }
    }
}
