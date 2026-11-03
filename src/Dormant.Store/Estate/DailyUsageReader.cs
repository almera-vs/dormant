using Dormant.Core.Usage;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Estate;

/// <summary>
/// User reads per table and per partition range, day by day, from the daily rollups, with how much of each day was
/// observed: what the report's heatmap draws (decision D-054). A day nobody watched is unknown, never a day without reads.
/// </summary>
internal sealed class DailyUsageReader
{
    private const long MsPerDay = RollupWriter.MsPerDay;

    private readonly SqliteConnection _connection;
    private readonly long _firstDay;
    private readonly long _today;
    private readonly TimeSpan _elapsedToday;

    public DailyUsageReader(SqliteConnection connection, long firstDay, long today, TimeSpan elapsedToday)
    {
        _connection = connection;
        _firstDay = firstDay;
        _today = today;
        _elapsedToday = elapsedToday;
    }

    public async Task<DailySeries> ReadAsync(CancellationToken ct)
    {
        var observed = new Dictionary<long, long[]>();
        await ClassificationReader.ReadAsync(
            _connection,
            "SELECT database_key, day, observed_ms FROM db_day WHERE day BETWEEN $from AND $to",
            r =>
            {
                if (!observed.TryGetValue(r.GetInt64(0), out var days))
                {
                    observed[r.GetInt64(0)] = days = new long[_today - _firstDay + 1];
                }

                days[r.GetInt64(1) - _firstDay] = r.GetInt64(2);
            },
            ct,
            ("$from", _firstDay),
            ("$to", _today));

        var tables = new Dictionary<long, Dictionary<long, DayActivity>>();
        await ClassificationReader.ReadAsync(
            _connection,
            """
            SELECT x.object_key, b.day, SUM(b.user_reads), SUM(b.maint_user_reads), MAX(b.lower_bound)
            FROM index_day AS b JOIN idx AS x ON x.index_key = b.index_key
            WHERE b.day BETWEEN $from AND $to
            GROUP BY x.object_key, b.day
            """,
            r => Add(tables, r.GetInt64(0), r.GetInt64(1), new DayActivity(r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) > 0)),
            ct,
            ("$from", _firstDay),
            ("$to", _today));

        var partitions = new Dictionary<(long, string), Dictionary<long, DayActivity>>();
        await ClassificationReader.ReadAsync(
            _connection,
            """
            SELECT x.object_key, p.range_key, b.day, SUM(b.reads), SUM(b.maint_reads), MAX(b.lower_bound)
            FROM part_day AS b JOIN part AS p ON p.partition_key = b.partition_key JOIN idx AS x ON x.index_key = p.index_key
            WHERE b.day BETWEEN $from AND $to
            GROUP BY x.object_key, p.range_key, b.day
            """,
            r => Add(partitions, (r.GetInt64(0), r.GetString(1)), r.GetInt64(2), new DayActivity(r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) > 0)),
            ct,
            ("$from", _firstDay),
            ("$to", _today));

        var unknown = await ClassificationReader.UnknownSpansAsync(_connection, _firstDay, ct);
        return new DailySeries(this, observed, tables, partitions, unknown);
    }

    private static void Add<TKey>(Dictionary<TKey, Dictionary<long, DayActivity>> into, TKey key, long day, DayActivity activity)
        where TKey : notnull
    {
        if (!into.TryGetValue(key, out var days))
        {
            into[key] = days = [];
        }

        days[day] = activity;
    }

    private DailyUsage Build(long[]? observedMs, Dictionary<long, DayActivity>? activity, long firstSeenMs)
    {
        var firstSeenDay = firstSeenMs / MsPerDay;
        var days = new UsageDay[_today - _firstDay + 1];
        for (var i = 0; i < days.Length; i++)
        {
            var day = _firstDay + i;
            var length = day == _today ? _elapsedToday.TotalMilliseconds : MsPerDay;
            var observed = observedMs is null || length <= 0 ? 0 : Math.Clamp(observedMs[i] / length, 0, 1);
            var reads = activity?.GetValueOrDefault(day) ?? default;
            days[i] = new UsageDay(day >= firstSeenDay, observed, reads.Reads, reads.AtLeast, reads.MaintenanceReads);
        }

        return new DailyUsage(_firstDay, days);
    }

    internal readonly record struct DayActivity(long Reads, long MaintenanceReads, bool AtLeast);

    internal sealed class DailySeries
    {
        private readonly DailyUsageReader _reader;
        private readonly Dictionary<long, long[]> _observed;
        private readonly Dictionary<long, Dictionary<long, DayActivity>> _tables;
        private readonly Dictionary<(long, string), Dictionary<long, DayActivity>> _partitions;
        private readonly Dictionary<(long, string), List<(long From, long To)>> _unknown;

        public DailySeries(
            DailyUsageReader reader,
            Dictionary<long, long[]> observed,
            Dictionary<long, Dictionary<long, DayActivity>> tables,
            Dictionary<(long, string), Dictionary<long, DayActivity>> partitions,
            Dictionary<(long, string), List<(long From, long To)>> unknown)
        {
            _reader = reader;
            _observed = observed;
            _tables = tables;
            _partitions = partitions;
            _unknown = unknown;
        }

        public DailyUsage ForTable(long databaseKey, long objectKey, long firstSeenMs) =>
            _reader.Build(_observed.GetValueOrDefault(databaseKey), _tables.GetValueOrDefault(objectKey), firstSeenMs);

        public DailyUsage ForPartition(long databaseKey, long objectKey, string rangeKey, long firstSeenMs)
        {
            var observed = _observed.GetValueOrDefault(databaseKey);
            if (observed is not null)
            {
                var tableReadDays = _tables.GetValueOrDefault(objectKey)?.Where(d => d.Value.Reads > 0).Select(d => d.Key);
                observed = ObservedTime.Adjust(observed, _reader._firstDay, tableReadDays, _unknown.GetValueOrDefault((objectKey, rangeKey)));
            }

            return _reader.Build(observed, _partitions.GetValueOrDefault((objectKey, rangeKey)), firstSeenMs);
        }
    }
}
