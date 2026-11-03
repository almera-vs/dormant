using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Core.Usage;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

/// <summary>
/// Classification end to end through ingestion, rollups and the reader, with one reading a day at noon (counted as
/// observed: the maximum observed span is set above a day here).
/// </summary>
public sealed class ClassificationTests : IAsyncLifetime
{
    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-class-").FullName;
    private DormantStore _store = null!;

    public async ValueTask InitializeAsync() =>
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "dormant.db"), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private DormantConfig Config(string windows = "") => ConfigLoader.Parse(
        $"version: 1\nstore: {{ path: x.db }}\ninstances:\n  - name: erp-prod\n    connection: {{ env: X }}\n{windows}",
        Path.Combine(_directory, "dormant.yaml"));

    /// <summary>Readings on days 0..<paramref name="lastDay"/>; returns the estate classified at the last one.</summary>
    private async Task<EstateClassification> RunAsync(int lastDay, Func<int, IndexObservation[]> indexes, DormantConfig? config = null)
    {
        config ??= Config();
        var options = new IngestOptions(TimeSpan.FromHours(25), config.Instances[0].MaintenanceWindows);
        for (var day = 0; day <= lastDay; day++)
        {
            var snapshot = new SnapshotBuilder(TimeSpan.FromDays(day)).Database("Sales", Guid, 5, indexes(day)).Build();
            await _store.IngestAsync("erp-prod", snapshot, options, TestContext.Current.CancellationToken);
        }

        return await _store.ReadClassificationAsync(config, T0 + TimeSpan.FromDays(lastDay), TestContext.Current.CancellationToken);
    }

    private static IndexObservation[] Orders(long seeks, long updates = 0) =>
    [
        Index("Orders", 1, "PK_Orders", seeks: seeks, updates: updates, type: "CLUSTERED", primaryKey: true),
    ];

    [Fact]
    public async Task A_table_unread_for_180_well_observed_days_is_dormant()
    {
        var estate = await RunAsync(180, _ => Orders(seeks: 0));

        var table = estate.Tables.ShouldHaveSingleItem();
        table.Classification.Class.ShouldBe(UsageClass.Dormant);
        table.Classification.DormantCoverage.ShouldBe(1.0);
        table.Classification.Explanation.ShouldBe("no reads in the last 180 days (100.0% observed)");
    }

    [Fact]
    public async Task Scenario_10_thirty_days_of_history_is_insufficient_never_dormant()
    {
        var estate = await RunAsync(30, _ => Orders(seeks: 0));

        var classification = estate.Tables.ShouldHaveSingleItem().Classification;
        classification.Class.ShouldBe(UsageClass.InsufficientHistory);
        classification.Explanation.ShouldStartWith("no reads seen, but only 16.4% of the last 180 days were observed");
    }

    [Fact]
    public async Task A_read_80_days_ago_is_cold_and_reads_on_most_recent_days_are_hot()
    {
        (await RunAsync(180, day => Orders(seeks: day >= 100 ? 1 : 0))).Tables.Single().Classification.Class.ShouldBe(UsageClass.Cold);

        await _store.DisposeAsync();
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "hot.db"), TestContext.Current.CancellationToken);
        (await RunAsync(10, day => Orders(seeks: Math.Max(0, day - 5)))).Tables.Single().Classification.Class.ShouldBe(UsageClass.Hot);
    }

    [Fact]
    public async Task A_table_first_seen_recently_cannot_be_dormant_however_long_its_database_was_watched()
    {
        var estate = await RunAsync(180, day => day < 170
            ? Orders(seeks: 0)
            : [.. Orders(seeks: 0), Index("Archive", 1, "PK_Archive", objectId: 200, type: "CLUSTERED", primaryKey: true)]);

        estate.Tables.Single(t => t.Name == "Orders").Classification.Class.ShouldBe(UsageClass.Dormant);
        var archive = estate.Tables.Single(t => t.Name == "Archive").Classification;
        archive.Class.ShouldBe(UsageClass.InsufficientHistory);
        archive.DormantCoverage.ShouldBeLessThan(0.06);
    }

    [Fact]
    public async Task Scenario_11_only_plain_nonclustered_indexes_with_writes_and_no_reads_are_unused()
    {
        var estate = await RunAsync(31, day =>
        [
            Index("Orders", 1, "PK_Orders", seeks: day, updates: day, type: "CLUSTERED", primaryKey: true),
            Index("Orders", 2, "UX_Orders_Number", seeks: 0, updates: day, unique: true),
            Index("Orders", 3, "IX_Orders_Plain", seeks: 0, updates: day),
            Index("Orders", 4, "IX_Orders_Idle", seeks: 0, updates: 0),
            Index("Orders", 5, "IX_Orders_Used", seeks: day, updates: day),
        ]);

        var unused = estate.UnusedIndexes.ShouldHaveSingleItem();
        unused.Index.ShouldBe("IX_Orders_Plain");
        unused.UpdatesInWindow.ShouldBe(30);
        unused.Coverage.ShouldBe(1.0);
        unused.DropStatement.ShouldBe("DROP INDEX [IX_Orders_Plain] ON [dbo].[Orders];");
        unused.ReservedBytes.ShouldBe(16 * 8192);
    }

    [Fact]
    public async Task An_index_younger_than_the_window_is_never_called_unused()
    {
        var estate = await RunAsync(31, day => day < 10
            ? Orders(seeks: day)
            : [.. Orders(seeks: day), Index("Orders", 3, "IX_New", seeks: 0, updates: day)]);

        estate.UnusedIndexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Memory_optimized_tables_are_never_judged()
    {
        var estate = await RunAsync(180, _ => [Index("Hot", 2, "IX_Hot", objectId: 300, memoryOptimized: true)]);

        var classification = estate.Tables.ShouldHaveSingleItem().Classification;
        classification.Class.ShouldBe(UsageClass.InsufficientHistory);
        classification.Explanation.ShouldStartWith("memory-optimized table");
        estate.UnusedIndexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_engines_own_last_read_time_counts_unless_it_fell_in_a_maintenance_window()
    {
        // No reads while Dormant watched, but SQL Server says the table was read just before Dormant's first reading.
        var lastRead = T0 - TimeSpan.FromHours(3);
        IndexObservation[] Indexes(int day) => [Index("Orders", 1, "PK_Orders", seeks: 5, lastRead: lastRead, type: "CLUSTERED", primaryKey: true)];

        (await RunAsync(10, Indexes)).Tables.Single().Classification.Class.ShouldBe(UsageClass.Warm);

        await _store.DisposeAsync();
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "maint.db"), TestContext.Current.CancellationToken);
        var config = Config("    maintenance_windows: [\"Daily 08:00-10:00 UTC\"]");
        (await RunAsync(10, Indexes, config)).Tables.Single().Classification.Class.ShouldBe(UsageClass.InsufficientHistory);
    }

    [Fact]
    public async Task Partitions_of_a_busy_table_are_classified_by_their_own_activity()
    {
        // Monthly partitions: March is read every day, January never; February's counters are never cached.
        IndexObservation[] Sales(int day) =>
        [
            Index("Sales", 1, "CIX_Sales", seeks: day, type: "CLUSTERED", partitions:
            [
                Partition(Month("2021-01-01", "2021-02-01"), 1, 11, reservedPages: 100, scans: 0),
                Partition(Month("2021-02-01", "2021-03-01"), 2, 12, reservedPages: 200),
                Partition(Month("2021-03-01", "2021-04-01"), 3, 13, reservedPages: 300, scans: day),
            ]),
        ];

        var estate = await RunAsync(180, Sales);

        var table = estate.Tables.ShouldHaveSingleItem();
        table.Classification.Class.ShouldBe(UsageClass.Hot);
        table.Partitions.Select(p => (p.Label, p.Classification.Class)).ShouldBe(
        [
            ("2021-01", UsageClass.Dormant),
            ("2021-02", UsageClass.InsufficientHistory),
            ("2021-03", UsageClass.Hot),
        ]);
        estate.Units.Where(u => u.Classification.Class == UsageClass.Dormant).Sum(u => u.ReservedBytes).ShouldBe(100 * 8192);
    }

    [Fact]
    public async Task Scenario_9_reads_only_inside_maintenance_windows_leave_a_table_dormant_and_say_why()
    {
        // Two readings a day, 11:00 and 13:00; every read happens between them, inside the 10:30-13:30 window.
        var config = Config("    maintenance_windows: [\"Daily 10:30-13:30 UTC\"]");
        var options = new IngestOptions(TimeSpan.FromHours(25), config.Instances[0].MaintenanceWindows);
        for (var day = 0; day <= 181; day++)
        {
            foreach (var (hours, seeks) in new[] { (-1, day * 3L), (1, (day * 3L) + 3) })
            {
                var snapshot = new SnapshotBuilder(TimeSpan.FromDays(day) + TimeSpan.FromHours(hours)).Database("Sales", Guid, 5, Orders(seeks)).Build();
                await _store.IngestAsync("erp-prod", snapshot, options, TestContext.Current.CancellationToken);
            }
        }

        var estate = await _store.ReadClassificationAsync(config, T0 + TimeSpan.FromDays(181) + TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

        var classification = estate.Tables.Single().Classification;
        classification.Class.ShouldBe(UsageClass.Dormant);
        classification.Explanation.ShouldEndWith("; reads inside maintenance windows are not counted as use");
    }
}
