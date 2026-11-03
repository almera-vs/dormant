using Dormant.Core.Attribution;
using Dormant.Core.Collection;
using Dormant.Store;
using Dormant.Store.History;

namespace Dormant.Integration;

/// <summary>
/// Collection against a real SQL Server, with a scripted workload so the expected history is known in advance.
/// Each test owns its databases; the container is shared.
/// </summary>
public sealed class CollectionTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public CollectionTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Schema(string database) => $"""
        CREATE DATABASE [{database}];
        GO
        USE [{database}];
        CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId int NOT NULL);
        CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId);
        CREATE TABLE dbo.Untouched (Id int NOT NULL CONSTRAINT PK_Untouched PRIMARY KEY);
        INSERT dbo.Orders (Id, CustomerId) SELECT value, value % 10 FROM GENERATE_SERIES(1, 500);
        INSERT dbo.Untouched (Id) VALUES (1);
        """;

    /// <summary>Reads the customer index <paramref name="times"/> times, one statement each.</summary>
    private static Task ReadCustomersAsync(SqlServerInstance sql, string database, int times) =>
        sql.ExecuteInAsync(
            database,
            string.Join("\nGO\n", Enumerable.Repeat("SELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;", times)),
            Ct);

    [Fact]
    public async Task Reads_between_collections_are_attributed_to_the_index_that_served_them()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Work"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Work");

        var first = await collection.CollectAsync();
        var database = first.Databases.ShouldHaveSingleItem();
        database.Outcome.ShouldBe(DatabaseOutcome.Collected);
        database.Interval.ShouldBeNull();
        database.IndexCount.ShouldBe(3);

        await ReadCustomersAsync(sql, "Work", 3);
        var second = await collection.CollectAsync();

        second.Databases.ShouldHaveSingleItem().Interval!.Kind.ShouldBe(IntervalKind.Continuous);
        var reads = (await collection.Inspect.DeltasAsync("Work")).Where(d => d.UserReads > 0).ToList();
        var delta = reads.ShouldHaveSingleItem();
        delta.Index.ShouldBe("IX_Orders_Customer");
        delta.UserReads.ShouldBe(3);
        reads.ShouldNotContain(d => d.Table == "Untouched");
    }

    [Fact]
    public async Task Scenario_2_time_without_a_reading_is_a_gap_never_inactivity()
    {
        // The agent's downtime, compressed: with a maximum observed span of 2 seconds, a 4-second pause is downtime.
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Downtime"), Ct);
        await using var collection = await Collection.StartAsync(sql, new IngestOptions(TimeSpan.FromSeconds(2), []), "Downtime");
        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Downtime", 1);
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        var result = await collection.CollectAsync();

        var interval = result.Databases.ShouldHaveSingleItem().Interval!;
        interval.Kind.ShouldBe(IntervalKind.Gap);
        interval.ObservedFromUtc.ShouldBeNull();
        // The read in the gap is kept as evidence of use; the time is not counted as watched.
        (await collection.Inspect.DeltasAsync("Downtime")).Single(d => d.Index == "IX_Orders_Customer").UserReads.ShouldBe(1);
        (await collection.Inspect.ObservedAsync("day")).Sum(o => o.Observed.Ticks).ShouldBe(0);
        (await collection.Store.ReadStatusAsync(Ct)).Instances.ShouldHaveSingleItem().Gaps.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Scenario_3_counters_that_go_backwards_are_a_reset_never_negative_use()
    {
        // Taking a database offline discards its usage counters; afterwards they are lower than Dormant last saw.
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Backwards"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Backwards");
        await ReadCustomersAsync(sql, "Backwards", 3);
        await collection.CollectAsync();
        await sql.ExecuteAsync("ALTER DATABASE Backwards SET OFFLINE WITH ROLLBACK IMMEDIATE;\nGO\nALTER DATABASE Backwards SET ONLINE;", Ct);
        // Use every table again, so that each index has a usage row and the customer index's reads are lower than before.
        await sql.ExecuteInAsync("Backwards", "INSERT dbo.Orders (Id, CustomerId) VALUES (1000, 3);\nGO\nINSERT dbo.Untouched (Id) VALUES (2);", Ct);
        await ReadCustomersAsync(sql, "Backwards", 1);

        var result = await collection.CollectAsync();

        var interval = result.Databases.ShouldHaveSingleItem().Interval!;
        interval.Kind.ShouldBe(IntervalKind.Reset);
        interval.ObservedFromUtc.ShouldBeNull();
        // Whether a counter went down or a row went missing, it is the same conclusion: the counters were reset.
        interval.Reason.ShouldBeOneOf(
            "usage counters went backwards (database restarted or closed)",
            "usage rows were removed while their indexes still exist (database restarted or closed)");
        var deltas = (await collection.Inspect.DeltasAsync("Backwards")).Where(d => d.Kind == "reset").ToList();
        var customer = deltas.Single(d => d.Index == "IX_Orders_Customer");
        customer.UserReads.ShouldBe(1);
        customer.LowerBound.ShouldBeTrue();
        deltas.ShouldAllBe(d => d.UserReads >= 0);
    }

    [Fact]
    public async Task Scenario_4_dropped_and_recreated_table_gets_a_separate_history()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Recreate"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Recreate");
        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Recreate", 2);
        await collection.CollectAsync();

        await sql.ExecuteInAsync("Recreate", """
            DROP TABLE dbo.Orders;
            CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId int NOT NULL);
            CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId);
            """, Ct);
        await ReadCustomersAsync(sql, "Recreate", 1);
        await collection.CollectAsync();

        var orders = (await collection.Inspect.ObjectsAsync()).Where(o => o.Name == "Orders").ToList();
        orders.Count.ShouldBe(2);
        var reads = (await collection.Inspect.DeltasAsync("Recreate")).Where(d => d.Index == "IX_Orders_Customer").ToList();
        reads.Select(d => (d.ObjectKey, d.UserReads)).ShouldBe([(orders[0].ObjectKey, 2L), (orders[1].ObjectKey, 1L)]);
    }

    [Fact]
    public async Task Scenario_5_renamed_table_keeps_one_continuous_history()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Rename"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Rename");
        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Rename", 2);
        await collection.CollectAsync();

        await sql.ExecuteInAsync("Rename", "EXEC sp_rename 'dbo.Orders', 'OrdersArchive';", Ct);
        await sql.ExecuteInAsync("Rename", "SELECT COUNT(*) FROM dbo.OrdersArchive WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;", Ct);
        var third = await collection.CollectAsync();

        third.Databases.ShouldHaveSingleItem().Interval!.Kind.ShouldBe(IntervalKind.Continuous);
        var reads = (await collection.Inspect.DeltasAsync("Rename")).Where(d => d.Index == "IX_Orders_Customer").ToList();
        reads.Select(d => d.UserReads).ShouldBe([2L, 1L]);
        reads.Select(d => d.IndexKey).Distinct().ShouldHaveSingleItem();
        reads.ShouldAllBe(d => d.Table == "OrdersArchive");
    }

    [Fact]
    public async Task Scenario_7_detached_and_reattached_database_is_the_same_database_with_a_reset()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Attach"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Attach");
        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Attach", 2);
        await collection.CollectAsync();

        // Detach, take the freed database_id with another database, attach again: a new database_id.
        await sql.ExecuteAsync("""
            ALTER DATABASE Attach SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            GO
            EXEC sp_detach_db 'Attach';
            GO
            CREATE DATABASE AttachFiller;
            GO
            CREATE DATABASE Attach ON (FILENAME = '/var/opt/mssql/data/Attach.mdf'), (FILENAME = '/var/opt/mssql/data/Attach_log.ldf') FOR ATTACH;
            GO
            ALTER DATABASE Attach SET MULTI_USER;
            """, Ct);
        await ReadCustomersAsync(sql, "Attach", 4);
        var after = await collection.CollectAsync();

        var interval = after.Databases.ShouldHaveSingleItem().Interval!;
        interval.Kind.ShouldBe(IntervalKind.Reset);
        interval.ObservedFromUtc.ShouldBeNull();
        (await collection.Inspect.ScalarAsync("SELECT COUNT(*) FROM db")).ShouldBe(1);
        var last = (await collection.Inspect.DeltasAsync("Attach")).Where(d => d.Index == "IX_Orders_Customer").Last();
        last.UserReads.ShouldBe(4);
        last.LowerBound.ShouldBeTrue();
    }

    [Fact]
    public async Task Index_rebuild_does_not_reset_usage_counters()
    {
        // docs/sqlserver-internals.md: on SQL Server 2016 and later a rebuild keeps the usage row. If this ever fails,
        // rebuilds would show as database resets (safe, but costly in coverage) and the documentation must change.
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Rebuild"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Rebuild");
        await ReadCustomersAsync(sql, "Rebuild", 2);
        await collection.CollectAsync();

        await sql.ExecuteInAsync("Rebuild", "ALTER INDEX ALL ON dbo.Orders REBUILD;", Ct);
        await ReadCustomersAsync(sql, "Rebuild", 1);
        var after = await collection.CollectAsync();

        after.Databases.ShouldHaveSingleItem().Interval!.Kind.ShouldBe(IntervalKind.Continuous);
        (await collection.Inspect.DeltasAsync("Rebuild")).Single(d => d.Index == "IX_Orders_Customer").UserReads.ShouldBe(1);
    }

    [Fact]
    public async Task Scenario_6_partitions_keep_their_history_through_merge_and_split()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync("""
            CREATE DATABASE Parts;
            GO
            USE Parts;
            CREATE PARTITION FUNCTION pfMonth (date) AS RANGE RIGHT FOR VALUES ('2021-01-01', '2021-02-01', '2021-03-01', '2021-04-01');
            CREATE PARTITION SCHEME psMonth AS PARTITION pfMonth ALL TO ([PRIMARY]);
            CREATE TABLE dbo.Sales (OrderDate date NOT NULL, Id int NOT NULL,
                CONSTRAINT PK_Sales PRIMARY KEY CLUSTERED (OrderDate, Id) ON psMonth (OrderDate));
            -- Ten rows in each month from December 2020 to May 2021.
            INSERT dbo.Sales (OrderDate, Id)
            SELECT DATEADD(MONTH, m.value, '2020-12-15'), (m.value * 100) + n.value
            FROM GENERATE_SERIES(0, 5) AS m CROSS JOIN GENERATE_SERIES(1, 10) AS n;
            -- Fresh statistics now, so the first query does not trigger an automatic update that scans every partition.
            UPDATE STATISTICS dbo.Sales WITH FULLSCAN;
            """, Ct);
        // The range ends inside March. "< '2021-04-01'" would also start a (fruitless) range scan in the partition that
        // holds the 2021-04-01 boundary; docs/sqlserver-internals.md.
        const string ReadMarch = "SELECT COUNT(*) FROM dbo.Sales WHERE OrderDate >= '2021-03-01' AND OrderDate <= '2021-03-31';";
        await using var collection = await Collection.StartAsync(sql, "Parts");

        await collection.CollectAsync();
        var initial = await collection.Inspect.PartitionsAsync();
        initial.Select(p => (p.Label, p.Number, p.RowCount)).ShouldBe(
            [("before 2021-01", 1, 10L), ("2021-01", 2, 10L), ("2021-02", 3, 10L), ("2021-03", 4, 10L), ("from 2021-04", 5, 20L)]);
        initial.ShouldAllBe(p => p.ReservedPages > 0);

        await sql.ExecuteInAsync("Parts", ReadMarch + "\nGO\n" + ReadMarch, Ct);
        await collection.CollectAsync();
        var march = initial.Single(p => p.Label == "2021-03").PartitionKey;
        var reads = (await collection.Inspect.PartitionDeltasAsync()).Where(d => d.Reads > 0).ToList();
        var marchReads = reads.ShouldHaveSingleItem();
        marchReads.PartitionKey.ShouldBe(march);
        marchReads.Reads.ShouldBe(2);
        marchReads.LowerBound.ShouldBeFalse();

        await sql.ExecuteInAsync("Parts", "ALTER PARTITION FUNCTION pfMonth() MERGE RANGE ('2021-02-01');", Ct);
        await sql.ExecuteInAsync("Parts", ReadMarch, Ct);
        await collection.CollectAsync();
        await sql.ExecuteInAsync("Parts", """
            ALTER PARTITION SCHEME psMonth NEXT USED [PRIMARY];
            ALTER PARTITION FUNCTION pfMonth() SPLIT RANGE ('2021-05-01');
            """, Ct);
        await collection.CollectAsync();

        var after = await collection.Inspect.PartitionsAsync();
        var marchNow = after.Single(p => p.Label == "2021-03");
        marchNow.PartitionKey.ShouldBe(march);
        marchNow.Number.ShouldBe(3);
        after.Where(p => p.LastSeen == after.Max(x => x.LastSeen)).Select(p => p.Label).ShouldBe(
            ["before 2021-01", "2021-03", "2021-01-01 ≤ OrderDate < 2021-03-01", "2021-04", "from 2021-05"],
            ignoreOrder: true);
        // The read after the MERGE is attributed to the same March history, exactly or as a lower bound.
        (await collection.Inspect.PartitionDeltasAsync()).Where(d => d.PartitionKey == march).Sum(d => d.Reads).ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Scenario_9_reads_only_inside_the_maintenance_window_are_not_counted_as_use()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Maint"), Ct);
        var now = DateTimeOffset.UtcNow;
        var text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Daily {now.AddHours(-2):HH:mm}-{now.AddHours(2):HH:mm} UTC");
        Core.Configuration.MaintenanceWindow.TryParse(text, out var window, out var error).ShouldBeTrue(error);
        await using var collection = await Collection.StartAsync(sql, [window!], "Maint");

        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Maint", 2);
        await collection.CollectAsync();

        var days = (await collection.Inspect.IndexBucketsAsync("day")).Where(d => d.Index == "IX_Orders_Customer").ToList();
        days.Sum(d => d.UserReads).ShouldBe(0);
        days.Sum(d => d.MaintUserReads).ShouldBe(2);
        // The time was observed; it just was maintenance time.
        var observed = await collection.Inspect.ObservedAsync("day");
        observed.Sum(o => o.Maintenance.Ticks).ShouldBe(observed.Sum(o => o.Observed.Ticks));
        observed.Sum(o => o.Observed.Ticks).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Classification_on_a_short_real_history_uses_engine_evidence_and_claims_nothing_else()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Classify"), Ct);
        await sql.ExecuteInAsync("Classify", "CREATE UNIQUE INDEX UX_Orders_Id ON dbo.Orders (Id, CustomerId);", Ct);
        await using var collection = await Collection.StartAsync(sql, "Classify");
        await ReadCustomersAsync(sql, "Classify", 2);
        await collection.CollectAsync();
        await collection.CollectAsync();

        var config = Core.Configuration.ConfigLoader.Parse(
            "version: 1\nstore: { path: x.db }\ninstances:\n  - name: test\n    connection: { env: X }\n",
            Path.Combine(Path.GetTempPath(), "dormant.yaml"));
        var estate = await collection.Store.ReadClassificationAsync(config, DateTimeOffset.UtcNow, Ct);

        // SQL Server's own last-read time says Orders was read today; nothing has been watched long enough to say less.
        estate.Tables.Single(t => t.Name == "Orders").Classification.Class.ShouldBe(Core.Usage.UsageClass.Warm);
        var untouched = estate.Tables.Single(t => t.Name == "Untouched");
        untouched.Classification.Class.ShouldBe(Core.Usage.UsageClass.InsufficientHistory);
        untouched.ReservedBytes.ShouldBeGreaterThan(0);
        estate.UnusedIndexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_heatmap_shows_todays_real_reads_and_claims_nothing_about_unwatched_time()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Heat"), Ct);
        await using var collection = await Collection.StartAsync(sql, "Heat");
        await collection.CollectAsync();
        await ReadCustomersAsync(sql, "Heat", 3);
        await collection.CollectAsync();

        var config = Core.Configuration.ConfigLoader.Parse(
            "version: 1\nstore: { path: x.db }\ninstances:\n  - name: test\n    connection: { env: X }\n",
            Path.Combine(Path.GetTempPath(), "dormant.yaml"));
        var now = DateTimeOffset.UtcNow;
        var estate = await collection.Store.ReadClassificationAsync(config, now, 30, Ct);

        // Seconds of observation today: the reads are shown, the untouched table's day is unknown, not idle.
        var orders = estate.Tables.Single(t => t.Name == "Orders").Daily.ShouldNotBeNull();
        orders.Days[^1].Reads.ShouldBe(3);
        Report.HeatmapCells.State(orders.Days[^1], 0.9).ShouldBe(Report.CellState.Reads1);
        var untouched = estate.Tables.Single(t => t.Name == "Untouched").Daily.ShouldNotBeNull();
        Report.HeatmapCells.State(untouched.Days[^1], 0.9).ShouldBe(Report.CellState.NoData);
        untouched.Days.Take(29).ShouldAllBe(d => !d.Present);

        var html = Report.HtmlReport.Render(new Report.ReportContext(estate, await collection.Store.ReadStatusAsync(Ct), config, "test"));
        html.ShouldContain("<h4>Heat</h4>");
        html.ShouldContain(": 3 user reads");
    }

    [Fact]
    public async Task Query_Store_names_the_procedure_that_reads_a_table_and_keeps_text_only_when_asked()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync(Schema("Qs"), Ct);
        await sql.ExecuteAsync("ALTER DATABASE Qs SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL);", Ct);
        await sql.ExecuteInAsync("Qs", """
            CREATE PROCEDURE dbo.MonthEndReport AS
                SELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;
            GO
            EXEC dbo.MonthEndReport;
            GO
            EXEC dbo.MonthEndReport;
            """, Ct);
        await using var collection = await Collection.StartAsync(sql, "Qs");
        await collection.CollectAsync();

        var first = (await collection.HarvestAsync(storeText: false)).ShouldHaveSingleItem();
        first.State.ShouldBe("READ_WRITE");
        first.Plans.ShouldContain(p => p.Procedure == "dbo.MonthEndReport" && p.References != null);

        var readers = await collection.Store.GetReadersAsync(new ObjectKey("test", "Qs", "dbo", "Orders"), DateTimeOffset.UtcNow.AddDays(-1), Ct);
        if (readers.Count != 1)
        {
            // Say exactly which statements Query Store holds, so a surprise is diagnosable from the CI log alone.
            var texts = await sql.QueryTextsAsync("Qs", readers.Select(r => r.QueryHash), Ct);
            throw new ShouldAssertException($"Expected only dbo.MonthEndReport to read dbo.Orders; found:{Environment.NewLine}{string.Join(Environment.NewLine, texts)}");
        }

        // Query Store also holds the engine's statistics query on Orders (SELECT StatMan ...): maintenance, not a reader.
        var report = readers[0];
        report.Procedure.ShouldBe("dbo.MonthEndReport");
        report.Indexes.ShouldBe(["IX_Orders_Customer"]);
        report.QueryText.ShouldBeNull();
        (await collection.Store.GetReadersAsync(new ObjectKey("test", "Qs", "dbo", "Untouched"), DateTimeOffset.UtcNow.AddDays(-1), Ct)).ShouldBeEmpty();

        // A second read parses only new plans; with store_text, a new ad-hoc reader keeps its text.
        await sql.ExecuteInAsync("Qs", "SELECT COUNT(*) FROM dbo.Untouched;", Ct);
        // Query Store captures asynchronously: wait until it holds the query before reading it.
        for (var attempt = 0; attempt < 30 && !(await sql.QueryStoreHoldsAsync("Qs", "FROM dbo.Untouched", Ct)); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        var second = (await collection.HarvestAsync(storeText: true)).ShouldHaveSingleItem();
        second.Plans.Where(p => p.References != null).ShouldAllBe(p => p.QueryText != null);
        second.Plans.ShouldContain(p => p.Procedure == "dbo.MonthEndReport" && p.References == null);
        var adHoc = (await collection.Store.GetReadersAsync(new ObjectKey("test", "Qs", "dbo", "Untouched"), DateTimeOffset.UtcNow.AddDays(-1), Ct)).ShouldHaveSingleItem();
        adHoc.Procedure.ShouldBeNull();
        adHoc.QueryText.ShouldNotBeNull().ShouldContain("Untouched");
    }

    [Fact]
    public async Task Auto_close_and_offline_databases_are_skipped_without_being_opened()
    {
        var sql = _fixture.Sql;
        await sql.ExecuteAsync("""
            CREATE DATABASE Sleepy;
            GO
            ALTER DATABASE Sleepy SET AUTO_CLOSE ON;
            GO
            CREATE DATABASE Parked;
            GO
            ALTER DATABASE Parked SET OFFLINE;
            """, Ct);
        await using var collection = await Collection.StartAsync(sql, "Sleepy", "Parked");

        var result = await collection.CollectAsync();

        result.Databases.Single(d => d.Name == "Sleepy").Detail.ShouldBe("AUTO_CLOSE is ON");
        result.Databases.Single(d => d.Name == "Parked").Detail.ShouldBe("OFFLINE");
        result.Databases.ShouldAllBe(d => d.Outcome == DatabaseOutcome.Skipped);
    }
}

/// <summary>Fault scenario 1, in its own container because it restarts SQL Server.</summary>
public sealed class RestartTests
{
    [Fact]
    public async Task Scenario_1_restart_between_collections_is_stitched_and_marked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sql = await SqlServerInstance.StartAsync(SqlServerFixture.Image, ct);
        await sql.ExecuteAsync("""
            CREATE DATABASE Shop;
            GO
            USE Shop;
            CREATE TABLE dbo.Orders (Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY, CustomerId int NOT NULL);
            CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId);
            INSERT dbo.Orders (Id, CustomerId) VALUES (1, 3), (2, 4);
            """, ct);
        await using var collection = await Collection.StartAsync(sql, "Shop");
        await sql.ExecuteInAsync("Shop", "SELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;", ct);
        await collection.CollectAsync();
        // Reads that happen after the last collection and before the restart are lost with the counters.
        await sql.ExecuteInAsync("Shop", "SELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;", ct);

        var beforeRestart = DateTimeOffset.UtcNow;
        await sql.RestartAsync(ct);
        await sql.ExecuteInAsync("Shop", "SELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;\nGO\nSELECT COUNT(*) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer)) WHERE CustomerId = 3;", ct);
        var result = await collection.CollectAsync();

        var interval = result.Databases.ShouldHaveSingleItem().Interval!;
        interval.Kind.ShouldBe(IntervalKind.InstanceRestart);
        interval.ResetAtUtc.ShouldNotBeNull().ShouldBeGreaterThan(beforeRestart.AddSeconds(-5));
        interval.ObservedFromUtc.ShouldBe(interval.ResetAtUtc);
        (await collection.Inspect.DeltasAsync("Shop")).Single(d => d.Index == "IX_Orders_Customer").UserReads.ShouldBe(2);

        var status = await collection.Store.ReadStatusAsync(ct);
        status.Instances.ShouldHaveSingleItem().Restarts.ShouldHaveSingleItem();
    }
}
