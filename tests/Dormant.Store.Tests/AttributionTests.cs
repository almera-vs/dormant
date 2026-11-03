using Dormant.Core.Attribution;
using Dormant.Core.Configuration;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

public sealed class AttributionTests : IAsyncLifetime
{
    private const string SalesId = "guid:00000000-0000-0000-0000-00000000000a";
    private const string ArchiveId = "guid:00000000-0000-0000-0000-00000000000b";
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-attr-").FullName;
    private DormantStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "dormant.db"), Ct);
        var snapshot = new SnapshotBuilder(TimeSpan.Zero)
            .Database("Sales", SalesId, 5, Index("Orders", 1, "PK_Orders", type: "CLUSTERED"), Index("Customers", 1, "PK_Customers", objectId: 101, type: "CLUSTERED"))
            .Database("Archive", ArchiveId, 6, Index("OldOrders", 1, "PK_OldOrders", objectId: 300, type: "CLUSTERED"))
            .Build();
        await _store.IngestAsync("erp-prod", snapshot, new IngestOptions(TimeSpan.FromMinutes(45), []), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static PlanObjectReference Read(string table, string? database = null, string index = "PK") =>
        new(database, "dbo", table, index, PlanAccess.Read);

    private static HarvestedPlan Plan(long id, DateTimeOffset lastRun, string? procedure, params PlanObjectReference[] references) =>
        new(new PlanKey(id, $"0x{id:X16}"), $"0xQ{id}", procedure, lastRun, null, references, ParseFailed: false);

    private Task WriteAsync(params HarvestedPlan[] plans) =>
        _store.WriteQueryStoreAsync("erp-prod", [new QueryStoreHarvest(SalesId, "Sales", "READ_WRITE", plans, null)], T0, Ct);

    private Task<IReadOnlyList<ObjectReader>> ReadersOfAsync(string table, DateTimeOffset since) =>
        _store.GetReadersAsync(new ObjectKey("erp-prod", "Sales", "dbo", table), since, Ct);

    [Fact]
    public async Task The_first_read_looks_back_the_configured_period_and_later_reads_start_from_the_newest_plan()
    {
        var first = (await _store.QueryStoreRequestsAsync("erp-prod", [(SalesId, "Sales")], T0, Lookback, Ct)).ShouldHaveSingleItem();
        first.Since.ShouldBe(T0 - Lookback);
        first.KnownPlans.ShouldBeEmpty();

        await WriteAsync(Plan(1, T0.AddDays(-2), null, Read("Orders")), Plan(2, T0.AddHours(-3), null, Read("Orders")));

        var next = (await _store.QueryStoreRequestsAsync("erp-prod", [(SalesId, "Sales")], T0, Lookback, Ct)).ShouldHaveSingleItem();
        next.Since.ShouldBe(T0.AddHours(-4));
        next.KnownPlans.ShouldBe([new PlanKey(1, "0x0000000000000001"), new PlanKey(2, "0x0000000000000002")], ignoreOrder: true);
    }

    [Fact]
    public async Task The_last_known_reader_is_the_most_recent_plan_that_reads_the_table()
    {
        await WriteAsync(
            Plan(1, T0.AddDays(-20), "dbo.MonthEndReport", Read("Orders", index: "PK_Orders")),
            Plan(2, T0.AddDays(-5), null, Read("ORDERS", database: "sales", index: "IX_Customer")),
            new HarvestedPlan(new PlanKey(3, "0x3"), "0xQ3", "dbo.Purge", T0.AddDays(-1), null,
                [new PlanObjectReference("Sales", "dbo", "Orders", "PK_Orders", PlanAccess.Write)], ParseFailed: false));

        var readers = await ReadersOfAsync("Orders", T0.AddDays(-30));
        readers.Select(r => r.Name).ShouldBe(["query 0xQ2", "dbo.MonthEndReport"]);
        readers[0].Indexes.ShouldBe(["IX_Customer"]);
        (await ReadersOfAsync("Orders", T0.AddDays(-10))).ShouldHaveSingleItem().QueryHash.ShouldBe("0xQ2");

        var config = ConfigLoader.Parse("version: 1\nstore: { path: x.db }\ninstances:\n  - name: erp-prod\n    connection: { env: X }\n", Path.Combine(_directory, "c.yaml"));
        var estate = await _store.ReadClassificationAsync(config, T0, Ct);
        estate.Tables.Single(t => t.Name == "Orders").LastReader!.QueryHash.ShouldBe("0xQ2");
        estate.Tables.Single(t => t.Name == "Customers").LastReader.ShouldBeNull();
    }

    [Fact]
    public async Task References_to_other_databases_on_the_instance_are_mapped_and_unknown_tables_are_skipped()
    {
        await WriteAsync(Plan(1, T0.AddDays(-1), "dbo.Copy", Read("OldOrders", database: "Archive"), Read("NotTracked")));

        var readers = await _store.GetReadersAsync(new ObjectKey("erp-prod", "Archive", "dbo", "OldOrders"), T0.AddDays(-30), Ct);
        readers.ShouldHaveSingleItem().Procedure.ShouldBe("dbo.Copy");
    }

    [Fact]
    public async Task Seeing_a_known_plan_again_refreshes_its_last_run_and_keeps_its_references()
    {
        await WriteAsync(Plan(1, T0.AddDays(-9), "dbo.Report", Read("Orders")));
        await WriteAsync(new HarvestedPlan(new PlanKey(1, "0x0000000000000001"), "0xQ1", "dbo.Report", T0.AddDays(-1), null, null, ParseFailed: false));

        (await ReadersOfAsync("Orders", T0.AddDays(-30))).ShouldHaveSingleItem().LastExecutionUtc.ShouldBe(T0.AddDays(-1));
    }

    [Fact]
    public async Task Plans_older_than_daily_retention_are_pruned()
    {
        await WriteAsync(Plan(1, T0.AddDays(-2), "dbo.Report", Read("Orders")));

        await _store.PruneAsync(T0 + TimeSpan.FromDays(4 * 365), RetentionOptions.Default, Ct);

        (await ReadersOfAsync("Orders", DateTimeOffset.MinValue)).ShouldBeEmpty();
    }
}
