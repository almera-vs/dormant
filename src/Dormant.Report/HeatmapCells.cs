using System.Globalization;
using Dormant.Core;
using Dormant.Core.Usage;

namespace Dormant.Report;

/// <summary>How one cell of the heatmap matrix is drawn (decisions D-054, D-064).</summary>
public enum CellState
{
    /// <summary>Dormant did not know the object yet: drawn empty.</summary>
    Absent,

    /// <summary>No reads seen, and some day in the cell was observed less than classification requires: hatched, never a shade.</summary>
    NoData,

    /// <summary>Every day in the cell observed, and no user reads.</summary>
    NoReads,

    /// <summary>1 to 9 user reads on the busiest day.</summary>
    Reads1,

    /// <summary>10 to 99 user reads on the busiest day.</summary>
    Reads10,

    /// <summary>100 to 999 user reads on the busiest day.</summary>
    Reads100,

    /// <summary>1,000 or more user reads on the busiest day.</summary>
    Reads1000,
}

/// <summary>One cell per day, or one per ISO week (Monday to Sunday) for longer windows.</summary>
public enum CellGrouping
{
    Daily,
    Weekly,
}

/// <summary>One column of the matrix: a run of days in the report window.</summary>
/// <param name="Start">Index of its first day in the window.</param>
/// <param name="Length">How many days of the window it covers; the first and last week can be partial.</param>
public readonly record struct CellColumn(int Start, int Length);

/// <summary>
/// Consecutive cells of one row drawn together: a cell with reads is always a run of its own, with its own numbers;
/// neighbouring cells without reads, without data or not yet seen share one run and one description.
/// </summary>
/// <param name="Column">The run's first column.</param>
/// <param name="Length">How many columns it spans.</param>
public sealed record CellRun(int Column, int Length, CellState State, string Title);

/// <summary>
/// Turns day-by-day usage into the cells of the heatmap matrix. A cell is shaded only by reads Dormant saw, and is drawn
/// as "no reads" only when every day in it was observed well enough; anything less is hatched: unknown, not idle.
/// </summary>
public static class HeatmapCells
{
    /// <summary>Windows longer than this many days are drawn by week.</summary>
    public const int MaxDailyDays = 90;

    public static CellGrouping GroupingFor(int days) => days <= MaxDailyDays ? CellGrouping.Daily : CellGrouping.Weekly;

    /// <summary>The window's columns, oldest first: one per day, or one per ISO week starting on Monday.</summary>
    public static IReadOnlyList<CellColumn> Columns(long firstDay, int days, CellGrouping grouping)
    {
        var columns = new List<CellColumn>();
        var start = 0;
        for (var i = 1; i <= days; i++)
        {
            var endsHere = i == days || grouping == CellGrouping.Daily || DateOf(firstDay + i).DayOfWeek == DayOfWeek.Monday;
            if (endsHere)
            {
                columns.Add(new CellColumn(start, i - start));
                start = i;
            }
        }

        return columns;
    }

    public static CellState State(UsageDay day, double minCoverage) => State([day], minCoverage);

    /// <summary>
    /// The state of a cell: empty when the object was never present in it; shaded by the busiest day when any reads were
    /// seen; "no reads" only when every present day was observed at least <paramref name="minCoverage"/>; else no data.
    /// </summary>
    public static CellState State(IReadOnlyList<UsageDay> days, double minCoverage)
    {
        ArgumentNullException.ThrowIfNull(days);
        var present = days.Where(d => d.Present).ToList();
        if (present.Count == 0)
        {
            return CellState.Absent;
        }

        return present.Max(d => d.Reads) switch
        {
            >= 1000 => CellState.Reads1000,
            >= 100 => CellState.Reads100,
            >= 10 => CellState.Reads10,
            >= 1 => CellState.Reads1,
            _ when present.All(d => d.Observed >= minCoverage) => CellState.NoReads,
            _ => CellState.NoData,
        };
    }

    /// <summary>The cells of one row, in column order, with cells of the same state but no reads merged into runs.</summary>
    public static IReadOnlyList<CellRun> Runs(DailyUsage usage, double minCoverage, CellGrouping grouping)
    {
        ArgumentNullException.ThrowIfNull(usage);
        var columns = Columns(usage.FirstDay, usage.Days.Count, grouping);
        var states = columns.Select(c => State(usage.Days.Skip(c.Start).Take(c.Length).ToList(), minCoverage)).ToList();
        var runs = new List<CellRun>();
        var start = 0;
        for (var i = 1; i <= columns.Count; i++)
        {
            var merges = i < columns.Count && states[i] == states[start] && states[i] is CellState.Absent or CellState.NoData or CellState.NoReads;
            if (merges)
            {
                continue;
            }

            var firstDay = columns[start].Start;
            var dayCount = columns[i - 1].Start + columns[i - 1].Length - firstDay;
            var days = usage.Days.Skip(firstDay).Take(dayCount).ToList();
            runs.Add(new CellRun(start, i - start, states[start], Title(usage.FirstDay, columns, start, i - start, days, states[start], grouping)));
            start = i;
        }

        return runs;
    }

    private static DateOnly DateOf(long day) => DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(day));

    private static string Title(
        long windowFirstDay, IReadOnlyList<CellColumn> columns, int first, int count, List<UsageDay> days, CellState state, CellGrouping grouping)
    {
        var from = Classifier.Date(windowFirstDay + columns[first].Start);
        var last = columns[first + count - 1];
        var when = (grouping, count) switch
        {
            (CellGrouping.Daily, 1) => from,
            (CellGrouping.Daily, _) => $"{from} to {Classifier.Date(windowFirstDay + last.Start)}",
            (_, 1) => string.Create(CultureInfo.InvariantCulture, $"Week of {from} ({Text.Plural(days.Count, "day")})"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{from} to {Classifier.Date(windowFirstDay + last.Start + last.Length - 1)} ({Text.Plural(count, "week")})"),
        };
        var present = days.Where(d => d.Present).ToList();
        if (present.Count == 0)
        {
            return $"{when}: not yet seen by Dormant";
        }

        var reads = present.Sum(d => d.Reads);
        var atLeast = present.Any(d => d.ReadsAtLeast) ? "at least " : string.Empty;
        var minObserved = Classifier.Percent(present.Min(d => d.Observed));
        var what = state switch
        {
            CellState.NoData => $"not enough observed to say (least observed day: {minObserved}); unknown, not idle",
            CellState.NoReads => $"no user reads (least observed day: {minObserved})",
            _ when grouping == CellGrouping.Weekly =>
                $"{atLeast}{Text.Count(reads)} user {(reads == 1 ? "read" : "reads")}, {Text.Count(present.Max(d => d.Reads))} on the busiest day",
            _ => $"{atLeast}{Text.Count(reads)} user {(reads == 1 ? "read" : "reads")}",
        };
        var maintenance = present.Sum(d => d.MaintenanceReads);
        var note = maintenance > 0
            ? string.Create(CultureInfo.InvariantCulture, $"; {Text.Count(maintenance)} reads inside maintenance windows, not counted")
            : string.Empty;
        return $"{when}: {what}{note}";
    }
}
