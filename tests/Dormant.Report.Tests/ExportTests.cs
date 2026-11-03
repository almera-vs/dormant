using System.Text;
using System.Text.Json;
using Dormant.Core.Usage;

namespace Dormant.Report.Tests;

public sealed class ExportTests : IClassFixture<SampleEstate>
{
    private readonly SampleEstate _sample;

    public ExportTests(SampleEstate sample)
    {
        _sample = sample;
    }

    private CostModel Cost => new(_sample.Config.Cost);

    private async Task<string[]> CsvAsync(ExportLevel level)
    {
        await using var writer = new StringWriter();
        await Export.WriteCsvAsync(writer, _sample.Estate, level, Cost, TestContext.Current.CancellationToken);
        return writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<JsonElement> JsonAsync(ExportLevel level)
    {
        using var stream = new MemoryStream();
        await Export.WriteJsonAsync(stream, _sample.Estate, level, Cost, _sample.Config.Classification, "0.1.0-test", TestContext.Current.CancellationToken);
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    [Theory]
    [InlineData(ExportLevel.Table, 4)]
    [InlineData(ExportLevel.Index, 6)]
    [InlineData(ExportLevel.Partition, 17 + 3)]
    public async Task Has_one_row_per_table_index_or_partition(ExportLevel level, int rows)
    {
        var lines = await CsvAsync(level);

        lines[0].ShouldBe(string.Join(',', Export.Columns(level)));
        lines.Length.ShouldBe(rows + 1);
        (await JsonAsync(level)).GetProperty("rows").GetArrayLength().ShouldBe(rows);
    }

    [Fact]
    public void Partition_rows_add_up_to_the_estate()
    {
        var columns = Export.Columns(ExportLevel.Partition).ToList();
        var bytes = Export.Rows(_sample.Estate, ExportLevel.Partition, Cost).Sum(r => (long)r[columns.IndexOf("reserved_bytes")]!);

        bytes.ShouldBe(_sample.Estate.Units.Sum(u => u.ReservedBytes));
    }

    [Fact]
    public async Task Unknown_values_are_empty_or_null_never_zero()
    {
        var json = await JsonAsync(ExportLevel.Table);
        var audit = json.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("table").GetString() == "AuditLog");
        audit.GetProperty("last_read").ValueKind.ShouldBe(JsonValueKind.Null);
        audit.GetProperty("class").GetString().ShouldBe("dormant");
        audit.GetProperty("write_only").GetBoolean().ShouldBeTrue();

        var csv = await CsvAsync(ExportLevel.Table);
        var columns = Export.Columns(ExportLevel.Table).ToList();
        csv.Single(l => l.Contains(",AuditLog,", StringComparison.Ordinal)).Split(',')[columns.IndexOf("last_read")].ShouldBeEmpty();
    }

    [Fact]
    public async Task Json_carries_the_thresholds_and_currency()
    {
        var json = await JsonAsync(ExportLevel.Table);

        json.GetProperty("dormant_export").GetInt32().ShouldBe(Export.FormatVersion);
        json.GetProperty("level").GetString().ShouldBe("table");
        json.GetProperty("as_of_utc").GetString().ShouldBe("2027-05-21T12:00:00Z");
        json.GetProperty("classification").GetProperty("dormant_days").GetInt32().ShouldBe(180);
        json.GetProperty("currency").GetString().ShouldBe("EUR");
    }

    [Fact]
    public async Task Only_unused_indexes_get_a_drop_statement()
    {
        var json = await JsonAsync(ExportLevel.Index);
        var rows = json.GetProperty("rows").EnumerateArray().ToList();

        var dropped = rows.Where(r => r.GetProperty("drop_statement").ValueKind == JsonValueKind.String).ToList();
        dropped.ShouldHaveSingleItem().GetProperty("index").GetString().ShouldBe("IX_Orders_Status");
        dropped[0].GetProperty("drop_statement").GetString().ShouldBe("DROP INDEX [IX_Orders_Status] ON [dbo].[Orders];");
        var email = rows.Single(r => r.GetProperty("index").GetString() == "UX_Customers_Email");
        email.GetProperty("unused").GetBoolean().ShouldBeFalse();
        email.GetProperty("is_unique").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Json_keeps_names_readable()
    {
        using var stream = new MemoryStream();
        await Export.WriteJsonAsync(stream, _sample.Estate, ExportLevel.Table, Cost, _sample.Config.Classification, "0.1.0+abc", TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        text.ShouldContain("\"0.1.0+abc\"");
        text.ShouldContain("\"Odd]<b>\\\"&\"");
    }

    [Theory]
    [InlineData("Orders", "Orders")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("", "")]
    public void Csv_fields_are_quoted_and_never_formulas(string value, string expected) =>
        Export.CsvField(value).ShouldBe(expected);

    [Fact]
    public void Csv_numbers_and_flags_are_invariant()
    {
        Export.CsvField(1234567L).ShouldBe("1234567");
        Export.CsvField(0.98765).ShouldBe("0.9877");
        Export.CsvField(true).ShouldBe("true");
        Export.CsvField(null).ShouldBe(string.Empty);
    }
}
