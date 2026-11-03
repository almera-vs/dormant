using System.Text;
using Dormant.Core;
using Dormant.Core.History;
using Dormant.Core.Usage;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Section 1: estate size, size and cost by class, unused indexes, and how much history stands behind it.</summary>
internal static class SummarySection
{
    public static readonly UsageClass[] ClassOrder =
        [UsageClass.Hot, UsageClass.Warm, UsageClass.Cold, UsageClass.Dormant, UsageClass.InsufficientHistory];

    public static void Write(StringBuilder html, ReportContext context)
    {
        var estate = context.Estate;
        var options = context.Options;
        var units = estate.Units.ToList();
        var total = units.Sum(u => u.ReservedBytes);
        var partitions = units.Count(u => u.Partition is not null);
        var databases = estate.Tables.Select(t => (t.Instance, t.Database)).Distinct().Count();

        html.Line("<section>")
            .Line("<h2 class=\"first\" id=\"summary\">Summary</h2>")
            .Line("<div class=\"figures\">")
            .Line(Figure(Text.Bytes(total), I($"estate size: {Text.Plural(estate.Tables.Count, "table")}, {Text.Plural(partitions, "partition")} of partitioned tables, {Text.Plural(databases, "database")}")));
        foreach (var usageClass in (UsageClass[])[UsageClass.Dormant, UsageClass.Cold])
        {
            var bytes = units.Where(u => u.Classification.Class == usageClass).Sum(u => u.ReservedBytes);
            html.Line(Figure(Text.Bytes(bytes), I($"{ClassName(usageClass)}: {Share(bytes, total)} of the estate")));
        }

        var unused = estate.UnusedIndexes;
        html.Line(Figure(
                Text.Plural(unused.Count, "unused index", "unused indexes"),
                I($"{Text.Bytes(unused.Sum(i => i.ReservedBytes))}, {Text.Count(unused.Sum(i => i.UpdatesInWindow))} updates to maintain in {options.WarmDays} days")))
            .Line("</div>");

        ByClass(html, context, units, total);
        History(html, context);
        html.Line("</section>");
    }

    private static void ByClass(StringBuilder html, ReportContext context, List<ClassifiedUnit> units, long total)
    {
        var options = context.Options;
        html.Line("<table>")
            .Line("<thead><tr><th>Class</th><th>Rule</th><th class=\"num\">Size</th><th class=\"num\">Share</th><th class=\"num\">Estimated cost per month</th><th class=\"num\">Tables</th><th class=\"num\">Partitions</th></tr></thead>")
            .Line("<tbody>");
        foreach (var usageClass in ClassOrder)
        {
            var inClass = units.Where(u => u.Classification.Class == usageClass).ToList();
            var bytes = inClass.Sum(u => u.ReservedBytes);
            html.Line(I($"<tr><td><span class=\"chip k-{ClassKey(usageClass)}\">{ClassName(usageClass)}</span></td><td class=\"muted\">{E(Rule(usageClass, options))}</td>")
                + I($"<td class=\"num\">{Text.Bytes(bytes)}</td><td class=\"num\">{Share(bytes, total)}</td><td class=\"num\">{Money(context.Cost, bytes)}</td>")
                + I($"<td class=\"num\">{Text.Count(inClass.Count(u => u.Partition is null))}</td><td class=\"num\">{Text.Count(inClass.Count(u => u.Partition is not null))}</td></tr>"));
        }

        var writeOnly = units.Where(u => u.Classification.WriteOnly).ToList();
        html.Line("</tbody>").Line("</table>");
        if (writeOnly.Count > 0)
        {
            html.Line(I($"<p><span class=\"flag\">write-only</span> {Text.Plural(writeOnly.Count, "table or partition", "tables and partitions")} ({Text.Bytes(writeOnly.Sum(u => u.ReservedBytes))}) ")
                + I($"{(writeOnly.Count == 1 ? "is" : "are")} written to but not read in {options.WarmDays} days: maintained, but not consumed.</p>"));
        }

        html.Line(context.Cost.HasPrices
            ? I($"<p class=\"muted\">Money is an estimate from the prices in the configuration: {E(context.Cost.Currency)} per GB-month of data storage and of backup storage, times {context.Config.Cost.BackupCopies} retained full-backup {(context.Config.Cost.BackupCopies == 1 ? "copy" : "copies")}. Sizes are reserved space, 1 GB = 1024³ bytes.</p>")
            : "<p class=\"muted\">No prices are configured, so no money figures are shown. Set cost.data_storage_per_gb_month and cost.backup_storage_per_gb_month to your own prices. Sizes are reserved space, 1 GB = 1024³ bytes.</p>");
    }

    private static void History(StringBuilder html, ReportContext context)
    {
        html.Line("<h3>History behind these figures</h3>").Line("<ul>");
        foreach (var instance in context.Status.Instances)
        {
            html.Line(I($"<li><b>{E(instance.Name)}</b>: {E(Describe(instance, context.Estate.AsOfUtc))}</li>"));
        }

        html.Line("</ul>");
    }

    /// <summary>"142 days since 2026-06-14, 98.1% observed, 3 restarts stitched, 1 gap of 6h 0m, no counter resets".</summary>
    public static string Describe(InstanceStatus instance, DateTimeOffset now)
    {
        if (instance.HistoryStartUtc is not { } start)
        {
            return "no successful collection yet";
        }

        var length = now - start;
        var span = length.TotalDays >= 2 ? Text.Plural((int)length.TotalDays, "day") : Text.Span(length < TimeSpan.Zero ? TimeSpan.Zero : length);
        var gaps = MergeByTime(instance.Gaps);
        var gapText = gaps.Count switch
        {
            0 => "no gaps",
            1 => $"1 gap of {Text.Span(gaps[0].Duration)}",
            _ => $"{gaps.Count} gaps, {Text.Span(gaps.Aggregate(TimeSpan.Zero, (sum, g) => sum + g.Duration))} in total",
        };
        var resets = instance.Resets.Count == 0 ? "no counter resets" : Text.Plural(instance.Resets.Count, "counter reset");
        return I($"{span} of history since {start:yyyy-MM-dd}, {Text.Percent(instance.Coverage)} observed, ")
            + I($"{Text.Plural(instance.Restarts.Count, "restart")} stitched, {gapText}, {resets}");
    }

    /// <summary>A gap in the agent's readings shows up once per database; count it once.</summary>
    public static List<HistoryEvent> MergeByTime(IReadOnlyList<HistoryEvent> events) =>
        events
            .GroupBy(e => e.AtUtc)
            .Select(g => new HistoryEvent(g.Key, string.Join(", ", g.Select(e => e.Database).Distinct().Order(StringComparer.OrdinalIgnoreCase)), g.Max(e => e.Duration), g.First().Reason))
            .OrderByDescending(e => e.AtUtc)
            .ToList();

    private static string Rule(UsageClass usageClass, Core.Configuration.ClassificationOptions options) => usageClass switch
    {
        UsageClass.Hot => I($"read on most of the last {options.HotDays} days"),
        UsageClass.Warm => I($"read in the last {options.WarmDays} days"),
        UsageClass.Cold => I($"no reads in {options.WarmDays} days, read within {options.DormantDays}"),
        UsageClass.Dormant => I($"no reads in {options.DormantDays} days, at least {Percent(options.MinCoverage)} of them observed"),
        _ => "would be Cold or Dormant, but too little was observed to say",
    };

    private static string Figure(string value, string label) => $"<div class=\"figure\"><b>{E(value)}</b><span>{E(label)}</span></div>";

    private static string Share(long part, long total) => total == 0 ? "0.0%" : Percent((double)part / total);
}
