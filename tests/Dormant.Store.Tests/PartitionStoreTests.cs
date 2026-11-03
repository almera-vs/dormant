using Dormant.Core.Collection;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

public sealed class PartitionStoreTests : IAsyncLifetime
{
    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private static readonly IngestOptions Options = new(TimeSpan.FromMinutes(45), []);
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(15);

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-parts-").FullName;
    private DormantStore _store = null!;
    private StoreInspector _inspect = null!;

    private string StorePath => Path.Combine(_directory, "dormant.db");

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

    private Task<Ingestion.IngestResult> IngestAsync(TimeSpan after, params PartitionObservation[] partitions) =>
        _store.IngestAsync(
            "erp-prod",
            new SnapshotBuilder(after).Database("Sales", Guid, 5, Index("Orders", 1, "CIX_Orders", seeks: 1, type: "CLUSTERED", partitions: partitions)).Build(),
            Options,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Scenario_6_merge_and_split_keep_each_ranges_history_whatever_its_number()
    {
        await IngestAsync(
            TimeSpan.Zero,
            Partition(Month(null, "2021-01-01"), 1, 11, scans: 0),
            Partition(Month("2021-01-01", "2021-02-01"), 2, 12, scans: 0),
            Partition(Month("2021-02-01", "2021-03-01"), 3, 13, scans: 0),
            Partition(Month("2021-03-01", "2021-04-01"), 4, 14, scans: 0),
            Partition(Month("2021-04-01", null), 5, 15, scans: 0));
        await IngestAsync(
            Step,
            Partition(Month(null, "2021-01-01"), 1, 11, scans: 0),
            Partition(Month("2021-01-01", "2021-02-01"), 2, 12, scans: 0),
            Partition(Month("2021-02-01", "2021-03-01"), 3, 13, scans: 0),
            Partition(Month("2021-03-01", "2021-04-01"), 4, 14, scans: 5),
            Partition(Month("2021-04-01", null), 5, 15, scans: 0));

        // MERGE RANGE ('2021-02-01'): January and February become one partition; March is renumbered from 4 to 3.
        await IngestAsync(
            Step * 2,
            Partition(Month(null, "2021-01-01"), 1, 11, scans: 0),
            Partition(Month("2021-01-01", "2021-03-01"), 2, 16, scans: 0),
            Partition(Month("2021-03-01", "2021-04-01"), 3, 14, scans: 7),
            Partition(Month("2021-04-01", null), 4, 15, scans: 0));

        // SPLIT RANGE ('2021-05-01'): the open-ended last partition becomes April and "from May".
        await IngestAsync(
            Step * 3,
            Partition(Month(null, "2021-01-01"), 1, 11, scans: 0),
            Partition(Month("2021-01-01", "2021-03-01"), 2, 16, scans: 0),
            Partition(Month("2021-03-01", "2021-04-01"), 3, 14, scans: 9),
            Partition(Month("2021-04-01", "2021-05-01"), 4, 15, scans: 0),
            Partition(Month("2021-05-01", null), 5, 17, scans: 0));

        var partitions = await _inspect.PartitionsAsync();
        partitions.Select(p => p.Label).ShouldBe(
            ["before 2021-01", "2021-01", "2021-02", "2021-03", "from 2021-04", "2021-01-01 ≤ OrderDate < 2021-03-01", "2021-04", "from 2021-05"]);

        var march = partitions.Single(p => p.Label == "2021-03");
        march.Number.ShouldBe(3);
        march.LastSeen.ShouldBe(T0 + (Step * 3));
        partitions.Single(p => p.Label == "2021-02").LastSeen.ShouldBe(T0 + Step);
        partitions.Single(p => p.Label == "from 2021-04").LastSeen.ShouldBe(T0 + (Step * 2));

        // One continuous, exact history for March across both renumberings.
        var marchDeltas = (await _inspect.PartitionDeltasAsync()).Where(d => d.PartitionKey == march.PartitionKey).ToList();
        marchDeltas.Select(d => (d.Reads, d.LowerBound)).ShouldBe([(5L, false), (2L, false), (2L, false)]);

        // Partitions created by the MERGE and the SPLIT are unknown only for the interval in which they appeared.
        var unknown = await _inspect.UnknownAsync();
        unknown.Select(u => partitions.Single(p => p.PartitionKey == u.PartitionKey).Label).ShouldBe(
            ["2021-01-01 ≤ OrderDate < 2021-03-01", "2021-04", "from 2021-05"]);
    }

    [Fact]
    public async Task Scenario_8_uncached_stretches_are_recorded_as_unknown_and_merged()
    {
        var range = Month("2021-03-01", "2021-04-01");
        await IngestAsync(TimeSpan.Zero, Partition(range, 1, 14, scans: 3));
        await IngestAsync(Step, Partition(range, 1, 14));
        await IngestAsync(Step * 2, Partition(range, 1, 14));
        await IngestAsync(Step * 3, Partition(range, 1, 14, scans: 1));
        await IngestAsync(Step * 4, Partition(range, 1, 14, scans: 1));

        var span = (await _inspect.UnknownAsync()).ShouldHaveSingleItem();
        span.From.ShouldBe(T0);
        span.To.ShouldBe(T0 + (Step * 3));
        // The read seen when the counters came back is kept, as a lower bound; nothing is recorded as zero.
        (await _inspect.PartitionDeltasAsync()).ShouldHaveSingleItem().LowerBound.ShouldBeTrue();
    }

    [Fact]
    public async Task Sizes_are_kept_once_per_day_and_only_when_they_change()
    {
        var range = PartitionRange.Whole;
        await IngestAsync(TimeSpan.Zero, Partition(range, 1, 14, rows: 100, reservedPages: 16));
        await IngestAsync(Step, Partition(range, 1, 14, rows: 100, reservedPages: 16));
        await IngestAsync(Step * 2, Partition(range, 1, 14, rows: 150, reservedPages: 24));
        await IngestAsync(TimeSpan.FromHours(13), Partition(range, 1, 14, rows: 150, reservedPages: 24));
        await IngestAsync(TimeSpan.FromHours(14), Partition(range, 1, 14, rows: 90, reservedPages: 24));

        (await _inspect.ScalarAsync("SELECT COUNT(*) FROM part_size")).ShouldBe(2);
        (await _inspect.ScalarAsync("SELECT row_count FROM part_size ORDER BY day LIMIT 1")).ShouldBe(150);
        var current = (await _inspect.PartitionsAsync()).ShouldHaveSingleItem();
        current.RowCount.ShouldBe(90);
        current.ReservedPages.ShouldBe(24);
    }

    [Fact]
    public async Task A_store_written_by_milestone_2_is_upgraded_in_place()
    {
        var path = Path.Combine(_directory, "old.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await Schema.MigrateAsync(connection, TestContext.Current.CancellationToken, targetVersion: 1);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO instance (name, created_ms) VALUES ('erp-prod', 0);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var store = await DormantStore.OpenAsync(path, TestContext.Current.CancellationToken))
        {
            (await store.ReadStatusAsync(TestContext.Current.CancellationToken)).Instances.ShouldHaveSingleItem().Name.ShouldBe("erp-prod");
        }

        (await new StoreInspector(path).ScalarAsync("PRAGMA user_version")).ShouldBe(Schema.CurrentVersion);
        (await new StoreInspector(path).ScalarAsync("SELECT COUNT(*) FROM part")).ShouldBe(0);
    }
}
