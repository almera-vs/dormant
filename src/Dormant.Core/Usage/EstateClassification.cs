using Dormant.Core.Attribution;

namespace Dormant.Core.Usage;

/// <summary>Every current table, partition and unused index of every instance, classified at one moment.</summary>
/// <param name="HistoryDays">How many UTC days, ending today, the <see cref="DailyUsage"/> series cover; 0 when none were read.</param>
public sealed record EstateClassification(
    DateTimeOffset AsOfUtc,
    IReadOnlyList<ClassifiedTable> Tables,
    IReadOnlyList<UnusedIndex> UnusedIndexes,
    int HistoryDays = 0)
{
    /// <summary>
    /// The units sizes and money are counted by: each partition of a partitioned table, each other table as a whole.
    /// </summary>
    public IEnumerable<ClassifiedUnit> Units =>
        Tables.SelectMany(t => t.IsPartitioned
            ? t.Partitions.Select(p => new ClassifiedUnit(t, p, p.ReservedBytes, p.Classification))
            : [new ClassifiedUnit(t, null, t.ReservedBytes, t.Classification)]);
}

public sealed record ClassifiedUnit(ClassifiedTable Table, ClassifiedPartition? Partition, long ReservedBytes, Classification Classification)
{
    /// <summary>The table, and for a partition its range label: <c>Sales.dbo.Orders · 2021-03</c>.</summary>
    public string DisplayName => Partition is null ? Table.DisplayName : $"{Table.DisplayName} · {Partition.Label}";
}

/// <param name="ReservedBytes">Every index and heap of the table, all partitions.</param>
/// <param name="Partitions">The table's data partitions by range (one entry, "all", when it is not partitioned).</param>
/// <param name="Indexes">The table's heap or clustered index and every other index, each classified on its own reads.</param>
/// <param name="LastReader">The most recent query or procedure Query Store saw reading the table; supporting information only.</param>
/// <param name="Daily">User reads per day over the requested history; null when none was requested.</param>
public sealed record ClassifiedTable(
    string Instance,
    string Database,
    string Schema,
    string Name,
    long RowCount,
    long ReservedBytes,
    Classification Classification,
    IReadOnlyList<ClassifiedPartition> Partitions,
    IReadOnlyList<ClassifiedIndex> Indexes,
    ObjectReader? LastReader = null,
    DailyUsage? Daily = null)
{
    public bool IsPartitioned => Partitions.Count > 1;

    public string DisplayName => $"{Database}.{Schema}.{Name}";
}

/// <param name="ReservedBytes">The range's share of every aligned index and the heap or clustered index.</param>
/// <param name="Daily">Reads per day from the partition's operational counters; null when no history was requested.</param>
public sealed record ClassifiedPartition(
    string Label,
    string Range,
    int PartitionNumber,
    long RowCount,
    long ReservedBytes,
    Classification Classification,
    DailyUsage? Daily = null);

/// <summary>One index of a table (or its heap), classified on its own user reads.</summary>
/// <param name="Name">The index name; null for a heap.</param>
/// <param name="UpdatesInWindow">User update statements in the last <c>warm_days</c> days, outside maintenance windows.</param>
/// <param name="Unused">Listed among the unused indexes.</param>
public sealed record ClassifiedIndex(
    string? Name,
    int IndexId,
    string Type,
    bool IsPrimaryKey,
    bool IsUnique,
    bool IsUniqueConstraint,
    bool IsDisabled,
    long ReservedBytes,
    long UpdatesInWindow,
    Classification Classification,
    bool Unused);

/// <summary>A nonclustered index with no user reads over the warm window, but with writes it must keep up with.</summary>
/// <param name="UpdatesInWindow">User update statements in the window: the write overhead.</param>
/// <param name="Coverage">Share of the window observed while the index existed.</param>
/// <param name="DropStatement">For review only. Test before running it anywhere.</param>
public sealed record UnusedIndex(
    string Instance,
    string Database,
    string Schema,
    string Table,
    string Index,
    long ReservedBytes,
    long UpdatesInWindow,
    double Coverage,
    string DropStatement);

/// <summary>Day by day (UTC days), oldest first, ending today.</summary>
public sealed record DailyUsage(long FirstDay, IReadOnlyList<UsageDay> Days);

/// <summary>One UTC day of an object's history.</summary>
/// <param name="Present">Dormant knew the object on this day: it had been seen on or before it.</param>
/// <param name="Observed">Share of the day (of its elapsed part, today) that was observed. Unobserved time is unknown, never idle.</param>
/// <param name="Reads">User reads outside maintenance windows.</param>
/// <param name="ReadsAtLeast">A counter reset fell inside the day, so <see cref="Reads"/> is a lower bound.</param>
/// <param name="MaintenanceReads">User reads inside maintenance windows: recorded, not counted as use.</param>
public readonly record struct UsageDay(bool Present, double Observed, long Reads, bool ReadsAtLeast, long MaintenanceReads);
