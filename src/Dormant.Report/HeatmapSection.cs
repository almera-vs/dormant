using System.Globalization;
using System.Text;
using Dormant.Core;
using Dormant.Core.History;
using Dormant.Core.Usage;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>
/// Section 2: a matrix with one row per table, grouped by instance and database, and one column per UTC day, or per
/// ISO week for windows longer than 90 days. Each cell is shaded by user reads on its busiest day. Partitioned tables
/// expand into one row per partition. Cells without enough observation are hatched, never shaded (D-054, D-064).
/// </summary>
internal static class HeatmapSection
{
    /// <summary>The width the matrix may take: an A4 landscape page with the name, size and class columns.</summary>
    public const int MaxWidth = 720;

    private const int RowHeight = 12;
    private const int AxisHeight = 26;

    /// <summary>The matrix's columns and their size: <paramref name="Pitch"/> pixels per column, of which <paramref name="Cell"/> is the cell.</summary>
    public sealed record Grid(CellGrouping Grouping, IReadOnlyList<CellColumn> Columns, int Pitch, int Cell)
    {
        public int Width => Columns.Count * Pitch;
    }

    public static Grid Layout(long firstDay, int days)
    {
        var grouping = HeatmapCells.GroupingFor(days);
        var columns = HeatmapCells.Columns(firstDay, days, grouping);
        var pitch = Math.Clamp(MaxWidth / Math.Max(1, columns.Count), 3, 24);
        return new Grid(grouping, columns, pitch, pitch >= 6 ? pitch - 2 : pitch - 1);
    }

    public static void Write(StringBuilder html, ReportContext context)
    {
        var estate = context.Estate;
        var days = estate.HistoryDays;
        html.Line("<section>").Line("<h2 id=\"heatmap\">Heatmap</h2>");
        if (days == 0 || estate.Tables.Count == 0)
        {
            html.Line("<p>No tables have been collected yet.</p>").Line("</section>");
            return;
        }

        var minCoverage = context.Options.MinCoverage;
        var firstDay = estate.Tables.Select(t => t.Daily?.FirstDay).FirstOrDefault(d => d is not null) ?? 0;
        var grid = Layout(firstDay, days);
        var cell = grid.Grouping == CellGrouping.Daily ? "day" : "ISO week (Monday to Sunday)";
        html.Line(I($"<p class=\"lede\">User reads over the last {days} days, outside maintenance windows: one cell per {cell}, shaded by the busiest day in it. ")
                + "Rows are sorted by size; click a column header to sort, hover a cell for its numbers. "
                + I($"A cell counts as observed only when every day in it was at least {Percent(minCoverage)} watched; a cell short of that, with no reads seen, is hatched: unknown, not idle.</p>"))
            .Line(Legend(grid.Grouping))
            .Line("<div class=\"controls\"><label>Find <input id=\"filter-text\" type=\"search\" placeholder=\"database.schema.table\"></label>"
                + "<label>Class <select id=\"filter-class\"><option value=\"\">any</option><option value=\"hot\">Hot</option><option value=\"warm\">Warm</option>"
                + "<option value=\"cold\">Cold</option><option value=\"dormant\">Dormant</option><option value=\"insufficient\">Insufficient history</option></select></label></div>");

        foreach (var instance in context.Instances)
        {
            var status = context.Status.Instances.FirstOrDefault(i => i.Name == instance);
            html.Line(I($"<h3>{E(instance)}</h3>"));
            foreach (var database in estate.Tables.Where(t => t.Instance == instance).GroupBy(t => t.Database).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                html.Line("<div class=\"heat-db\">")
                    .Line(I($"<h4>{E(database.Key)}</h4>"))
                    .Line("<table class=\"heat\">")
                    .Line(I($"<thead><tr><th data-sort=\"table\">Table</th><th class=\"num\" data-sort=\"size\" data-dir=\"desc\">Size</th><th data-sort=\"rank\">Class</th><th>{Axis(firstDay, grid, status, database.Key)}</th></tr></thead>"));
                foreach (var table in database.OrderByDescending(t => t.ReservedBytes).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Rows(html, table, grid, minCoverage);
                }

                html.Line("</table>").Line("</div>");
            }
        }

        html.Line("</section>");
    }

    private static void Rows(StringBuilder html, ClassifiedTable table, Grid grid, double minCoverage)
    {
        var name = $"{table.Database}.{table.Schema}.{table.Name}";
        var classes = string.Join(' ', table.Partitions.Select(p => p.Classification.Class).Append(table.Classification.Class).Distinct().Select(ClassKey));
        html.Line(I($"<tbody data-name=\"{E(name.ToLowerInvariant())}\" data-table=\"{E($"{table.Schema}.{table.Name}".ToLowerInvariant())}\" data-size=\"{table.ReservedBytes}\" data-rank=\"{Rank(table.Classification.Class)}\" data-classes=\"{classes}\">"))
            .Line(I($"<tr><td class=\"name\" title=\"{E(name)}\">{E(table.Schema)}.{E(table.Name)}</td><td class=\"num\">{Text.Bytes(table.ReservedBytes)}</td><td>{Chip(table.Classification)}</td><td class=\"cells\">{Cells(table.Daily, grid, minCoverage)}</td></tr>"));
        if (table.IsPartitioned)
        {
            foreach (var partition in table.Partitions.OrderBy(p => p.PartitionNumber))
            {
                html.Line(I($"<tr class=\"part\"><td class=\"name\" title=\"{E(partition.Range)}\">{E(partition.Label)}</td><td class=\"num\">{Text.Bytes(partition.ReservedBytes)}</td><td>{Chip(partition.Classification)}</td><td class=\"cells\">{Cells(partition.Daily, grid, minCoverage)}</td></tr>"));
            }
        }

        html.Line("</tbody>");
    }

    /// <summary>Hot first, then Warm, Insufficient history, Cold, Dormant: sorting descending puts the least used last.</summary>
    private static int Rank(UsageClass value) => value switch
    {
        UsageClass.Hot => 4,
        UsageClass.Warm => 3,
        UsageClass.InsufficientHistory => 2,
        UsageClass.Cold => 1,
        _ => 0,
    };

    /// <summary>
    /// One row of the matrix as a small SVG. Each run of cells is one path of cell-sized squares, so a long stretch without
    /// reads costs one element and one description, while every cell with reads carries its own numbers.
    /// </summary>
    private static string Cells(DailyUsage? usage, Grid grid, double minCoverage)
    {
        if (usage is null)
        {
            return string.Empty;
        }

        var svg = new StringBuilder();
        svg.Append(I($"<svg width=\"{grid.Width}\" height=\"{RowHeight}\" role=\"img\">"));
        foreach (var run in HeatmapCells.Runs(usage, minCoverage, grid.Grouping))
        {
            if (run.State == CellState.Absent)
            {
                continue;
            }

            var path = new StringBuilder(I($"M{run.Column * grid.Pitch} 0"));
            for (var i = 0; i < run.Length; i++)
            {
                if (i > 0)
                {
                    path.Append(I($"m{grid.Pitch} 0"));
                }

                path.Append(I($"h{grid.Cell}v{RowHeight}h-{grid.Cell}z"));
            }

            svg.Append(I($"<path class=\"{CssClass(run.State)}\" d=\"{path}\"><title>{E(run.Title)}</title></path>"));
        }

        return svg.Append("</svg>").ToString();
    }

    private static string CssClass(CellState state) => state switch
    {
        CellState.Absent => "ab",
        CellState.NoData => "nd",
        CellState.NoReads => "c0",
        CellState.Reads1 => "c1",
        CellState.Reads10 => "c2",
        CellState.Reads100 => "c3",
        _ => "c4",
    };

    /// <summary>Month labels, engine restarts of the instance (triangles) and counter resets of the database (circles), by column.</summary>
    private static string Axis(long firstDay, Grid grid, InstanceStatus? status, string database)
    {
        var svg = new StringBuilder();
        svg.Append(I($"<svg class=\"axis\" width=\"{grid.Width}\" height=\"{AxisHeight}\" role=\"img\" aria-label=\"time\">"));
        var labelEnd = 0;
        for (var c = 0; c < grid.Columns.Count; c++)
        {
            var column = grid.Columns[c];
            var firstOfMonth = Enumerable.Range(column.Start, column.Length)
                .Select(i => DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(firstDay + i)))
                .FirstOrDefault(d => d.Day == 1);
            if (c != 0 && firstOfMonth == default)
            {
                continue;
            }

            var date = firstOfMonth == default ? DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(firstDay + column.Start)) : firstOfMonth;
            var x = c * grid.Pitch;
            svg.Append(I($"<line x1=\"{x + 0.5}\" y1=\"0\" x2=\"{x + 0.5}\" y2=\"6\"/>"));
            var label = date.Month == 1 || c == 0 ? date.ToString("MMM yyyy", CultureInfo.InvariantCulture) : date.ToString("MMM", CultureInfo.InvariantCulture);
            var labelWidth = (label.Length * 6) + 6;
            if (x >= labelEnd && x + labelWidth <= grid.Width)
            {
                svg.Append(I($"<text x=\"{x + 2}\" y=\"15\">{label}</text>"));
                labelEnd = x + labelWidth;
            }
        }

        if (status is not null)
        {
            foreach (var restart in status.Restarts)
            {
                if (CenterOf(restart.AtUtc, firstDay, grid) is { } x)
                {
                    svg.Append(I($"<path class=\"restart\" d=\"M{x - 4} {AxisHeight} L{x + 4} {AxisHeight} L{x} {AxisHeight - 7} Z\"><title>{E($"Engine restart {Time(restart.AtUtc)}: counters started again from zero; the history is stitched across it")}</title></path>"));
                }
            }

            foreach (var reset in status.Resets.Where(r => r.Database == database))
            {
                if (CenterOf(reset.AtUtc, firstDay, grid) is { } x)
                {
                    svg.Append(I($"<circle class=\"reset\" cx=\"{x}\" cy=\"{AxisHeight - 4}\" r=\"3\"><title>{E($"Counter reset {Time(reset.AtUtc)}: {reset.Reason ?? "counters went back"}; {Text.Span(reset.Duration)} not observed")}</title></circle>"));
                }
            }
        }

        return svg.Append("</svg>").ToString();
    }

    /// <summary>The horizontal centre of the cell holding <paramref name="at"/>, or null outside the window.</summary>
    private static double? CenterOf(DateTimeOffset at, long firstDay, Grid grid)
    {
        var index = (at.ToUnixTimeMilliseconds() / 86_400_000) - firstDay;
        foreach (var (column, c) in grid.Columns.Select((column, c) => (column, c)))
        {
            if (index >= column.Start && index < column.Start + column.Length)
            {
                return (c * grid.Pitch) + (grid.Cell / 2.0);
            }
        }

        return null;
    }

    private static string Legend(CellGrouping grouping) =>
        "<div class=\"legend\">"
        + I($"<span class=\"legend-title\">{(grouping == CellGrouping.Daily ? "User reads that day" : "User reads on the busiest day of the week")}</span>")
        + "<span><i class=\"sw c0\"></i>none</span>"
        + "<span><i class=\"sw c1\"></i>1 to 9</span>"
        + "<span><i class=\"sw c2\"></i>10 to 99</span>"
        + "<span><i class=\"sw c3\"></i>100 to 999</span>"
        + "<span><i class=\"sw c4\"></i>1,000 or more</span>"
        + "<span><i class=\"sw nd\"></i>no data: not observed enough</span>"
        + "<span><i class=\"sw ab\"></i>not yet seen</span>"
        + "<span><svg width=\"10\" height=\"10\" class=\"axis\" style=\"margin-right:5px\"><path class=\"restart\" d=\"M1 10 L9 10 L5 3 Z\"/></svg>engine restart</span>"
        + "<span><svg width=\"10\" height=\"10\" class=\"axis\" style=\"margin-right:5px\"><circle class=\"reset\" cx=\"5\" cy=\"6\" r=\"3\"/></svg>counter reset</span>"
        + "</div>";
}
