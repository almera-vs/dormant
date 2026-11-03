using Dormant.Core.Collection;

namespace Dormant.Store.Tests;

/// <summary>Builds snapshots the way the SQL Server collector would, for store tests.</summary>
internal sealed class SnapshotBuilder
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    public static readonly EngineIncarnation Engine = new(StartMsTicks: 1_000, MsTicks: 1_000_000, StartTimeUtc: T0.AddDays(-3));
    public static readonly DateTime Created = new(2024, 1, 15, 9, 30, 0, 123);

    private readonly List<DatabaseSnapshot> _databases = [];
    private readonly DateTimeOffset _at;
    private readonly EngineIncarnation _engine;

    public SnapshotBuilder(TimeSpan after, EngineIncarnation? engine = null)
    {
        _at = T0 + after;
        _engine = engine ?? Engine with { MsTicks = Engine.MsTicks + (long)after.TotalMilliseconds };
    }

    public SnapshotBuilder Database(string name, string identity, int databaseId, params IndexObservation[] indexes)
    {
        _databases.Add(new DatabaseSnapshot(identity, name, databaseId, DatabaseOutcome.Collected, SkipReason.None, null, _at, _engine, indexes));
        return this;
    }

    public SnapshotBuilder Skipped(string name, int databaseId, SkipReason reason, string detail)
    {
        _databases.Add(DatabaseSnapshot.Skipped(null, name, databaseId, reason, detail));
        return this;
    }

    public SnapshotBuilder Failed(string name, int databaseId, string detail)
    {
        _databases.Add(DatabaseSnapshot.Failed(null, name, databaseId, detail));
        return this;
    }

    public Snapshot Build() =>
        new(new InstanceInfo("sql01", "SQL Server 2022", "16.0.4135.4", "Developer Edition", "dormant", _engine.StartTimeUtc), _at, TimeSpan.FromMilliseconds(250), _databases);

    public static IndexObservation Index(
        string table, int indexId, string? name, long? seeks = null, int objectId = 100, DateTime? created = null, string type = "NONCLUSTERED",
        bool unique = false, IReadOnlyList<PartitionObservation>? partitions = null, long? updates = null, DateTimeOffset? lastRead = null,
        bool primaryKey = false, bool memoryOptimized = false) =>
        new(
            new ObjectRef(objectId, created ?? Created, "dbo", table, "U", memoryOptimized),
            indexId,
            name,
            indexId == 0 ? "HEAP" : type,
            IsPrimaryKey: primaryKey,
            IsUnique: unique || primaryKey,
            IsUniqueConstraint: false,
            IsDisabled: false,
            seeks is null && updates is null ? null : new UsageCounters(seeks ?? 0, 0, 0, updates ?? 0, 0, 0, 0, 0, lastRead, null),
            partitions ?? [Partition(PartitionRange.Whole, 1, (objectId * 1000L) + indexId)]);

    public static PartitionObservation Partition(
        PartitionRange range, int number, long partitionId, long rows = 100, long reservedPages = 16, long? scans = null) =>
        new(number, partitionId, range, rows, reservedPages, reservedPages, "NONE",
            scans is null ? null : new OperationalCounters(scans.Value, 0, 0, 0, 0));

    /// <summary>A monthly RANGE RIGHT partition on OrderDate; null bounds are open ends.</summary>
    public static PartitionRange Month(string? from, string? to) =>
        new(true, true, from is null ? null : new BoundaryValue("date", from), to is null ? null : new BoundaryValue("date", to), "OrderDate");
}
