using System.Globalization;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Core.Usage;
using Dormant.Store;
using Dormant.Store.Tests;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Demo.Tests;

/// <summary>
/// The demo's synthetic history, through the real store and classifier, from a snapshot shaped like the demo database
/// right after its restart: Dormant must find exactly what <see cref="DemoPlan"/> planted and nothing else. The same
/// check runs against a real SQL Server in tests/Dormant.Integration/DemoTests.
/// </summary>
public sealed class DemoBackfillTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Real = new(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Base = DemoPlan.BaseMonth(Real);

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-demo-").FullName;
    private DormantStore _store = null!;

    public async ValueTask InitializeAsync() =>
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "dormant.db"), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static string Day(int month) => Base.AddMonths(month).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The 26 monthly ranges: before month -23, months -23 to 0, and from month +1.</summary>
    private static List<PartitionObservation> Months(long idBase) =>
    [
        Partition(Month(null, Day(DemoPlan.OldestMonth)), 1, idBase + 1, rows: 0, reservedPages: 0, scans: null),
        .. Enumerable.Range(DemoPlan.OldestMonth, 1 - DemoPlan.OldestMonth)
            .Select((m, i) => Partition(Month(Day(m), Day(m + 1)), i + 2, idBase + i + 2, rows: 3000, reservedPages: 100, scans: null)),
        Partition(Month(Day(1), null), 26, idBase + 26, rows: 0, reservedPages: 0, scans: null),
    ];

    /// <summary>The demo database as the collector sees it after the restart: no usage rows, no cached operational counters.</summary>
    private static Snapshot Template()
    {
        IndexObservation Table(string table, int objectId, int indexId, string? name, string type = "CLUSTERED", bool primaryKey = true, bool unique = false, List<PartitionObservation>? partitions = null) =>
            Index(table, indexId, name, objectId: objectId, type: type, primaryKey: primaryKey, unique: unique,
                partitions: partitions ?? [Partition(PartitionRange.Whole, 1, (objectId * 100L) + indexId, reservedPages: 400, scans: null)]);

        var engine = new EngineIncarnation(55_000_000, 55_300_000, Real - TimeSpan.FromMinutes(5));
        var builder = new SnapshotBuilder(Real - T0, engine).Database(
            DemoPlan.Database,
            "guid:00000000-0000-0000-0000-00000000de30",
            7,
            Table("Orders", 100, 1, "PK_Orders", partitions: Months(1000)),
            Table("Orders", 100, 2, "IX_Orders_Customer", type: "NONCLUSTERED", primaryKey: false, partitions: Months(2000)),
            Table("Orders", 100, 3, DemoPlan.UnusedIndex, type: "NONCLUSTERED", primaryKey: false, partitions: Months(3000)),
            Table("Customers", 200, 1, "PK_Customers"),
            Table("Customers", 200, 2, "UX_Customers_Email", type: "NONCLUSTERED", primaryKey: false, unique: true),
            Table("Products", 300, 1, "PK_Products"),
            Table("OrderArchive", 400, 1, "PK_OrderArchive"),
            Table("AuditLog", 500, 0, null, primaryKey: false));
        return builder.Build();
    }

    [Fact]
    public async Task Dormant_finds_exactly_what_the_demo_planted()
    {
        var ct = TestContext.Current.CancellationToken;
        var template = Template();
        var synthetic = new IngestOptions(DemoBackfill.Step + TimeSpan.FromHours(1), [], Synthetic: true);
        var readings = 0;
        foreach (var snapshot in DemoBackfill.Snapshots(template, Real))
        {
            await _store.IngestAsync(DemoPlan.Instance, snapshot, synthetic, ct);
            readings++;
        }

        await _store.IngestAsync(DemoPlan.Instance, template, new IngestOptions(TimeSpan.FromMinutes(45), []), ct);
        var config = ConfigLoader.Parse(
            $"version: 1\nstore: {{ path: x.db }}\ninstances:\n  - name: {DemoPlan.Instance}\n    connection: {{ env: X }}\n",
            Path.Combine(_directory, "demo.yaml"));
        var estate = await _store.ReadClassificationAsync(config, Real + TimeSpan.FromMinutes(1), ct);
        var status = (await _store.ReadStatusAsync(ct)).Instances.Single();

        readings.ShouldBe(1600);
        status.SyntheticCollections.ShouldBe(1600);
        status.Restarts.Count.ShouldBe(1);
        status.Gaps.ShouldBeEmpty();

        foreach (var table in estate.Tables)
        {
            var (planted, writeOnly) = DemoPlan.Tables[table.Name];
            table.Classification.Class.ShouldBe(planted, table.Name + ": " + table.Classification.Explanation);
            table.Classification.WriteOnly.ShouldBe(writeOnly, table.Name);
        }

        var orders = estate.Tables.Single(t => t.Name == "Orders");
        orders.Partitions.Count.ShouldBe(26);
        foreach (var partition in orders.Partitions)
        {
            var month = partition.PartitionNumber == 1 ? DemoPlan.BeforeOldest : partition.PartitionNumber - 25;
            partition.Classification.Class.ShouldBe(DemoPlan.OrdersPartition(month), partition.Label + ": " + partition.Classification.Explanation);
        }

        estate.UnusedIndexes.Select(i => i.Index).ShouldBe([DemoPlan.UnusedIndex]);
    }

    [Theory]
    [InlineData(-24, UsageClass.Dormant)]
    [InlineData(-8, UsageClass.Dormant)]
    [InlineData(-7, UsageClass.Cold)]
    [InlineData(-6, UsageClass.Cold)]
    [InlineData(-5, UsageClass.Warm)]
    [InlineData(-3, UsageClass.Warm)]
    [InlineData(-2, UsageClass.Hot)]
    [InlineData(1, UsageClass.Hot)]
    public void The_plan_ages_orders_by_month(int month, UsageClass expected) =>
        DemoPlan.OrdersPartition(month).ShouldBe(expected);

    [Fact]
    public void Partition_months_come_from_boundary_values_not_numbers()
    {
        DemoBackfill.MonthOf(Month(null, Day(-23)), Base).ShouldBe(DemoPlan.BeforeOldest);
        DemoBackfill.MonthOf(Month(Day(-7), Day(-6)), Base).ShouldBe(-7);
        DemoBackfill.MonthOf(Month(Day(1), null), Base).ShouldBe(1);
    }
}
