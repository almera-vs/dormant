using System.Globalization;
using System.Net;
using System.Text;
using Dormant.Core;
using Dormant.Core.Usage;

namespace Dormant.Report;

/// <summary>Encoding and formatting shared by the report's sections. Every value from the store is encoded.</summary>
internal static class Html
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public static string I(FormattableString value) => FormattableString.Invariant(value);

    public static StringBuilder Line(this StringBuilder builder, string value) => builder.Append(value).Append('\n');

    public static string Date(long? day) => day is { } d ? Classifier.Date(d) : "none seen";

    public static string Time(DateTimeOffset value) => Text.Time(value);

    public static string Percent(double fraction) => Classifier.Percent(fraction);

    public static string ClassName(UsageClass value) => value switch
    {
        UsageClass.Hot => "Hot",
        UsageClass.Warm => "Warm",
        UsageClass.Cold => "Cold",
        UsageClass.Dormant => "Dormant",
        _ => "Insufficient history",
    };

    /// <summary>The CSS class that colours a usage class, and the key the filter matches on.</summary>
    public static string ClassKey(UsageClass value) => value switch
    {
        UsageClass.Hot => "hot",
        UsageClass.Warm => "warm",
        UsageClass.Cold => "cold",
        UsageClass.Dormant => "dormant",
        _ => "insufficient",
    };

    public static string Chip(Classification classification) =>
        $"<span class=\"chip k-{ClassKey(classification.Class)}\" title=\"{E(classification.Explanation)}\">{ClassName(classification.Class)}</span>"
        + (classification.WriteOnly ? " <span class=\"flag\" title=\"Written to, but not read\">write-only</span>" : string.Empty);

    public static string Money(CostModel cost, long bytes) =>
        cost.MonthlyCost(bytes) is { } value
            ? string.Create(CultureInfo.InvariantCulture, $"{E(cost.Currency)} {value:N2}")
            : "<span class=\"muted\">set your price</span>";
}
