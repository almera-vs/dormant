using System.Text;
using Dormant.Core;
using Dormant.Core.History;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Section 6: how the history was built, what interrupted it, the thresholds, and what each source cannot tell.</summary>
internal static class MethodSection
{
    private const int MaxEvents = 100;

    public static void Write(StringBuilder html, ReportContext context)
    {
        var options = context.Options;
        var config = context.Config;
        html.Line("<section>").Line("<h2 id=\"method\">Method &amp; caveats</h2>")
            .Line("<h3>How the history was built</h3>")
            .Line(I($"<p>Every {Text.Span(config.Collection.Interval)}, Dormant reads SQL Server's usage counters and sizes, read-only, and stores them locally. ")
                + "SQL Server resets these counters when the engine restarts, and for single objects when they are rebuilt or their metadata leaves the cache. "
                + "Dormant keeps the difference between consecutive readings, so each interval's activity survives the next reset: after a restart, the new counters are counted from zero, "
                + "and a counter that goes down is treated as a reset of that object, never as negative use.</p>")
            .Line(I($"<p>Time between two readings longer than {Text.Span(config.Collection.MaxObservedSpan)} is a gap: Dormant was not watching, so nothing is concluded about it. ")
                + "An object missing from a reading is unknown for that interval, not idle. Coverage, shown with every class, is the share of a window that was actually observed while the object existed. "
                + "Days are UTC days; activity in an interval is counted on the day the interval ended.</p>")
            .Line("<p>Only user reads count as use: seeks, scans and lookups by queries. Activity by SQL Server itself (statistics updates, internal checks) is kept apart and never counts. "
                + "Reads inside configured maintenance windows are recorded but not counted as use.</p>")
            .Line("<h3>Thresholds</h3>")
            .Line("<dl class=\"thresholds\">")
            .Line(I($"<dt>Hot</dt><dd>read on more than half of the last {options.HotDays} days</dd>"))
            .Line(I($"<dt>Warm</dt><dd>read in the last {options.WarmDays} days</dd>"))
            .Line(I($"<dt>Cold</dt><dd>no reads in the last {options.WarmDays} days, with at least {Percent(options.MinCoverage)} of them observed; read within {options.DormantDays} days</dd>"))
            .Line(I($"<dt>Dormant</dt><dd>no reads in the last {options.DormantDays} days, with at least {Percent(options.MinCoverage)} of them observed</dd>"))
            .Line("<dt>Insufficient history</dt><dd>would be Cold or Dormant, but too little of the window was observed to say so</dd>")
            .Line(I($"<dt>Write-only</dt><dd>written to, not read, in the last {options.WarmDays} days, with enough of them observed</dd>"))
            .Line(I($"<dt>Unused index</dt><dd>nonclustered, enforces nothing, no user reads in {options.WarmDays} days with at least {Percent(options.MinCoverage)} observed, and written to</dd>"))
            .Line(I($"<dt>Collection</dt><dd>every {Text.Span(config.Collection.Interval)}; a gap is anything longer than {Text.Span(config.Collection.MaxObservedSpan)}</dd>"));
        foreach (var instance in config.Instances.Where(i => context.Instances.Contains(i.Name)))
        {
            var windows = instance.MaintenanceWindows.Count == 0 ? "none" : string.Join(", ", instance.MaintenanceWindows.Select(w => w.Text));
            html.Line(I($"<dt>Maintenance, {E(instance.Name)}</dt><dd>{E(windows)}</dd>"));
        }

        html.Line("</dl>");

        foreach (var instance in context.Status.Instances)
        {
            Events(html, instance, context);
        }

        Limitations(html, context);
        html.Line("</section>");
    }

    private static void Events(StringBuilder html, InstanceStatus instance, ReportContext context)
    {
        html.Line(I($"<h3>Restarts, resets and gaps: {E(instance.Name)}</h3>"))
            .Line(I($"<p>{E(SummarySection.Describe(instance, context.Estate.AsOfUtc))}.</p>"));
        var gaps = SummarySection.MergeByTime(instance.Gaps);
        if (instance.Restarts.Count + gaps.Count + instance.Resets.Count == 0)
        {
            return;
        }

        var rows = instance.Restarts.Select(e => (e.AtUtc, Kind: "Engine restart", Detail: $"counters started again from zero; stitched. Databases: {e.Database}"))
            .Concat(gaps.Select(e => (e.AtUtc, Kind: "Gap", Detail: $"{Text.Span(e.Duration)} without a reading; unknown, not idle. Databases: {e.Database}")))
            .Concat(instance.Resets.Select(e => (e.AtUtc, Kind: "Counter reset", Detail: $"{e.Database}: {e.Reason ?? "counters went back"}; {Text.Span(e.Duration)} not observed")))
            .OrderByDescending(e => e.AtUtc)
            .ToList();
        html.Line("<table>").Line("<thead><tr><th>When</th><th>What</th><th>Detail</th></tr></thead>").Line("<tbody>");
        foreach (var row in rows.Take(MaxEvents))
        {
            html.Line(I($"<tr><td class=\"mono\">{E(Time(row.AtUtc))}</td><td>{E(row.Kind)}</td><td>{E(row.Detail)}</td></tr>"));
        }

        html.Line("</tbody>").Line("</table>");
        if (rows.Count > MaxEvents)
        {
            html.Line(I($"<p class=\"muted\">{Text.Count(rows.Count - MaxEvents)} earlier events are not shown here; 'dormant status' lists them all.</p>"));
        }
    }

    private static void Limitations(StringBuilder html, ReportContext context)
    {
        html.Line("<h3>What each source cannot tell</h3>")
            .Line("<ul>")
            .Line("<li><b>Index usage statistics</b> (sys.dm_db_index_usage_stats) count statements, not rows, per index. They are the primary signal for tables and indexes. "
                + "They do not report memory-optimized tables, so those are always Insufficient history.</li>")
            .Line("<li><b>Operational statistics</b> (sys.dm_db_index_operational_stats) give the per-partition picture. Their rows can vanish when metadata leaves the cache, "
                + "and they do not separate user activity from maintenance: a statistics update or an index build reads partitions too, so a partition can look read when only maintenance touched it. "
                + "When a partition's counters were unknown on a day its table was read, that day does not count as observed for the partition.</li>")
            .Line("<li><b>Query Store</b> names readers only where it is enabled and read, and by default it skips infrequent and cheap queries. A last known reader is supporting information; "
                + "no reader recorded proves nothing, and Query Store never changes a class.</li>")
            .Line("<li><b>Sizes</b> are reserved space from sys.dm_db_partition_stats at the latest reading, including every index of the table.</li>")
            .Line(I($"<li><b>The engine's own last-read time</b> is used for a table's last read when it is newer than Dormant's history, unless it falls inside a maintenance window. ")
                + I($"It can show a read from before Dormant started, but it is lost on restart, so its absence proves nothing.</li>"))
            .Line("<li><b>Nothing here is a recommendation to delete.</b> A table can be read once a year by a job that has not run in the window. Check with the owners of the data before acting.</li>")
            .Line("</ul>");
        if (context.Estate.HistoryDays < context.Options.DormantDays)
        {
            html.Line(I($"<p class=\"muted\">The heatmap shows {context.Estate.HistoryDays} days; classes are decided over the full thresholds above, whatever the heatmap shows.</p>"));
        }
    }
}
