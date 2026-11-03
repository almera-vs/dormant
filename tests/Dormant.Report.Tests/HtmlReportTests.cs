using System.Text.RegularExpressions;
using Dormant.Core.Usage;

namespace Dormant.Report.Tests;

public sealed partial class HtmlReportTests : IClassFixture<SampleEstate>
{
    private readonly SampleEstate _sample;
    private readonly string _html;

    public HtmlReportTests(SampleEstate sample)
    {
        _sample = sample;
        _html = HtmlReport.Render(sample.Context);

        // Kept next to the test binaries, to look at in a browser.
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "sample-report.html"), _html);
    }

    [Fact]
    public void The_sample_is_classified_as_planted()
    {
        var tables = _sample.Estate.Tables.ToDictionary(t => t.Name);
        tables["Customers"].Classification.Class.ShouldBe(UsageClass.Hot);
        tables["AuditLog"].Classification.Class.ShouldBe(UsageClass.Dormant);
        tables["AuditLog"].Classification.WriteOnly.ShouldBeTrue();
        tables[SampleEstate.OddName].Classification.Class.ShouldBe(UsageClass.Cold);
        tables["Orders"].Partitions[0].Classification.Class.ShouldBe(UsageClass.Dormant);
        tables["Orders"].Partitions[^1].Classification.Class.ShouldBe(UsageClass.Hot);
        _sample.Estate.UnusedIndexes.ShouldHaveSingleItem().Index.ShouldBe("IX_Orders_Status");
    }

    [Fact]
    public void Has_all_six_sections_in_order()
    {
        string[] ids = ["summary", "heatmap", "cold-dormant", "unused-indexes", "partitions", "method"];
        var positions = ids.Select(id => _html.IndexOf($"<h2 id=\"{id}\"", StringComparison.Ordinal) is var i and >= 0 ? i : _html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)).ToList();
        positions.ShouldAllBe(p => p >= 0);
        positions.ShouldBeInOrder();
    }

    [Fact]
    public void Makes_no_external_requests()
    {
        // No URL anywhere: no stylesheet, font, script or image from elsewhere, and a policy that forbids them.
        UrlPattern().IsMatch(_html).ShouldBeFalse();
        _html.ShouldContain("default-src 'none'");
        _html.ShouldNotContain(" src=");
        _html.ShouldNotContain("@import");
    }

    [Fact]
    public void Encodes_names_from_the_store()
    {
        _html.ShouldNotContain(SampleEstate.OddName);
        _html.ShouldContain("Odd]&lt;b&gt;&quot;&amp;");
    }

    [Fact]
    public void Unobserved_days_are_hatched_never_drawn_as_days_without_reads()
    {
        // The gap: no reading on days 60 to 62, so those days (and the partial days around them) are 'no data'.
        var customers = _sample.Estate.Tables.Single(t => t.Name == "Customers").Daily.ShouldNotBeNull();
        var t0Day = Store.Tests.SnapshotBuilder.T0.ToUnixTimeMilliseconds() / 86_400_000;
        foreach (var gapDay in SampleEstate.GapDays)
        {
            var day = customers.Days[(int)(t0Day + gapDay - customers.FirstDay)];
            day.Observed.ShouldBe(0);
            day.Reads.ShouldBe(0);
            HeatmapCells.State(day, 0.9).ShouldBe(CellState.NoData);
        }

        var audit = HeatmapCells.Runs(_sample.Estate.Tables.Single(t => t.Name == "AuditLog").Daily.ShouldNotBeNull(), 0.9, CellGrouping.Weekly);
        audit.ShouldContain(r => r.State == CellState.NoData && r.Title.Contains("unknown, not idle", StringComparison.Ordinal));
        _html.ShouldContain("class=\"nd\"");
    }

    [Fact]
    public void Marks_the_engine_restart_on_the_time_axis()
    {
        _html.ShouldContain("class=\"restart\"");
        _html.ShouldContain("Engine restart");
    }

    [Fact]
    public void Expands_partitioned_tables_into_one_row_per_range()
    {
        _html.ShouldContain("<td class=\"name\" title=\"OrderDate &lt; 2026-02-01\">before 2026-02</td>");
        _html.ShouldContain("<td class=\"name\" title=\"2026-02-01 ≤ OrderDate &lt; 2026-03-01\">2026-02</td>");
        Regex.Count(_html, "<tr class=\"part\">").ShouldBe(17);
    }

    [Fact]
    public void Shows_drop_statements_for_review_only()
    {
        _html.ShouldContain("For review only.");
        _html.ShouldContain("USE [Sales];<br>DROP INDEX [IX_Orders_Status] ON [dbo].[Orders];");
        _html.ShouldNotContain("UX_Customers_Email];");
    }

    [Fact]
    public void Lists_cold_and_dormant_units_with_the_evidence()
    {
        var section = System.Net.WebUtility.HtmlDecode(Between("id=\"cold-dormant\"", "id=\"unused-indexes\""));
        section.ShouldContain("AuditLog");
        section.ShouldContain("Sales.dbo.Orders · 2026-02");
        section.ShouldNotContain("Customers");
        section.ShouldContain("of 180 days");
        section.ShouldContain("EUR");
    }

    [Fact]
    public void Summarises_the_history_honestly()
    {
        var summary = Between("id=\"summary\"", "id=\"heatmap\"");
        summary.ShouldContain("200 days of history");
        summary.ShouldContain("1 restart stitched");
        summary.ShouldContain("1 gap of");
    }

    [Fact]
    public void Real_history_carries_no_synthetic_notice_and_made_up_history_always_does()
    {
        _html.ShouldNotContain("synthetic demo data");

        var status = _sample.Status with
        {
            Instances = [_sample.Status.Instances[0] with { SyntheticCollections = 1600, LastSyntheticUtc = _sample.Now.AddHours(-3) }],
        };
        var html = HtmlReport.Render(_sample.Context with { Status = status });

        html.ShouldContain("<b>Contains synthetic demo data.</b>");
        html.ShouldContain("erp-prod: 1,600 readings up to 2027-05-21 09:00 UTC");
    }

    [Fact]
    public void Has_no_exclamation_marks_in_its_copy()
    {
        Regex.Replace(_html, "<(script|style)>.*?</\\1>|<!DOCTYPE html>", string.Empty, RegexOptions.Singleline).ShouldNotContain("!");
    }

    private string Between(string from, string to)
    {
        var start = _html.IndexOf(from, StringComparison.Ordinal);
        var end = _html.IndexOf(to, start, StringComparison.Ordinal);
        return _html[start..end];
    }

    [GeneratedRegex("(https?:)?//[a-z0-9.-]+\\.[a-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
