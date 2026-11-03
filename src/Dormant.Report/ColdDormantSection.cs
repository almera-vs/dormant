using System.Text;
using Dormant.Core;
using Dormant.Core.Usage;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Section 3: every Cold or Dormant table and partition, largest first, with the evidence behind it.</summary>
internal static class ColdDormantSection
{
    public static void Write(StringBuilder html, ReportContext context)
    {
        var options = context.Options;
        var units = context.Estate.Units
            .Where(u => u.Classification.Class is UsageClass.Cold or UsageClass.Dormant)
            .OrderByDescending(u => u.ReservedBytes)
            .ThenBy(u => u.Table.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.Partition?.PartitionNumber ?? 0)
            .ToList();
        var insufficient = context.Estate.Units.Where(u => u.Classification.Class == UsageClass.InsufficientHistory).ToList();

        html.Line("<section>").Line("<h2 id=\"cold-dormant\">Cold &amp; Dormant, by size</h2>");
        html.Line(I($"<p class=\"lede\">Cold: no user reads in the last {options.WarmDays} days, with at least {Percent(options.MinCoverage)} of them observed. ")
            + I($"Dormant: no user reads in the last {options.DormantDays} days, with at least {Percent(options.MinCoverage)} of them observed. Coverage is the share of that window Dormant actually watched.</p>"));

        if (units.Count == 0)
        {
            html.Line("<p>Nothing is Cold or Dormant.</p>");
        }
        else
        {
            var bytes = units.Sum(u => u.ReservedBytes);
            html.Line(I($"<p>{Text.Plural(units.Count, "table or partition", "tables and partitions")}, {Text.Bytes(bytes)} in total{(context.Cost.HasPrices ? $", an estimated {Money(context.Cost, bytes)} per month" : string.Empty)}.</p>"))
                .Line("<table>")
                .Line("<thead><tr><th data-sort=\"name\">Table or partition</th><th data-sort=\"rank\">Class</th><th class=\"num\">Coverage</th><th>Last read</th><th>Last write</th>"
                    + "<th class=\"num\" data-sort=\"size\" data-dir=\"desc\">Size</th><th class=\"num\">Estimated cost per month</th><th>Last known reader</th></tr></thead>")
                .Line("<tbody>");
            foreach (var unit in units)
            {
                var c = unit.Classification;
                var window = c.Class == UsageClass.Cold ? options.WarmDays : options.DormantDays;
                html.Line(I($"<tr data-name=\"{E(unit.DisplayName.ToLowerInvariant())}\" data-size=\"{unit.ReservedBytes}\" data-rank=\"{(c.Class == UsageClass.Cold ? 1 : 0)}\">")
                    + I($"<td><span class=\"mono\">{E(unit.Table.Instance)}</span> {E(unit.DisplayName)}{(unit.Partition is null ? string.Empty : $"<br><span class=\"muted\">{E(unit.Partition.Range)}</span>")}</td>")
                    + I($"<td>{Chip(c)}</td><td class=\"num\" title=\"{E(c.Explanation)}\">{Percent(c.DecidingCoverage)} of {window} days</td>")
                    + I($"<td>{Date(c.LastReadDay)}</td><td>{Date(c.LastWriteDay)}</td>")
                    + I($"<td class=\"num\">{Text.Bytes(unit.ReservedBytes)}</td><td class=\"num\">{Money(context.Cost, unit.ReservedBytes)}</td><td>{Reader(context, unit)}</td></tr>"));
            }

            html.Line("</tbody>").Line("</table>");
        }

        if (insufficient.Count > 0)
        {
            html.Line(I($"<p class=\"muted\">Not listed: {Text.Plural(insufficient.Count, "table or partition", "tables and partitions")} ({Text.Bytes(insufficient.Sum(u => u.ReservedBytes))}) with no reads seen, ")
                + I($"but with too little history to call them Cold or Dormant. They are shown as Insufficient history until enough days have been observed.</p>"));
        }

        html.Line("</section>");
    }

    private static string Reader(ReportContext context, ClassifiedUnit unit)
    {
        if (unit.Table.LastReader is { } reader)
        {
            var scope = unit.Partition is null ? string.Empty : " (reads the table)";
            return I($"{E(reader.Name)}{scope}<br><span class=\"muted\">last ran {E(Time(reader.LastExecutionUtc))}</span>");
        }

        var queryStore = context.Config.Instances.FirstOrDefault(i => i.Name == unit.Table.Instance)?.QueryStore.Enabled ?? false;
        return queryStore
            ? "<span class=\"muted\">none recorded in Query Store</span>"
            : "<span class=\"muted\">Query Store not read</span>";
    }
}
