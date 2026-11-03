using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

public sealed class RollupTests : IAsyncLifetime
{
    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private const long MsPerHour = 3_600_000;
    private const long MsPerDay = 86_400_000;
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-rollup-").FullName;
    private DormantStore _store = null!;
    private StoreInspector _inspect = null!;

    private string StorePath => Path.Combine(_directory, "dormant.db");

    // 2026-11-02 12:00 UTC is a Monday.
    private static long Hour(TimeSpan after) => (T0 + after).ToUnixTimeMilliseconds() / MsPerHour;

    private static long Day(TimeSpan after) => (T0 + after).ToUnixTimeMilliseconds() / MsPerDay;

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

    private Task<Ingestion.IngestResult> IngestAsync(TimeSpan after, long? seeks, IReadOnlyList<MaintenanceWindow>? windows = null, EngineIncarnation? engine = null) =>
        _store.IngestAsync(
            "erp-prod",
            new SnapshotBuilder(after, engine).Database("Sales", Guid, 5, Index("Orders", 2, "IX_Customer", seeks: seeks)).Build(),
            new IngestOptions(TimeSpan.FromMinutes(45), windows ?? []),
            TestContext.Current.CancellationToken);

    private static MaintenanceWindow Window(string text)
    {
        MaintenanceWindow.TryParse(text, out var window, out var error).ShouldBeTrue(error);
        return window!;
    }

    [Fact]
    public async Task Observed_time_is_split_exactly_at_hour_and_day_boundaries()
    {
        // Readings 11:50, 12:05, 12:20 ... 13:05: the first interval straddles noon.
        for (var minutes = -10; minutes <= 65; minutes += 15)
        {
            await IngestAsync(TimeSpan.FromMinutes(minutes), seeks: null);
        }

        var hours = await _inspect.ObservedAsync("hour");
        hours.Select(h => (h.Bucket, h.Observed)).ShouldBe(
        [
            (Hour(TimeSpan.FromMinutes(-10)), TimeSpan.FromMinutes(10)),
            (Hour(TimeSpan.Zero), TimeSpan.FromMinutes(60)),
            (Hour(TimeSpan.FromMinutes(65)), TimeSpan.FromMinutes(5)),
        ]);
        var day = (await _inspect.ObservedAsync("day")).ShouldHaveSingleItem();
        day.Observed.ShouldBe(TimeSpan.FromMinutes(75));
        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM usage_interval WHERE rolled_up = 0")).ShouldBe(0);
    }

    [Fact]
    public async Task Activity_goes_to_the_bucket_in_which_its_interval_ended()
    {
        await IngestAsync(TimeSpan.FromMinutes(50), seeks: 0);
        await IngestAsync(TimeSpan.FromMinutes(65), seeks: 4);

        var bucket = (await _inspect.IndexBucketsAsync("hour")).ShouldHaveSingleItem();
        bucket.Bucket.ShouldBe(Hour(TimeSpan.FromMinutes(65)));
        bucket.UserReads.ShouldBe(4);
        (await _inspect.IndexBucketsAsync("day")).ShouldHaveSingleItem().UserReads.ShouldBe(4);
    }

    [Fact]
    public async Task Scenario_2_six_hours_of_agent_downtime_are_a_gap_in_every_bucket_they_cover()
    {
        await IngestAsync(TimeSpan.Zero, seeks: 1);
        await IngestAsync(Step, seeks: 1);
        await IngestAsync(Step + TimeSpan.FromHours(6), seeks: 4);

        // Only the first quarter hour was observed; the six hours have no observed time anywhere.
        (await _inspect.ObservedAsync("hour")).ShouldHaveSingleItem().Observed.ShouldBe(Step);
        // The three reads are kept, at the end of the gap.
        var reads = (await _inspect.IndexBucketsAsync("hour")).ShouldHaveSingleItem();
        reads.Bucket.ShouldBe(Hour(Step + TimeSpan.FromHours(6)));
        reads.UserReads.ShouldBe(3);
        var gap = (await _inspect.EventsAsync()).ShouldHaveSingleItem();
        gap.Kind.ShouldBe("gap");
        gap.Duration.ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Scenario_9_reads_inside_a_maintenance_window_are_recorded_but_kept_apart()
    {
        IReadOnlyList<MaintenanceWindow> windows = [Window("Daily 12:00-14:00 UTC")];
        await IngestAsync(TimeSpan.FromMinutes(-10), seeks: 0, windows);
        await IngestAsync(TimeSpan.FromMinutes(5), seeks: 1, windows);   // 11:50-12:05 overlaps the start: use
        await IngestAsync(TimeSpan.FromMinutes(20), seeks: 4, windows);  // 12:05-12:20 inside: maintenance
        await IngestAsync(TimeSpan.FromMinutes(35), seeks: 6, windows);  // 12:20-12:35 inside: maintenance

        var day = (await _inspect.IndexBucketsAsync("day")).ShouldHaveSingleItem();
        day.UserReads.ShouldBe(1);
        day.MaintUserReads.ShouldBe(5);
        var observed = (await _inspect.ObservedAsync("day")).ShouldHaveSingleItem();
        observed.Observed.ShouldBe(TimeSpan.FromMinutes(45));
        observed.Maintenance.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task Lower_bounds_and_resets_survive_rollup()
    {
        var restarted = Engine with { StartMsTicks = 9_999_999, StartTimeUtc = T0.AddDays(-40) };
        await IngestAsync(TimeSpan.Zero, seeks: 10);
        await IngestAsync(Step, seeks: 2, engine: restarted);

        (await _inspect.IndexBucketsAsync("day")).ShouldHaveSingleItem().LowerBound.ShouldBeTrue();
        (await _inspect.EventsAsync()).ShouldHaveSingleItem().Kind.ShouldBe("reset");
        (await _inspect.ObservedAsync("day")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Retention_prunes_each_level_on_its_own_schedule_and_status_keeps_its_story()
    {
        var retention = RetentionOptions.Default;
        await IngestAsync(TimeSpan.Zero, seeks: 0);
        await IngestAsync(Step, seeks: 3);
        await IngestAsync(Step + TimeSpan.FromHours(6), seeks: 3);

        var afterRaw = await _store.PruneAsync(T0 + TimeSpan.FromDays(15), retention, TestContext.Current.CancellationToken);
        afterRaw.RawIntervals.ShouldBe(2);
        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM index_delta")).ShouldBe(0);
        (await _inspect.IndexBucketsAsync("hour")).ShouldHaveSingleItem();
        var status = (await _store.ReadStatusAsync(TestContext.Current.CancellationToken)).Instances.ShouldHaveSingleItem();
        status.Databases.ShouldHaveSingleItem().Observed.ShouldBe(Step);
        status.Gaps.ShouldHaveSingleItem();

        await _store.PruneAsync(T0 + TimeSpan.FromDays(100), retention, TestContext.Current.CancellationToken);
        (await _inspect.IndexBucketsAsync("hour")).ShouldBeEmpty();
        (await _inspect.IndexBucketsAsync("day")).ShouldHaveSingleItem();

        var later = T0 + TimeSpan.FromDays(4 * 365);
        await _store.PruneAsync(later, retention, TestContext.Current.CancellationToken);
        (await _inspect.IndexBucketsAsync("day")).ShouldBeEmpty();
        (await _inspect.EventsAsync()).ShouldBeEmpty();
        var firstSeen = await _inspect.ScalarAsync("SELECT first_seen_ms FROM db");
        firstSeen.ShouldBe(((later - retention.Daily).ToUnixTimeMilliseconds() / MsPerDay) * MsPerDay);
    }

    [Fact]
    public async Task A_store_written_by_milestone_3_backfills_rollups_and_events()
    {
        var path = Path.Combine(_directory, "v2.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await Schema.MigrateAsync(connection, TestContext.Current.CancellationToken, targetVersion: 2);
            await using var command = connection.CreateCommand();
            var t0 = T0.ToUnixTimeMilliseconds();
            command.CommandText = $"""
                INSERT INTO instance (instance_key, name, created_ms) VALUES (1, 'erp-prod', {t0});
                INSERT INTO collection (collection_id, instance_key, started_ms, duration_ms, outcome, database_count, index_count)
                VALUES (1, 1, {t0}, 10, 'ok', 1, 0);
                INSERT INTO db (database_key, instance_key, identity, name, first_seen_ms, last_seen_ms)
                VALUES (1, 1, '{Guid}', 'Sales', {t0}, {t0});
                INSERT INTO usage_interval (database_key, collection_id, from_ms, to_ms, observed_from_ms, kind)
                VALUES (1, 1, {t0}, {t0 + 900_000}, {t0}, 'continuous'),
                       (1, 1, {t0 + 900_000}, {t0 + 22_500_000}, NULL, 'gap');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var store = await DormantStore.OpenAsync(path, TestContext.Current.CancellationToken))
        {
            // The next collection, of any instance, folds the old intervals in.
            await store.IngestAsync(
                "other",
                new SnapshotBuilder(TimeSpan.FromHours(7)).Database("Other", "guid:00000000-0000-0000-0000-000000000009", 7).Build(),
                new IngestOptions(TimeSpan.FromMinutes(45), []),
                TestContext.Current.CancellationToken);
        }

        var inspect = new StoreInspector(path);
        (await inspect.EventsAsync()).ShouldHaveSingleItem().Kind.ShouldBe("gap");
        (await inspect.ObservedAsync("day")).ShouldHaveSingleItem().Observed.ShouldBe(Step);
        (await inspect.ScalarAsync("SELECT COUNT(*) FROM usage_interval WHERE rolled_up = 0")).ShouldBe(0);
    }
}
