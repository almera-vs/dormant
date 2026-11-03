using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Core.History;
using Dormant.Core.Usage;
using Dormant.Store;
using Microsoft.Data.Sqlite;
using Dormant.Store.Tests;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Report.Tests;

/// <summary>
/// 200 days of history through the real store, with one reading a day at noon: a partitioned Orders table whose old
/// months are never read, a table read every day, an audit heap only written to, a table last read on day 30, an unused
/// index, a unique index nobody reads, an engine restart on day 120 and a three-day gap on days 60 to 62.
/// </summary>
public sealed class SampleEstate : IAsyncLifetime
{
    public const int Days = 200;
    public const int RestartDay = 120;
    public static readonly int[] GapDays = [60, 61, 62];
    public const string OddName = "Odd]<b>\"&";

    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private const int Months = 17;

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-report-").FullName;

    public DateTimeOffset Now { get; } = T0 + TimeSpan.FromDays(Days);

    public DormantConfig Config { get; private set; } = null!;

    public EstateClassification Estate { get; private set; } = null!;

    public StoreStatus Status { get; private set; } = null!;

    public ReportContext Context => new(Estate, Status, Config, "0.1.0-test");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Config = ConfigLoader.Parse(
            "version: 1\nstore: { path: dormant.db }\ninstances:\n  - name: erp-prod\n    connection: { env: X }\n    query_store: { enabled: true }\n"
            + "cost: { currency: EUR, data_storage_per_gb_month: 0.5, backup_copies: 2, backup_storage_per_gb_month: 0.1 }\n",
            Path.Combine(_directory, "dormant.yaml"));
        await using var store = await DormantStore.OpenAsync(Config.Store.Path, ct);
        var options = new IngestOptions(TimeSpan.FromHours(25), []);
        for (var day = 0; day <= Days; day++)
        {
            if (GapDays.Contains(day))
            {
                continue;
            }

            var snapshot = new SnapshotBuilder(TimeSpan.FromDays(day), EngineOn(day)).Database("Sales", Guid, 5, Indexes(day)).Build();
            await store.IngestAsync("erp-prod", snapshot, options, ct);
        }

        Estate = await store.ReadClassificationAsync(Config, Now, 180, ct);
        Status = await store.ReadStatusAsync(ct);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>The month label of Orders partition <paramref name="number"/> (1-based): 2026-01 onwards.</summary>
    public static string MonthLabel(int number) => new DateOnly(2026, 1, 1).AddMonths(number - 1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    private static EngineIncarnation? EngineOn(int day)
    {
        if (day < RestartDay)
        {
            return null;
        }

        // Restarted at 11:00 on the restart day; the reading an hour later sees counters from zero.
        var start = 9_999_999L;
        var since = TimeSpan.FromHours(1) + TimeSpan.FromDays(day - RestartDay);
        return new EngineIncarnation(start, start + (long)since.TotalMilliseconds, T0 + TimeSpan.FromDays(RestartDay) - TimeSpan.FromHours(1));
    }

    private static IndexObservation[] Indexes(int day)
    {
        // Readings since the engine started: counters are cumulative and start again after the restart.
        long n = day < RestartDay ? day + 1 : day - RestartDay + 1;
        var orders = Enumerable.Range(1, Months).ToList();
        PartitionRange Range(int m) => Month(m == 1 ? null : MonthLabel(m) + "-01", m == Months ? null : MonthLabel(m + 1) + "-01");
        var recent = Months - 2;

        // The odd table is read on days 0 to 30 only; after the restart its counters are zero.
        long oddSeeks = day < RestartDay ? Math.Min(day, 30) + 1 : 0;

        return
        [
            Index("Orders", 1, "PK_Orders", seeks: n * 500, updates: n * 100, objectId: 100, type: "CLUSTERED", primaryKey: true,
                partitions: orders.Select(m => Partition(Range(m), m, 100_000 + m, rows: 1000 * m, reservedPages: 1280, scans: m >= recent ? n * 20 : 0)).ToList()),
            Index("Orders", 2, "IX_Orders_Status", seeks: 0, updates: n * 100, objectId: 100,
                partitions: orders.Select(m => Partition(Range(m), m, 200_000 + m, rows: 1000 * m, reservedPages: 128, scans: 0)).ToList()),
            Index("Customers", 1, "PK_Customers", seeks: n * 50, updates: n, objectId: 200, type: "CLUSTERED", primaryKey: true),
            Index("Customers", 2, "UX_Customers_Email", seeks: 0, updates: n * 10, objectId: 200, unique: true),
            Index("AuditLog", 0, null, seeks: 0, updates: n * 1000, objectId: 300),
            Index(OddName, 1, "PK_Odd", seeks: oddSeeks * 3, updates: 0, objectId: 400, type: "CLUSTERED", primaryKey: true),
        ];
    }
}
