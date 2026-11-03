using Dormant.Core.Collection;
using Dormant.Core.Usage;

namespace Dormant.Demo;

/// <summary>
/// Made-up history for the demo: 200 days of readings every 3 hours, built from one real snapshot of the demo database
/// (so every table, index and partition has its real identity, size and range) with counters that follow
/// <see cref="DemoPlan"/>. The store records these readings as synthetic, and every report says so (decision D-058).
/// </summary>
public static class DemoBackfill
{
    public static readonly TimeSpan Step = TimeSpan.FromHours(3);

    /// <summary>The last made-up reading is this long before the real one, so the hand-over is a short, real interval.</summary>
    public static readonly TimeSpan HandOver = TimeSpan.FromMinutes(10);

    public static IEnumerable<Snapshot> Snapshots(Snapshot template, DateTimeOffset realReadingUtc)
    {
        ArgumentNullException.ThrowIfNull(template);
        var database = template.Databases.Single(d => d.Name == DemoPlan.Database && d.Outcome == DatabaseOutcome.Collected);
        var baseMonth = DemoPlan.BaseMonth(realReadingUtc);
        var last = realReadingUtc - HandOver;
        var count = (int)(TimeSpan.FromDays(DemoPlan.HistoryDays) / Step);
        var first = last - (Step * (count - 1));
        var engineStart = first - TimeSpan.FromHours(1);
        var counters = new Counters();

        for (var k = 0; k < count; k++)
        {
            var at = first + (Step * k);
            var age = realReadingUtc.UtcDateTime.Date.Subtract(at.UtcDateTime.Date).Days;
            var firstOfDay = at.UtcDateTime.TimeOfDay < Step;
            var engine = new EngineIncarnation(1, 1 + (long)(at - engineStart).TotalMilliseconds, engineStart);
            var indexes = database.Indexes.Select(index => Advance(index, counters, at, age, firstOfDay, baseMonth)).ToList();
            yield return new Snapshot(
                template.Instance with { StartTimeUtc = engineStart },
                at,
                TimeSpan.FromMilliseconds(200),
                [database with { ObservedUtc = at, Engine = engine, Indexes = indexes }]);
        }
    }

    /// <summary>The month of a partition relative to <paramref name="baseMonth"/>; the open first partition is <see cref="DemoPlan.BeforeOldest"/>.</summary>
    public static int MonthOf(PartitionRange range, DateOnly baseMonth)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (range.Lower is null || !range.Lower.TryGetDate(out var lower))
        {
            return DemoPlan.BeforeOldest;
        }

        return ((lower.Year * 12) + lower.Month) - ((baseMonth.Year * 12) + baseMonth.Month);
    }

    private static IndexObservation Advance(IndexObservation index, Counters counters, DateTimeOffset at, int age, bool firstOfDay, DateOnly baseMonth)
    {
        var table = index.Table.Name;
        var name = index.IndexName ?? "heap";
        var (reads, updates) = (table, name) switch
        {
            ("Orders", "PK_Orders") => (30, 20),
            ("Orders", "IX_Orders_Customer") => (15, 20),
            ("Orders", DemoPlan.UnusedIndex) => (0, 20),
            ("Customers", "PK_Customers") => (40, 0),
            ("Customers", "UX_Customers_Email") => (0, 1),
            ("Products", "PK_Products") => (age >= DemoPlan.ProductsLastReadAge && (age - DemoPlan.ProductsLastReadAge) % 7 == 0 && firstOfDay ? 3 : 0, 0),
            ("AuditLog", "heap") => (0, 50),
            _ => (0, 0),
        };

        var usage = counters.Usage(table, name, reads, updates, at);
        var partitions = index.Partitions.Select(partition =>
        {
            long scans = 0;
            if (table == "Orders" && index.IndexId != 0 && name != DemoPlan.UnusedIndex
                && DemoPlan.OrdersPartitionLastReadAge(MonthOf(partition.Range, baseMonth)) is { } lastReadAge && age >= lastReadAge)
            {
                var hot = DemoPlan.OrdersPartition(MonthOf(partition.Range, baseMonth)) == UsageClass.Hot;
                scans = name == "PK_Orders" ? (hot ? 10 : 2) : (hot ? 5 : 1);
            }
            else if (!partition.Range.IsPartitioned)
            {
                scans = reads;
            }

            var inserts = updates > 0 && Contains(partition.Range, at) ? updates : 0;
            return partition with { Operations = counters.Operations(table, name, partition.PartitionNumber, scans, inserts) };
        }).ToList();

        return index with { Usage = usage, Partitions = partitions };
    }

    private static bool Contains(PartitionRange range, DateTimeOffset at)
    {
        if (!range.IsPartitioned)
        {
            return true;
        }

        var day = at.UtcDateTime.Date;
        var afterLower = range.Lower is null || (range.Lower.TryGetDate(out var lower) && day >= lower);
        var beforeUpper = range.Upper is null || (range.Upper.TryGetDate(out var upper) && day < upper);
        return afterLower && beforeUpper;
    }

    /// <summary>Cumulative counters, as SQL Server keeps them within one engine incarnation.</summary>
    private sealed class Counters
    {
        private readonly Dictionary<(string, string), UsageCounters> _usage = [];
        private readonly Dictionary<(string, string, int), OperationalCounters> _operations = [];

        public UsageCounters? Usage(string table, string index, long reads, long updates, DateTimeOffset at)
        {
            var key = (table, index);
            _usage.TryGetValue(key, out var before);
            if (before is null && reads == 0 && updates == 0)
            {
                // Never used since the counters were initialised: SQL Server has no row for it.
                return null;
            }

            before ??= new UsageCounters(0, 0, 0, 0, 0, 0, 0, 0, null, null);
            var next = before with
            {
                UserSeeks = before.UserSeeks + reads,
                UserUpdates = before.UserUpdates + updates,
                LastUserReadUtc = reads > 0 ? at : before.LastUserReadUtc,
                LastUserUpdateUtc = updates > 0 ? at : before.LastUserUpdateUtc,
            };
            _usage[key] = next;
            return next;
        }

        public OperationalCounters Operations(string table, string index, int partition, long scans, long inserts)
        {
            var key = (table, index, partition);
            var before = _operations.GetValueOrDefault(key) ?? new OperationalCounters(0, 0, 0, 0, 0);
            var next = before with { RangeScans = before.RangeScans + scans, LeafInserts = before.LeafInserts + inserts };
            _operations[key] = next;
            return next;
        }
    }
}
