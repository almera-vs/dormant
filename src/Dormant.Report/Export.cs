using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dormant.Core.Configuration;
using Dormant.Core.Usage;

namespace Dormant.Report;

public enum ExportLevel
{
    /// <summary>One row per table.</summary>
    Table,

    /// <summary>One row per index or heap.</summary>
    Index,

    /// <summary>One row per partition of a partitioned table, one per other table: the units sizes add up over.</summary>
    Partition,
}

/// <summary>
/// The classification as CSV or JSON, one row per table, index or partition, with the same columns in both formats
/// (decision D-056). Dates are UTC days (yyyy-MM-dd), sizes are bytes, coverage is a fraction from 0 to 1, and a value
/// that is not known is empty (CSV) or null (JSON), never zero.
/// </summary>
public static class Export
{
    public const int FormatVersion = 1;

    private static readonly string[] TableColumns =
    [
        "instance", "database", "schema", "table", "class", "explanation", "warm_coverage", "dormant_coverage", "last_read", "last_write",
        "write_only", "rows", "reserved_bytes", "estimated_monthly_cost", "currency", "partitions", "last_reader", "last_reader_executed_utc",
    ];

    private static readonly string[] PartitionColumns =
    [
        "instance", "database", "schema", "table", "partition_number", "partition", "range", "class", "explanation", "warm_coverage",
        "dormant_coverage", "last_read", "last_write", "write_only", "rows", "reserved_bytes", "estimated_monthly_cost", "currency",
    ];

    private static readonly string[] IndexColumns =
    [
        "instance", "database", "schema", "table", "index", "index_id", "type", "is_primary_key", "is_unique", "is_unique_constraint",
        "is_disabled", "class", "explanation", "warm_coverage", "dormant_coverage", "last_read", "last_write", "updates_in_window", "unused",
        "reserved_bytes", "drop_statement",
    ];

    public static IReadOnlyList<string> Columns(ExportLevel level) => level switch
    {
        ExportLevel.Table => TableColumns,
        ExportLevel.Index => IndexColumns,
        _ => PartitionColumns,
    };

    /// <summary>The rows, in column order. Values are string, long, double, bool or null.</summary>
    public static IEnumerable<object?[]> Rows(EstateClassification estate, ExportLevel level, CostModel cost)
    {
        ArgumentNullException.ThrowIfNull(estate);
        ArgumentNullException.ThrowIfNull(cost);
        var tables = estate.Tables
            .OrderBy(t => t.Instance, StringComparer.Ordinal)
            .ThenBy(t => t.Database, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);
        string? currency = cost.HasPrices ? cost.Currency : null;
        double? Cost(long bytes) => cost.MonthlyCost(bytes) is { } value ? (double)Math.Round(value, 4) : null;

        switch (level)
        {
            case ExportLevel.Table:
                foreach (var t in tables)
                {
                    var c = t.Classification;
                    yield return
                    [
                        t.Instance, t.Database, t.Schema, t.Name, ClassValue(c.Class), c.Explanation, c.WarmCoverage, c.DormantCoverage,
                        Day(c.LastReadDay), Day(c.LastWriteDay), c.WriteOnly, t.RowCount, t.ReservedBytes, Cost(t.ReservedBytes), currency,
                        (long)(t.IsPartitioned ? t.Partitions.Count : 1), t.LastReader?.Name, t.LastReader?.LastExecutionUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ];
                }

                break;

            case ExportLevel.Partition:
                foreach (var t in tables)
                {
                    var units = t.IsPartitioned ? t.Partitions.OrderBy(p => p.PartitionNumber).ToList() : [];
                    if (units.Count == 0)
                    {
                        var whole = t.Partitions.Count > 0 ? t.Partitions[0] : null;
                        var c = t.Classification;
                        yield return
                        [
                            t.Instance, t.Database, t.Schema, t.Name, (long)(whole?.PartitionNumber ?? 1), whole?.Label ?? "all", whole?.Range ?? "all values",
                            ClassValue(c.Class), c.Explanation, c.WarmCoverage, c.DormantCoverage, Day(c.LastReadDay), Day(c.LastWriteDay), c.WriteOnly,
                            t.RowCount, t.ReservedBytes, Cost(t.ReservedBytes), currency,
                        ];
                        continue;
                    }

                    foreach (var p in units)
                    {
                        var c = p.Classification;
                        yield return
                        [
                            t.Instance, t.Database, t.Schema, t.Name, (long)p.PartitionNumber, p.Label, p.Range, ClassValue(c.Class), c.Explanation,
                            c.WarmCoverage, c.DormantCoverage, Day(c.LastReadDay), Day(c.LastWriteDay), c.WriteOnly, p.RowCount, p.ReservedBytes,
                            Cost(p.ReservedBytes), currency,
                        ];
                    }
                }

                break;

            default:
                var drops = estate.UnusedIndexes.ToDictionary(i => (i.Instance, i.Database, i.Schema, i.Table, i.Index), i => i.DropStatement);
                foreach (var t in tables)
                {
                    foreach (var x in t.Indexes.OrderBy(i => i.IndexId))
                    {
                        var c = x.Classification;
                        yield return
                        [
                            t.Instance, t.Database, t.Schema, t.Name, x.Name, (long)x.IndexId, x.Type, x.IsPrimaryKey, x.IsUnique, x.IsUniqueConstraint,
                            x.IsDisabled, ClassValue(c.Class), c.Explanation, c.WarmCoverage, c.DormantCoverage, Day(c.LastReadDay), Day(c.LastWriteDay),
                            x.UpdatesInWindow, x.Unused, x.ReservedBytes,
                            x.Name is null ? null : drops.GetValueOrDefault((t.Instance, t.Database, t.Schema, t.Name, x.Name)),
                        ];
                    }
                }

                break;
        }
    }

    /// <summary>RFC 4180 CSV with a header row and CRLF line ends.</summary>
    public static async Task WriteCsvAsync(TextWriter output, EstateClassification estate, ExportLevel level, CostModel cost, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(string.Join(',', Columns(level)) + "\r\n");
        foreach (var row in Rows(estate, level, cost))
        {
            ct.ThrowIfCancellationRequested();
            await output.WriteAsync(string.Join(',', row.Select(CsvField)) + "\r\n");
        }
    }

    public static async Task WriteJsonAsync(
        Stream output, EstateClassification estate, ExportLevel level, CostModel cost, ClassificationOptions options, string productVersion, CancellationToken ct,
        bool containsSynthetic = false)
    {
        ArgumentNullException.ThrowIfNull(estate);
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(options);
        // A data file, never embedded in HTML: names and text stay readable instead of \u-escaped.
        await using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        json.WriteStartObject();
        json.WriteNumber("dormant_export", FormatVersion);
        json.WriteString("dormant_version", productVersion);
        json.WriteString("as_of_utc", estate.AsOfUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        json.WriteString("level", level.ToString().ToLowerInvariant());
        json.WriteBoolean("contains_synthetic_data", containsSynthetic);
        json.WriteStartObject("classification");
        json.WriteNumber("hot_days", options.HotDays);
        json.WriteNumber("warm_days", options.WarmDays);
        json.WriteNumber("dormant_days", options.DormantDays);
        json.WriteNumber("min_coverage", options.MinCoverage);
        json.WriteEndObject();
        if (cost.HasPrices)
        {
            json.WriteString("currency", cost.Currency);
        }
        else
        {
            json.WriteNull("currency");
        }

        json.WriteStartArray("rows");
        var columns = Columns(level);
        foreach (var row in Rows(estate, level, cost))
        {
            ct.ThrowIfCancellationRequested();
            json.WriteStartObject();
            for (var i = 0; i < columns.Count; i++)
            {
                WriteValue(json, columns[i], row[i]);
            }

            json.WriteEndObject();
            if (json.BytesPending > 64 * 1024)
            {
                await json.FlushAsync(ct);
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    /// <summary>
    /// One CSV field. Text that a spreadsheet would run as a formula (starting with =, +, -, @, tab or carriage return)
    /// is prefixed with an apostrophe, so opening an export can never execute anything (decision D-056).
    /// </summary>
    public static string CsvField(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            bool b => b ? "true" : "false",
            double d => d.ToString("0.####", CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            string s when s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' => "'" + s,
            string s => s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    public static string ClassValue(UsageClass value) => value switch
    {
        UsageClass.Hot => "hot",
        UsageClass.Warm => "warm",
        UsageClass.Cold => "cold",
        UsageClass.Dormant => "dormant",
        _ => "insufficient_history",
    };

    private static string? Day(long? day) => day is { } d ? Classifier.Date(d) : null;

    private static void WriteValue(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case bool b:
                json.WriteBoolean(name, b);
                break;
            case long l:
                json.WriteNumber(name, l);
                break;
            case double d:
                json.WriteNumber(name, Math.Round(d, 4));
                break;
            default:
                json.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>UTF-8 with a byte order mark, so spreadsheets read non-ASCII names correctly from a CSV file.</summary>
    public static Encoding CsvFileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
}
