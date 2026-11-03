using System.Text;
using Dormant.Core;
using Dormant.Core.Usage;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Section 5: each partitioned table as a strip of its partitions in range order, shaded by class.</summary>
internal static class PartitionSection
{
    public static void Write(StringBuilder html, ReportContext context)
    {
        var tables = context.Estate.Tables
            .Where(t => t.IsPartitioned)
            .OrderByDescending(t => t.ReservedBytes)
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        html.Line("<section>").Line("<h2 id=\"partitions\">Partitioned tables</h2>");
        if (tables.Count == 0)
        {
            html.Line("<p>No partitioned tables.</p>").Line("</section>");
            return;
        }

        html.Line("<p class=\"lede\">Each strip is one table, its partitions in range order from the lowest boundary to the highest. "
                + "Partitions are classified on their own operational counters; hover a partition for its range, size and evidence.</p>")
            .Line("<div class=\"legend\">"
                + string.Concat(SummarySection.ClassOrder.Select(c => $"<span><i class=\"k-{ClassKey(c)}\"></i>{ClassName(c)}</span>"))
                + "</div>");

        foreach (var table in tables)
        {
            var partitions = table.Partitions.OrderBy(p => p.PartitionNumber).ToList();
            html.Line("<div class=\"strip-table\">")
                .Line(I($"<b><span class=\"mono\">{E(table.Instance)}</span> {E(table.DisplayName)}</b> <span class=\"muted\">{Text.Bytes(table.ReservedBytes)}, {Text.Plural(partitions.Count, "partition")}</span>"))
                .Append("<div class=\"strip\">");
            foreach (var p in partitions)
            {
                var title = $"{p.Label} · {p.Range} · {ClassName(p.Classification.Class)} · {Text.Bytes(p.ReservedBytes)}, {Text.Plural(p.RowCount, "row")} · {p.Classification.Explanation}";
                html.Append(I($"<span class=\"k-{ClassKey(p.Classification.Class)}\" title=\"{E(title)}\"></span>"));
            }

            html.Line("</div>")
                .Line(I($"<div class=\"strip-ends\"><span>{E(partitions[0].Label)}</span><span>{E(partitions[^1].Label)}</span></div>"))
                .Line(I($"<p>{E(Breakdown(partitions))}</p>"))
                .Line("</div>");
        }

        html.Line("</section>");
    }

    /// <summary>"Dormant: 48 partitions, 210.0 GB (2015-01 – 2018-12); Hot: 3 partitions, 2.0 GB (2026-09 – 2026-11)": lowest and highest range in each class.</summary>
    private static string Breakdown(List<ClassifiedPartition> partitions) =>
        string.Join("; ", SummarySection.ClassOrder
            .Reverse()
            .Select(c => partitions.Where(p => p.Classification.Class == c).ToList())
            .Where(group => group.Count > 0)
            .Select(group =>
            {
                var span = group.Count == 1 ? group[0].Label : $"{group[0].Label} – {group[^1].Label}";
                return $"{ClassName(group[0].Classification.Class)}: {Text.Plural(group.Count, "partition")}, {Text.Bytes(group.Sum(p => p.ReservedBytes))} ({span})";
            }));
}
