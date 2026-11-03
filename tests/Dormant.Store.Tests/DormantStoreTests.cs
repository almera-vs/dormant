using Dormant.Core.Collection;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

public sealed class DormantStoreTests : IAsyncLifetime
{
    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private static readonly IngestOptions Options = new(TimeSpan.FromMinutes(45), []);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-store-").FullName;
    private DormantStore _store = null!;
    private StoreInspector _inspect = null!;

    private string StorePath => Path.Combine(_directory, "history", "dormant.db");

    public async ValueTask InitializeAsync()
    {
        _store = await DormantStore.OpenAsync(StorePath, TestContext.Current.CancellationToken);
        _inspect = new StoreInspector(StorePath);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private Task<Ingestion.IngestResult> IngestAsync(SnapshotBuilder builder) =>
        _store.IngestAsync("erp-prod", builder.Build(), Options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task First_reading_starts_history_and_the_next_closes_an_observed_interval()
    {
        var first = await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 10), Index("Orders", 2, "IX_Customer")));
        first.Databases.ShouldHaveSingleItem().Interval.ShouldBeNull();

        var second = await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 25), Index("Orders", 2, "IX_Customer")));

        second.Databases.ShouldHaveSingleItem().Interval!.Kind.ShouldBe(IntervalKind.Continuous);
        var interval = (await _inspect.IntervalsAsync("Sales")).ShouldHaveSingleItem();
        interval.Kind.ShouldBe("continuous");
        interval.ObservedFrom.ShouldBe(T0);
        interval.To.ShouldBe(T0 + Step);
        var delta = (await _inspect.DeltasAsync("Sales")).ShouldHaveSingleItem();
        delta.Index.ShouldBe("PK_Orders");
        delta.UserReads.ShouldBe(15);
    }

    [Fact]
    public async Task Synthetic_readings_are_recorded_as_such_and_counted_by_status()
    {
        var synthetic = Options with { Synthetic = true };
        var ct = TestContext.Current.CancellationToken;
        await _store.IngestAsync("erp-prod", new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 1)).Build(), synthetic, ct);
        await _store.IngestAsync("erp-prod", new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 2)).Build(), synthetic, ct);
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 3)));

        var instance = (await _store.ReadStatusAsync(ct)).Instances.ShouldHaveSingleItem();

        instance.SyntheticCollections.ShouldBe(2);
        instance.LastSyntheticUtc.ShouldBe(T0 + Step);
        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM collection WHERE synthetic = 0")).ShouldBe(1L);
    }

    [Fact]
    public async Task Scenario_5_a_renamed_table_keeps_one_history_under_its_new_name()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 1)));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 2)));
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("OrdersArchive", 1, "PK_Orders", seeks: 4)));

        var obj = (await _inspect.ObjectsAsync()).ShouldHaveSingleItem();
        obj.Name.ShouldBe("OrdersArchive");
        var deltas = await _inspect.DeltasAsync("Sales");
        deltas.Count.ShouldBe(2);
        deltas.Select(d => d.IndexKey).Distinct().ShouldHaveSingleItem();
        deltas.ShouldAllBe(d => d.Table == "OrdersArchive" && d.Kind == "continuous");
    }

    [Fact]
    public async Task Scenario_4_a_table_dropped_and_recreated_with_the_same_name_gets_a_separate_history()
    {
        var recreated = Created.AddDays(300);
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 50)));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 60)));
        // Same name, new object_id and create_date: no usage row yet for the new table.
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", objectId: 200, created: recreated)));
        await IngestAsync(new SnapshotBuilder(Step * 3).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 3, objectId: 200, created: recreated)));

        var objects = await _inspect.ObjectsAsync();
        objects.Count.ShouldBe(2);
        objects.ShouldAllBe(o => o.Name == "Orders");
        var deltas = await _inspect.DeltasAsync("Sales");
        deltas.Select(d => (d.ObjectKey, d.UserReads)).ShouldBe([(objects[0].ObjectKey, 10L), (objects[1].ObjectKey, 3L)]);
        (await _inspect.IntervalsAsync("Sales")).ShouldAllBe(i => i.Kind == "continuous");
    }

    [Fact]
    public async Task Same_object_id_with_a_new_create_date_is_a_new_table()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", created: Created.AddSeconds(1))));

        (await _inspect.ObjectsAsync()).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_renamed_or_retyped_index_starts_a_new_history()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 2, "IX_A")));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 2, "IX_B")));
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("Orders", 2, "IX_B", type: "NONCLUSTERED COLUMNSTORE")));

        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM idx")).ShouldBe(3);
    }

    [Fact]
    public async Task Scenario_7_a_detached_and_reattached_database_keeps_its_identity_and_marks_a_reset()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 40)));
        // Detached: absent from the collection entirely.
        await IngestAsync(new SnapshotBuilder(Step).Database("Other", "guid:00000000-0000-0000-0000-000000000001", 6));
        // Reattached with a new database_id; its counters started again.
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 9, Index("Orders", 1, "PK_Orders", seeks: 2)));

        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM db WHERE identity = $g", ("$g", Guid))).ShouldBe(1);
        var interval = (await _inspect.IntervalsAsync("Sales")).ShouldHaveSingleItem();
        interval.Kind.ShouldBe("reset");
        interval.ObservedFrom.ShouldBeNull();
        interval.Reason.ShouldBe("database was absent from a collection (detached, dropped or excluded)");
        var delta = (await _inspect.DeltasAsync("Sales")).ShouldHaveSingleItem();
        delta.UserReads.ShouldBe(2);
        delta.LowerBound.ShouldBeTrue();
    }

    [Fact]
    public async Task A_renamed_database_keeps_its_history()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 1)));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales2026", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 2)));

        (await _inspect.IntervalsAsync("Sales2026")).ShouldHaveSingleItem().Kind.ShouldBe("continuous");
    }

    [Fact]
    public async Task A_database_seen_offline_cannot_be_compared_across_the_outage()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 40)));
        await IngestAsync(new SnapshotBuilder(Step).Skipped("Sales", 5, SkipReason.NotOnline, "OFFLINE"));
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 40)));

        var interval = (await _inspect.IntervalsAsync("Sales")).ShouldHaveSingleItem();
        interval.Kind.ShouldBe("reset");
        interval.Reason.ShouldBe("database was not readable at one point (OFFLINE)");
        // 40 again is 40 new reads after the database came back, never "no change".
        (await _inspect.DeltasAsync("Sales")).ShouldHaveSingleItem().UserReads.ShouldBe(40);
    }

    [Fact]
    public async Task A_failed_reading_keeps_the_baseline_so_the_next_one_compares_cleanly()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 40)));
        await IngestAsync(new SnapshotBuilder(Step).Failed("Sales", 5, "Lock request time out period exceeded. (error 1222)"));
        await IngestAsync(new SnapshotBuilder(Step * 2).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders", seeks: 41)));

        var interval = (await _inspect.IntervalsAsync("Sales")).ShouldHaveSingleItem();
        interval.Kind.ShouldBe("continuous");
        interval.From.ShouldBe(T0);
        (await _inspect.DeltasAsync("Sales")).ShouldHaveSingleItem().UserReads.ShouldBe(1);
    }

    [Fact]
    public async Task Status_summarises_coverage_restarts_gaps_and_the_last_collection()
    {
        var restart = new Core.Collection.EngineIncarnation(StartMsTicks: 5_000_000, MsTicks: 5_300_000, StartTimeUtc: T0 + (Step * 2) - TimeSpan.FromMinutes(5));
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        await IngestAsync(new SnapshotBuilder(Step).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        await IngestAsync(new SnapshotBuilder(Step * 2, restart).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        // Six hours without a reading, then a normal one.
        var later = restart with { MsTicks = restart.MsTicks + 6 * 3_600_000 };
        await IngestAsync(new SnapshotBuilder(Step * 2 + TimeSpan.FromHours(6), later).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        await _store.RecordFailureAsync("erp-prod", T0 + TimeSpan.FromHours(7), TimeSpan.FromSeconds(15), "A network-related error occurred.", TestContext.Current.CancellationToken);

        var status = await _store.ReadStatusAsync(TestContext.Current.CancellationToken);

        var instance = status.Instances.ShouldHaveSingleItem();
        instance.Collections.ShouldBe(5);
        instance.LastCollection!.Outcome.ShouldBe("failed");
        instance.LastCollection.Error.ShouldBe("A network-related error occurred.");
        instance.LastSuccessfulCollection!.Outcome.ShouldBe("ok");
        var database = instance.Databases.ShouldHaveSingleItem();
        database.Window.ShouldBe(TimeSpan.FromMinutes(30) + TimeSpan.FromHours(6));
        database.Observed.ShouldBe(Step + TimeSpan.FromMinutes(5));
        instance.Restarts.ShouldHaveSingleItem().AtUtc.ShouldBe(restart.StartTimeUtc);
        instance.Gaps.ShouldHaveSingleItem().Duration.ShouldBe(TimeSpan.FromHours(6));
        instance.Resets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reopening_keeps_history_and_a_newer_schema_is_refused()
    {
        await IngestAsync(new SnapshotBuilder(TimeSpan.Zero).Database("Sales", Guid, 5, Index("Orders", 1, "PK_Orders")));
        await _store.DisposeAsync();

        await using (var reopened = await DormantStore.OpenAsync(StorePath, TestContext.Current.CancellationToken))
        {
            (await reopened.ReadStatusAsync(TestContext.Current.CancellationToken)).Instances.ShouldHaveSingleItem();
        }

        await using (var raw = new SqliteConnection($"Data Source={StorePath};Pooling=False"))
        {
            await raw.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = raw.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var ex = await Should.ThrowAsync<StoreException>(() => DormantStore.OpenAsync(StorePath, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("newer version of Dormant");
        (await Should.ThrowAsync<StoreException>(() => DormantStore.OpenReadOnlyAsync(StorePath, TestContext.Current.CancellationToken))).Message.ShouldContain("newer version");

        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "other.db"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Read_only_open_does_not_create_a_store()
    {
        var missing = Path.Combine(_directory, "nothing-here.db");

        (await DormantStore.OpenReadOnlyAsync(missing, TestContext.Current.CancellationToken)).ShouldBeNull();
        File.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public void The_store_file_is_private_to_its_owner_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes; Windows inherits the directory's ACL.");
        }
        else
        {
            File.GetUnixFileMode(StorePath).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(Path.GetDirectoryName(StorePath)!).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
