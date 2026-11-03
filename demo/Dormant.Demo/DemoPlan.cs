using Dormant.Core.Usage;

namespace Dormant.Demo;

/// <summary>
/// What the demo plants, and therefore what Dormant must find and nothing else. Months are counted
/// from the first day of the current UTC month: 0 is this month, -1 the previous one.
/// </summary>
public static class DemoPlan
{
    public const string Database = "DormantDemo";
    public const string Instance = "demo";
    public const string Login = "dormant";

    /// <summary>The oldest monthly partition boundary: 23 months back. Orders hold data for months -23 to 0.</summary>
    public const int OldestMonth = -23;

    /// <summary>The partition before the oldest boundary, which holds nothing.</summary>
    public const int BeforeOldest = -24;

    /// <summary>How long the synthetic history reaches back.</summary>
    public const int HistoryDays = 200;

    /// <summary>The one unused index: written with every order, never read.</summary>
    public const string UnusedIndex = "IX_Orders_Status";

    public static DateOnly BaseMonth(DateTimeOffset nowUtc) => new(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, 1);

    /// <summary>The class each Orders partition is planted to have, by month.</summary>
    public static UsageClass OrdersPartition(int month) => month switch
    {
        <= -8 => UsageClass.Dormant,
        <= -6 => UsageClass.Cold,
        <= -3 => UsageClass.Warm,
        _ => UsageClass.Hot,
    };

    /// <summary>The age in days of the last read of a partition of this month, or null when never read.</summary>
    public static int? OrdersPartitionLastReadAge(int month) => OrdersPartition(month) switch
    {
        UsageClass.Dormant => null,
        UsageClass.Cold => 100,
        UsageClass.Warm => 20,
        _ => 0,
    };

    /// <summary>The planted class of each table, and whether it is written to but never read.</summary>
    public static IReadOnlyDictionary<string, (UsageClass Class, bool WriteOnly)> Tables { get; } = new Dictionary<string, (UsageClass, bool)>
    {
        ["Orders"] = (UsageClass.Hot, false),
        ["Customers"] = (UsageClass.Hot, false),
        ["Products"] = (UsageClass.Warm, false),
        ["OrderArchive"] = (UsageClass.Dormant, false),
        ["AuditLog"] = (UsageClass.Dormant, true),
    };

    /// <summary>Products are read once a week, the last time this many days ago.</summary>
    public const int ProductsLastReadAge = 12;
}
