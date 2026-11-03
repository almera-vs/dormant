namespace Dormant.Core.Collection;

/// <summary>Everything one collection read from one instance.</summary>
/// <param name="Instance">What the instance reported about itself.</param>
/// <param name="StartedUtc">When collection started, by the agent's clock.</param>
/// <param name="Duration">How long collection took.</param>
/// <param name="Databases">One entry per database selected by the configuration, collected or not.</param>
public sealed record Snapshot(
    InstanceInfo Instance,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    IReadOnlyList<DatabaseSnapshot> Databases)
{
    public int IndexCount => Databases.Sum(d => d.Indexes.Count);

    public int PartitionCount => Databases.Sum(d => d.Indexes.Sum(i => i.Partitions.Count));

    /// <summary>Pages reserved by every collected table and index, in bytes.</summary>
    public long ReservedBytes => Databases.Sum(d => d.Indexes.Sum(i => i.Partitions.Sum(p => p.ReservedPages))) * 8192;
}

/// <summary>
/// One run of the database engine. Usage counters accumulate only within an incarnation.
/// </summary>
/// <param name="StartMsTicks">
/// The engine's tick count when it started. Fixed for the life of the process, so a different value means the engine
/// restarted (or, on Azure SQL Database, that the database now runs on another node).
/// </param>
/// <param name="MsTicks">The tick count when this was read. A smaller value than before means the host restarted.</param>
/// <param name="StartTimeUtc">When the engine started, in UTC.</param>
public sealed record EngineIncarnation(long StartMsTicks, long MsTicks, DateTimeOffset StartTimeUtc)
{
    public bool IsSameAs(EngineIncarnation earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return StartMsTicks == earlier.StartMsTicks && MsTicks >= earlier.MsTicks;
    }
}

public enum DatabaseOutcome
{
    /// <summary>Usage was read.</summary>
    Collected,

    /// <summary>Deliberately not read; see <see cref="SkipReason"/>.</summary>
    Skipped,

    /// <summary>Reading failed; see <see cref="DatabaseSnapshot.Detail"/>.</summary>
    Failed,
}

public enum SkipReason
{
    None,

    /// <summary>The database is not ONLINE. Its usage counters were discarded when it went offline.</summary>
    NotOnline,

    /// <summary>SINGLE_USER or RESTRICTED_USER: Dormant never takes a restricted connection.</summary>
    RestrictedAccess,

    /// <summary>AUTO_CLOSE is ON: the engine discards counters on every close, and reading would wake it.</summary>
    AutoClose,

    /// <summary>The login cannot open the database.</summary>
    NoAccess,

    /// <summary>A non-readable availability group secondary.</summary>
    NotReadableSecondary,
}

/// <summary>One database as seen by one collection.</summary>
/// <param name="Identity">
/// Stable identity that survives renames and detach/attach: <c>guid:&lt;database_guid&gt;</c> on SQL Server and Managed
/// Instance, <c>azure:&lt;create_date&gt;</c> on Azure SQL Database. Null when the database could not be read.
/// </param>
/// <param name="Name">Current name.</param>
/// <param name="DatabaseId">database_id; changes on detach/attach.</param>
/// <param name="ObservedUtc">When usage was read, by the server's clock. Null unless collected.</param>
/// <param name="Engine">The engine incarnation the counters belong to. Null unless collected.</param>
/// <param name="Detail">Human-readable detail for a skip or failure.</param>
public sealed record DatabaseSnapshot(
    string? Identity,
    string Name,
    int DatabaseId,
    DatabaseOutcome Outcome,
    SkipReason SkipReason,
    string? Detail,
    DateTimeOffset? ObservedUtc,
    EngineIncarnation? Engine,
    IReadOnlyList<IndexObservation> Indexes)
{
    /// <summary>
    /// True when the engine may have discarded this database's counters since it was last read, so the next reading
    /// cannot be compared with the last one.
    /// </summary>
    public bool CountersMayHaveReset => SkipReason is SkipReason.NotOnline or SkipReason.AutoClose
        or SkipReason.NoAccess or SkipReason.NotReadableSecondary;

    public static DatabaseSnapshot Skipped(string? identity, string name, int databaseId, SkipReason reason, string detail) =>
        new(identity, name, databaseId, DatabaseOutcome.Skipped, reason, detail, null, null, []);

    public static DatabaseSnapshot Failed(string? identity, string name, int databaseId, string detail) =>
        new(identity, name, databaseId, DatabaseOutcome.Failed, SkipReason.None, detail, null, null, []);
}

/// <summary>A table or indexed view (referred to as the "table" throughout, for brevity).</summary>
/// <param name="ObjectId">object_id.</param>
/// <param name="CreateDate">
/// create_date exactly as reported. Together with <paramref name="ObjectId"/> it identifies the object: a table that is
/// dropped and recreated gets a new create_date and therefore a new history.
/// </param>
/// <param name="Type">sys.objects type: U (table) or V (view).</param>
/// <param name="IsMemoryOptimized">
/// A memory-optimized table: sys.dm_db_index_usage_stats never reports its indexes, so its usage is unknown and it is
/// never classified as Cold or Dormant (decision D-049).
/// </param>
public sealed record ObjectRef(int ObjectId, DateTime CreateDate, string Schema, string Name, string Type, bool IsMemoryOptimized = false);

/// <summary>One index (or heap) and its usage counters, if the engine has a row for it.</summary>
/// <param name="Usage">
/// Null when sys.dm_db_index_usage_stats has no row: the index has not been used since the counters were last
/// initialised for its database.
/// </param>
/// <param name="Partitions">Every partition of the index; one for an index that is not partitioned.</param>
public sealed record IndexObservation(
    ObjectRef Table,
    int IndexId,
    string? IndexName,
    string IndexType,
    bool IsPrimaryKey,
    bool IsUnique,
    bool IsUniqueConstraint,
    bool IsDisabled,
    UsageCounters? Usage,
    IReadOnlyList<PartitionObservation> Partitions);

/// <summary>One partition: what it holds, how big it is, and its operational counters if the engine has them.</summary>
/// <param name="PartitionNumber">1-based number; changes when boundaries are split or merged, so it is not an identity.</param>
/// <param name="PartitionId">sys.partitions.partition_id; a new one means a new rowset whose counters started again.</param>
/// <param name="RowCount">Approximate rows (sys.dm_db_partition_stats.row_count).</param>
/// <param name="ReservedPages">8 KB pages reserved, including LOB and row-overflow data.</param>
/// <param name="UsedPages">8 KB pages in use.</param>
/// <param name="Compression">NONE, ROW, PAGE, COLUMNSTORE or COLUMNSTORE_ARCHIVE.</param>
/// <param name="Operations">
/// Null when sys.dm_db_index_operational_stats has no row: its metadata is not cached, so its activity is unknown.
/// </param>
public sealed record PartitionObservation(
    int PartitionNumber,
    long PartitionId,
    PartitionRange Range,
    long RowCount,
    long ReservedPages,
    long UsedPages,
    string Compression,
    OperationalCounters? Operations);

/// <summary>
/// Cumulative counters from sys.dm_db_index_operational_stats. They start at zero whenever the partition's metadata is
/// loaded into the cache and do not separate user activity from maintenance: supporting evidence, never proof of disuse.
/// </summary>
/// <param name="LeafDeletes">leaf_delete_count plus leaf_ghost_count: most deletes are ghosted first.</param>
public sealed record OperationalCounters(long RangeScans, long SingletonLookups, long LeafInserts, long LeafUpdates, long LeafDeletes)
{
    public long Reads => RangeScans + SingletonLookups;

    public bool DecreasedSince(OperationalCounters earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return RangeScans < earlier.RangeScans || SingletonLookups < earlier.SingletonLookups || LeafInserts < earlier.LeafInserts
            || LeafUpdates < earlier.LeafUpdates || LeafDeletes < earlier.LeafDeletes;
    }
}

/// <summary>Cumulative counters from sys.dm_db_index_usage_stats. User and system activity are kept apart.</summary>
public sealed record UsageCounters(
    long UserSeeks,
    long UserScans,
    long UserLookups,
    long UserUpdates,
    long SystemSeeks,
    long SystemScans,
    long SystemLookups,
    long SystemUpdates,
    DateTimeOffset? LastUserReadUtc,
    DateTimeOffset? LastUserUpdateUtc)
{
    public long UserReads => UserSeeks + UserScans + UserLookups;

    /// <summary>True when any counter is lower than in <paramref name="earlier"/>: the row was reset in between.</summary>
    public bool DecreasedSince(UsageCounters earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        return UserSeeks < earlier.UserSeeks || UserScans < earlier.UserScans || UserLookups < earlier.UserLookups
            || UserUpdates < earlier.UserUpdates || SystemSeeks < earlier.SystemSeeks || SystemScans < earlier.SystemScans
            || SystemLookups < earlier.SystemLookups || SystemUpdates < earlier.SystemUpdates;
    }
}
