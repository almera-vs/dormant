using System.Text;
using Dormant.Core.Configuration;
using Dormant.Core.History;
using Dormant.Core.Usage;
using static Dormant.Report.Html;

namespace Dormant.Report;

/// <summary>Everything one report is drawn from.</summary>
/// <param name="Estate">The classification, with day-by-day reads over <see cref="EstateClassification.HistoryDays"/>.</param>
/// <param name="Status">How complete the history is: restarts, resets and gaps, per instance.</param>
/// <param name="ProductVersion">The Dormant version that wrote the report.</param>
public sealed record ReportContext(EstateClassification Estate, StoreStatus Status, DormantConfig Config, string ProductVersion)
{
    public CostModel Cost { get; } = new(Config.Cost);

    public ClassificationOptions Options => Config.Classification;

    /// <summary>The instances in the report, in name order.</summary>
    public IReadOnlyList<string> Instances =>
        Status.Instances.Select(i => i.Name)
            .Union(Estate.Tables.Select(t => t.Instance), StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// The heatmap report: one self-contained HTML file with inline CSS and a little inline script, no external requests,
/// printable to A4 landscape, in six sections.
/// </summary>
public static class HtmlReport
{
    public static string Render(ReportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var html = new StringBuilder(64 * 1024);
        html.Line("<!DOCTYPE html>")
            .Line("<html lang=\"en\">")
            .Line("<head>")
            .Line("<meta charset=\"utf-8\">")
            .Line("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Line("<meta name=\"color-scheme\" content=\"dark\">")
            .Line("<meta name=\"generator\" content=\"" + E("Dormant " + context.ProductVersion) + "\">")
            // Nothing may load from elsewhere: no network requests, even if the file is opened on a connected machine.
            .Line("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:\">")
            .Line(I($"<title>Dormant usage report · {E(Time(context.Estate.AsOfUtc))}</title>"))
            .Line("<style>").Line(Assets.Css).Line("</style>")
            .Line("</head>")
            .Line("<body>")
            .Line("<svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\"><defs>"
                + "<pattern id=\"hatch\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\" patternTransform=\"rotate(45)\">"
                + "<rect class=\"hatch-bg\" width=\"4\" height=\"4\"/><line class=\"hatch-line\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"4\" stroke-width=\"1.4\"/>"
                + "</pattern></defs></svg>")
            .Line("<main>");

        Header(html, context);
        SummarySection.Write(html, context);
        HeatmapSection.Write(html, context);
        ColdDormantSection.Write(html, context);
        UnusedIndexSection.Write(html, context);
        PartitionSection.Write(html, context);
        MethodSection.Write(html, context);

        html.Line(I($"<footer>Written by Dormant {E(context.ProductVersion)} from the local store only. Read-only: nothing was changed on any instance. Times are UTC.</footer>"))
            .Line("</main>")
            .Line("<script>").Line(Assets.Script).Line("</script>")
            .Line("</body>")
            .Line("</html>");
        return html.ToString();
    }

    /// <summary>Made-up history is never presented as real: a notice at the top, naming how much and until when (D-058).</summary>
    private static string SyntheticNotice(ReportContext context)
    {
        var synthetic = context.Status.Instances.Where(i => i.SyntheticCollections > 0).ToList();
        if (synthetic.Count == 0)
        {
            return string.Empty;
        }

        var detail = string.Join("; ", synthetic.Select(i => I($"{E(i.Name)}: {Core.Text.Plural(i.SyntheticCollections, "reading")} up to {E(Time(i.LastSyntheticUtc!.Value))}")));
        return "<div class=\"warning synthetic\"><b>Contains synthetic demo data.</b> Part of this history was made up to demonstrate Dormant, not collected from SQL Server: "
            + detail + ". Nothing in it describes real usage.</div>";
    }

    private static void Header(StringBuilder html, ReportContext context)
    {
        var instances = context.Instances;
        html.Line("<header>")
            .Line("<h1>Dormant usage report</h1>")
            .Line(I($"<p class=\"lede\">As of {E(Time(context.Estate.AsOfUtc))} · {E(string.Join(", ", instances))} · user reads over the last {context.Estate.HistoryDays} days</p>"))
            .Line(SyntheticNotice(context))
            .Line("<nav><a href=\"#summary\">Summary</a><a href=\"#heatmap\">Heatmap</a><a href=\"#cold-dormant\">Cold &amp; Dormant</a>"
                + "<a href=\"#unused-indexes\">Unused indexes</a><a href=\"#partitions\">Partitioned tables</a><a href=\"#method\">Method &amp; caveats</a></nav>")
            .Line("</header>");
    }
}
