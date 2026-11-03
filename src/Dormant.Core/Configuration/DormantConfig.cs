namespace Dormant.Core.Configuration;

/// <summary>A fully validated configuration. Paths are absolute; durations are parsed.</summary>
public sealed record DormantConfig(
    int Version,
    string SourcePath,
    StoreOptions Store,
    CollectionOptions Collection,
    IReadOnlyList<InstanceOptions> Instances,
    ClassificationOptions Classification,
    CostOptions Cost,
    ReportOptions Report);

public sealed record StoreOptions(string Path, RetentionOptions Retention);

/// <summary>How long each level of history is kept (decision D-045).</summary>
/// <param name="Raw">Per-collection intervals and deltas.</param>
/// <param name="Hourly">Hourly rollups.</param>
/// <param name="Daily">Daily rollups, restart/reset/gap events and size history.</param>
public sealed record RetentionOptions(TimeSpan Raw, TimeSpan Hourly, TimeSpan Daily)
{
    public static readonly RetentionOptions Default = new(TimeSpan.FromDays(14), TimeSpan.FromDays(90), TimeSpan.FromDays(3 * 365));
}

public sealed record CollectionOptions(TimeSpan Interval)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest interval between two readings that still counts as observed time: three collection intervals, so a
    /// single missed or slow collection is not a gap, but agent downtime is (docs/decisions.md D-027).
    /// </summary>
    public TimeSpan MaxObservedSpan => Interval * 3;
}

public sealed record InstanceOptions(
    string Name,
    SecretReference Connection,
    DatabaseFilter Databases,
    QueryStoreOptions QueryStore,
    IReadOnlyList<MaintenanceWindow> MaintenanceWindows);

public sealed record QueryStoreOptions(bool Enabled, TimeSpan Lookback, bool StoreText)
{
    public static readonly QueryStoreOptions Default = new(Enabled: false, Lookback: TimeSpan.FromDays(30), StoreText: false);
}

public sealed record ClassificationOptions(int HotDays, int WarmDays, int DormantDays, double MinCoverage)
{
    public static readonly ClassificationOptions Default = new(HotDays: 7, WarmDays: 30, DormantDays: 180, MinCoverage: 0.9);
}

public sealed record CostOptions(
    string Currency,
    decimal DataStoragePerGbMonth,
    int BackupCopies,
    decimal BackupStoragePerGbMonth)
{
    public static readonly CostOptions Default = new(Currency: "EUR", DataStoragePerGbMonth: 0m, BackupCopies: 1, BackupStoragePerGbMonth: 0m);

    /// <summary>True when no price has been configured; reports then show "set your price" instead of money.</summary>
    public bool HasPrices => DataStoragePerGbMonth > 0m || BackupStoragePerGbMonth > 0m;
}

public sealed record ReportOptions(string OutDir);
