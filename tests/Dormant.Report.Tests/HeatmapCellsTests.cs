using Dormant.Core.Usage;

namespace Dormant.Report.Tests;

public sealed class HeatmapCellsTests
{
    private const double MinCoverage = 0.9;

    private static UsageDay Day(long reads = 0, double observed = 1, bool present = true, bool atLeast = false, long maintenance = 0) =>
        new(present, observed, reads, atLeast, maintenance);

    [Theory]
    [InlineData(1, CellState.Reads1)]
    [InlineData(9, CellState.Reads1)]
    [InlineData(10, CellState.Reads10)]
    [InlineData(99, CellState.Reads10)]
    [InlineData(100, CellState.Reads100)]
    [InlineData(999, CellState.Reads100)]
    [InlineData(1000, CellState.Reads1000)]
    [InlineData(5_000_000, CellState.Reads1000)]
    public void Reads_are_shaded_in_four_steps(long reads, CellState expected) =>
        HeatmapCells.State(Day(reads), MinCoverage).ShouldBe(expected);

    [Fact]
    public void A_day_without_reads_is_idle_only_when_observed_enough()
    {
        HeatmapCells.State(Day(observed: 1), MinCoverage).ShouldBe(CellState.NoReads);
        HeatmapCells.State(Day(observed: 0.9), MinCoverage).ShouldBe(CellState.NoReads);
        HeatmapCells.State(Day(observed: 0.89), MinCoverage).ShouldBe(CellState.NoData);
        HeatmapCells.State(Day(observed: 0), MinCoverage).ShouldBe(CellState.NoData);
    }

    [Fact]
    public void Reads_on_a_partly_observed_day_are_still_reads()
    {
        // Seeing reads is evidence of use however little of the day was watched.
        HeatmapCells.State(Day(reads: 3, observed: 0.1), MinCoverage).ShouldBe(CellState.Reads1);
    }

    [Fact]
    public void Days_before_the_object_was_seen_are_empty()
    {
        HeatmapCells.State(Day(present: false), MinCoverage).ShouldBe(CellState.Absent);
        HeatmapCells.State(Day(reads: 50, present: false), MinCoverage).ShouldBe(CellState.Absent);
    }

    // 2026-11-01, a Sunday, is day 20758; 2026-11-02 is a Monday.
    private const long Sunday = 20758;

    [Fact]
    public void Daily_cells_without_reads_share_a_run_and_every_cell_with_reads_has_its_own()
    {
        var usage = new DailyUsage(Sunday, [Day(present: false), Day(), Day(), Day(observed: 0), Day(reads: 12), Day(reads: 40, atLeast: true)]);

        var runs = HeatmapCells.Runs(usage, MinCoverage, CellGrouping.Daily);

        runs.Select(r => (r.Column, r.Length, r.State)).ShouldBe(
        [
            (0, 1, CellState.Absent),
            (1, 2, CellState.NoReads),
            (3, 1, CellState.NoData),
            (4, 1, CellState.Reads10),
            (5, 1, CellState.Reads10),
        ]);
        runs[1].Title.ShouldBe("2026-11-02 to 2026-11-03: no user reads (least observed day: 100.0%)");
        runs[2].Title.ShouldBe("2026-11-04: not enough observed to say (least observed day: 0.0%); unknown, not idle");
        runs[3].Title.ShouldBe("2026-11-05: 12 user reads");
        runs[4].Title.ShouldBe("2026-11-06: at least 40 user reads");
    }

    [Fact]
    public void Weeks_run_from_Monday_to_Sunday_and_the_ends_can_be_partial()
    {
        // Sunday, then a full week, then Monday and Tuesday.
        var columns = HeatmapCells.Columns(Sunday, 10, CellGrouping.Weekly);

        columns.ShouldBe([new CellColumn(0, 1), new CellColumn(1, 7), new CellColumn(8, 2)]);
        HeatmapCells.Columns(Sunday, 10, CellGrouping.Daily).Count.ShouldBe(10);
    }

    [Fact]
    public void A_week_is_idle_only_when_every_day_in_it_was_observed()
    {
        UsageDay[] observed = [Day(), Day(), Day(), Day(), Day(), Day(), Day()];
        UsageDay[] oneDayShort = [Day(), Day(), Day(observed: 0.5), Day(), Day(), Day(), Day()];

        HeatmapCells.State(observed, MinCoverage).ShouldBe(CellState.NoReads);
        HeatmapCells.State(oneDayShort, MinCoverage).ShouldBe(CellState.NoData);
    }

    [Fact]
    public void A_week_is_shaded_by_its_busiest_day()
    {
        UsageDay[] week = [Day(reads: 3), Day(reads: 150, observed: 0.2), Day(), Day(observed: 0), Day(), Day(), Day()];

        HeatmapCells.State(week, MinCoverage).ShouldBe(CellState.Reads100);
        var run = HeatmapCells.Runs(new DailyUsage(Sunday + 1, week), MinCoverage, CellGrouping.Weekly).ShouldHaveSingleItem();
        run.Title.ShouldBe("Week of 2026-11-02 (7 days): 153 user reads, 150 on the busiest day");
    }

    [Fact]
    public void A_week_the_object_entered_counts_only_its_days()
    {
        UsageDay[] week = [Day(present: false, observed: 0), Day(present: false, observed: 0), Day(), Day(), Day(), Day(), Day()];

        HeatmapCells.State(week, MinCoverage).ShouldBe(CellState.NoReads);
    }

    [Fact]
    public void Weeks_without_reads_share_a_run()
    {
        var days = Enumerable.Repeat(Day(), 14).Concat([Day(reads: 5)]).ToArray();

        var runs = HeatmapCells.Runs(new DailyUsage(Sunday + 1, days), MinCoverage, CellGrouping.Weekly);

        runs.Select(r => (r.Column, r.Length, r.State)).ShouldBe([(0, 2, CellState.NoReads), (2, 1, CellState.Reads1)]);
        runs[0].Title.ShouldBe("2026-11-02 to 2026-11-15 (2 weeks): no user reads (least observed day: 100.0%)");
    }

    [Fact]
    public void Maintenance_reads_are_mentioned_but_not_shaded()
    {
        var runs = HeatmapCells.Runs(new DailyUsage(Sunday, [Day(maintenance: 400)]), MinCoverage, CellGrouping.Daily);

        runs.ShouldHaveSingleItem().State.ShouldBe(CellState.NoReads);
        runs[0].Title.ShouldEndWith("; 400 reads inside maintenance windows, not counted");
    }

    [Theory]
    [InlineData(30, CellGrouping.Daily)]
    [InlineData(90, CellGrouping.Daily)]
    [InlineData(91, CellGrouping.Weekly)]
    [InlineData(180, CellGrouping.Weekly)]
    [InlineData(1095, CellGrouping.Weekly)]
    public void Windows_up_to_90_days_are_daily_and_longer_ones_weekly_and_all_fit_a_landscape_page(int days, CellGrouping grouping)
    {
        var grid = HeatmapSection.Layout(Sunday, days);

        grid.Grouping.ShouldBe(grouping);
        grid.Width.ShouldBeLessThanOrEqualTo(HeatmapSection.MaxWidth);
        grid.Cell.ShouldBeLessThan(grid.Pitch);
        grid.Cell.ShouldBeGreaterThanOrEqualTo(2);
    }
}
