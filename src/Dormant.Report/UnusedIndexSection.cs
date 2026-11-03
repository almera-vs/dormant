using System.Text;
using Dormant.Core;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Section 4: nonclustered indexes nobody reads but every write must maintain, with DROP statements for review only.</summary>
internal static class UnusedIndexSection
{
    public static void Write(StringBuilder html, ReportContext context)
    {
        var options = context.Options;
        var unused = context.Estate.UnusedIndexes
            .OrderByDescending(i => i.ReservedBytes)
            .ThenBy(i => $"{i.Database}.{i.Schema}.{i.Table}.{i.Index}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        html.Line("<section>").Line("<h2 id=\"unused-indexes\">Unused indexes</h2>")
            .Line(I($"<p class=\"lede\">Nonclustered indexes with no user seeks, scans or lookups in the last {options.WarmDays} days, with at least {Percent(options.MinCoverage)} of them observed, that were written to in that time. ")
                + "Primary keys, unique indexes, unique constraints, clustered indexes and heaps are never listed: they enforce correctness even when nothing reads them.</p>");

        if (unused.Count == 0)
        {
            html.Line(I($"<p>None found. An index needs {options.WarmDays} well-observed days without reads before it is listed here.</p>")).Line("</section>");
            return;
        }

        html.Line("<div class=\"warning\"><b>For review only.</b> Dormant never runs these statements. Before dropping an index, test the workload without it on a copy of the database, "
                + "check for index hints and plan guides that name it, remember monthly and yearly jobs that may not have run in the window, and keep a script to recreate it.</div>")
            .Line(I($"<p>{Text.Plural(unused.Count, "index", "indexes")}, {Text.Bytes(unused.Sum(i => i.ReservedBytes))}, {Text.Count(unused.Sum(i => i.UpdatesInWindow))} update statements to maintain them in {options.WarmDays} days.</p>"))
            .Line("<table>")
            .Line(I($"<thead><tr><th data-sort=\"name\">Index</th><th class=\"num\" data-sort=\"size\" data-dir=\"desc\">Size</th><th class=\"num\" data-sort=\"updates\">Updates in {options.WarmDays} days</th><th class=\"num\">Coverage</th><th>Statement, for review</th></tr></thead>"))
            .Line("<tbody>");
        foreach (var index in unused)
        {
            var name = $"{index.Database}.{index.Schema}.{index.Table}.{index.Index}";
            html.Line(I($"<tr data-name=\"{E(name.ToLowerInvariant())}\" data-size=\"{index.ReservedBytes}\" data-updates=\"{index.UpdatesInWindow}\">")
                + I($"<td><span class=\"mono\">{E(index.Instance)}</span> {E(index.Database)}.{E(index.Schema)}.{E(index.Table)}<br><b>{E(index.Index)}</b></td>")
                + I($"<td class=\"num\">{Text.Bytes(index.ReservedBytes)}</td><td class=\"num\">{Text.Count(index.UpdatesInWindow)}</td><td class=\"num\">{Percent(index.Coverage)}</td>")
                + I($"<td><code>USE {E(Quote(index.Database))};<br>{E(index.DropStatement)}</code></td></tr>"));
        }

        html.Line("</tbody>").Line("</table>").Line("</section>");
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
