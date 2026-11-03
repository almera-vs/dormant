using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Core.Usage;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Store.Tests;

/// <summary>
/// The day-by-day series the report's heatmap draws, the last write, and per-index classes, through ingestion, rollups
/// and the reader. One reading a day at noon; a maximum observed span above a day, so each interval counts as observed.
/// </summary>
public sealed class DailyUsageTests : IAsyncLifetime
{
    private const string Guid = "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff";
    private static readonly long T0Day = T0.ToUnixTimeMilliseconds() / 86_400_000;

    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-daily-").FullName;
    private DormantStore _store = null!;

    public async ValueTask InitializeAsync() =>
        _store = await DormantStore.OpenAsync(Path.Combine(_directory, "dormant.db"), TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private DormantConfig Config => ConfigLoader.Parse(
        "version: 1\nstore: { path: x.db }\ninstances:\n  - name: erp-prod\n    connection: { env: X }\n",
        Path.Combine(_directory, "dormant.yaml"));

    /// <summary>Readings at noon on days 0..<paramref name="lastDay"/>, except <paramref name="skipped"/>.</summary>
    private async Task<EstateClassification> RunAsync(int lastDay, Func<int, IndexObservation[]> indexes, int historyDays, params int[] skipped)
    {
        var options = new IngestOptions(TimeSpan.FromHours(25), []);
        for (var day = 0; day <= lastDay; day++)
        {
            if (!skipped.Contains(day))
            {
                var snapshot = new SnapshotBuilder(TimeSpan.FromDays(day)).Database("Sales", Guid, 5, indexes(day)).Build();
                await _store.IngestAsync("erp-prod", snapshot, options, TestContext.Current.CancellationToken);
            }
        }

        return await _store.ReadClassificationAsync(Config, T0 + TimeSpan.FromDays(lastDay), historyDays, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Each_day_carries_its_reads_and_how_much_of_it_was_observed()
    {
        // Ten reads on day 3 only; no readings on days 6 and 7, so the interval from day 5 to day 8 is a gap.
        var estate = await RunAsync(10, day => [Index("Orders", 1, "PK_Orders", seeks: day >= 3 ? 10 : 0, type: "CLUSTERED", primaryKey: true)], 14, 6, 7);

        var daily = estate.Tables.Single().Daily.ShouldNotBeNull();
        estate.HistoryDays.ShouldBe(14);
        daily.Days.Count.ShouldBe(14);
        daily.FirstDay.ShouldBe(T0Day + 10 - 13);
        UsageDay On(int day) => daily.Days[(int)(T0Day + day - daily.FirstDay)];

        On(-1).Present.ShouldBeFalse();
        On(0).Present.ShouldBeTrue();
        On(0).Observed.ShouldBe(0.5);
        On(3).Reads.ShouldBe(10);
        On(3).Observed.ShouldBe(1);
        On(4).Reads.ShouldBe(0);
        On(4).Observed.ShouldBe(1);
        On(5).Observed.ShouldBe(0.5);
        On(6).Observed.ShouldBe(0);
        On(7).Observed.ShouldBe(0);
        On(8).Observed.ShouldBe(0.5);

        // Today ends now, at noon: the half day elapsed was fully observed.
        On(10).Observed.ShouldBe(1);
    }

    [Fact]
    public async Task No_series_is_read_unless_asked_for()
    {
        var estate = await RunAsync(2, _ => [Index("Orders", 1, "PK_Orders", seeks: 1, type: "CLUSTERED")], historyDays: 0);

        estate.HistoryDays.ShouldBe(0);
        estate.Tables.Single().Daily.ShouldBeNull();
    }

    [Fact]
    public async Task A_partitions_day_is_unobserved_when_its_table_was_read_and_its_counters_were_unknown()
    {
        // The table is read every day; January's operational counters are missing on day 4 (unknown, not zero).
        IndexObservation[] Sales(int day) =>
        [
            Index("Sales", 1, "CIX_Sales", seeks: day * 5, type: "CLUSTERED", partitions:
            [
                Partition(Month("2021-01-01", "2021-02-01"), 1, 11, scans: day == 4 ? null : 0),
                Partition(Month("2021-02-01", "2021-03-01"), 2, 12, scans: day),
            ]),
        ];

        var estate = await RunAsync(8, Sales, 8);

        var table = estate.Tables.Single();
        var january = table.Partitions[0].Daily.ShouldNotBeNull();
        var february = table.Partitions[1].Daily.ShouldNotBeNull();
        UsageDay On(DailyUsage usage, int day) => usage.Days[(int)(T0Day + day - usage.FirstDay)];

        // Both intervals that touch the missing reading are unknown for January: noon on day 3 to noon on day 5.
        On(january, 2).Observed.ShouldBe(1);
        On(january, 3).Observed.ShouldBe(0.5);
        On(january, 4).Observed.ShouldBe(0);
        On(january, 5).Observed.ShouldBe(0.5);
        On(january, 6).Observed.ShouldBe(1);
        On(february, 4).Observed.ShouldBe(1);
        On(january, 6).Reads.ShouldBe(0);
        On(february, 6).Reads.ShouldBe(1);
        table.Daily.ShouldNotBeNull().Days[^1].Reads.ShouldBe(5);
    }

    [Fact]
    public async Task The_last_write_is_the_last_day_with_user_updates()
    {
        var estate = await RunAsync(10, day => [Index("Orders", 1, "PK_Orders", seeks: 0, updates: day >= 5 ? 7 : 0, type: "CLUSTERED")], 0);

        var classification = estate.Tables.Single().Classification;
        classification.LastWriteDay.ShouldBe(T0Day + 5);
        classification.LastReadDay.ShouldBeNull();
    }

    [Fact]
    public async Task Every_index_is_classified_on_its_own_reads()
    {
        var estate = await RunAsync(31, day =>
        [
            Index("Orders", 1, "PK_Orders", seeks: day, updates: day, type: "CLUSTERED", primaryKey: true),
            Index("Orders", 2, "UX_Orders_Number", seeks: 0, updates: day, unique: true),
            Index("Orders", 3, "IX_Orders_Plain", seeks: 0, updates: day),
        ], 0);

        var indexes = estate.Tables.Single().Indexes;
        indexes.Select(i => (i.Name, i.Classification.Class, i.Unused)).ShouldBe(
        [
            ("PK_Orders", UsageClass.Hot, false),
            ("UX_Orders_Number", UsageClass.InsufficientHistory, false),
            ("IX_Orders_Plain", UsageClass.InsufficientHistory, true),
        ]);
        indexes[2].UpdatesInWindow.ShouldBe(30);
        indexes[2].ReservedBytes.ShouldBe(16 * 8192);
    }
}
