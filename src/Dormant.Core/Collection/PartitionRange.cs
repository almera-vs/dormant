using System.Globalization;

namespace Dormant.Core.Collection;

/// <summary>A partition boundary value: its SQL type and an exact, culture-free text form.</summary>
/// <param name="BaseType">SQL_VARIANT_PROPERTY(value, 'BaseType'), e.g. date, datetime2, int, nvarchar.</param>
/// <param name="Text">The value converted to text without loss (ISO 8601 for dates and times).</param>
public sealed record BoundaryValue(string BaseType, string Text)
{
    public string Token => $"{BaseType}:{Text}";

    public bool IsTemporal => BaseType is "date" or "datetime" or "datetime2" or "smalldatetime";

    /// <summary>The value as people read it: dates without a midnight time part.</summary>
    public string Display => IsTemporal && Text.EndsWith("T00:00:00", StringComparison.Ordinal) ? Text[..^9] : Text;

    public bool TryGetDate(out DateTime value) =>
        DateTime.TryParse(Text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) && IsTemporal;
}

/// <summary>
/// The range of values a partition holds, which is its identity: partition numbers change when boundaries are split or
/// merged, boundary values do not. A table or index that is not partitioned has one range, <see cref="Whole"/>.
/// </summary>
/// <param name="IsPartitioned">False for a heap or index stored on a filegroup rather than a partition scheme.</param>
/// <param name="RangeRight">RANGE RIGHT: the lower boundary belongs to the partition. RANGE LEFT: the upper one does.</param>
/// <param name="Lower">Lower boundary, or null for the first partition.</param>
/// <param name="Upper">Upper boundary, or null for the last partition.</param>
/// <param name="Column">The partitioning column, for labels.</param>
public sealed record PartitionRange(bool IsPartitioned, bool RangeRight, BoundaryValue? Lower, BoundaryValue? Upper, string? Column)
{
    public static readonly PartitionRange Whole = new(false, true, null, null, null);

    /// <summary>Stable identity of the range. Equal keys mean the same rows by definition, whatever the partition number.</summary>
    public string Key => !IsPartitioned
        ? "all"
        : $"{(RangeRight ? "R" : "L")}|{Lower?.Token ?? "-"}|{Upper?.Token ?? "+"}";

    /// <summary>The exact range, e.g. <c>2021-03-01 ≤ OrderDate &lt; 2021-04-01</c>.</summary>
    public string Description
    {
        get
        {
            if (!IsPartitioned)
            {
                return "all rows";
            }

            var column = Column ?? "value";
            var (lowerOp, upperOp) = RangeRight ? ("≤", "<") : ("<", "≤");
            return (Lower, Upper) switch
            {
                (null, null) => "all rows",
                (null, { } upper) => $"{column} {upperOp} {upper.Display}",
                ({ } lower, null) => $"{column} {(RangeRight ? "≥" : ">")} {lower.Display}",
                ({ } lower, { } upper) => $"{lower.Display} {lowerOp} {column} {upperOp} {upper.Display}",
            };
        }
    }

    /// <summary>
    /// A short label for calendar ranges of a RANGE RIGHT date function (<c>2021-03</c>, <c>2021-Q1</c>, <c>2021</c>,
    /// <c>2021-03-15</c>, <c>before 2019-01</c>, <c>from 2026-11</c>); otherwise <see cref="Description"/>.
    /// </summary>
    public string Label
    {
        get
        {
            if (!IsPartitioned)
            {
                return "all";
            }

            if (!RangeRight)
            {
                return Description;
            }

            var hasLower = Lower is { } lo && lo.TryGetDate(out _);
            var hasUpper = Upper is { } hi && hi.TryGetDate(out _);
            if (hasLower && hasUpper)
            {
                Lower!.TryGetDate(out var from);
                Upper!.TryGetDate(out var to);
                return CalendarLabel(from, to) ?? Description;
            }

            if (Lower is null && hasUpper)
            {
                Upper!.TryGetDate(out var to);
                return $"before {Short(to)}";
            }

            if (Upper is null && hasLower)
            {
                Lower!.TryGetDate(out var from);
                return $"from {Short(from)}";
            }

            return Description;
        }
    }

    private static string? CalendarLabel(DateTime from, DateTime to)
    {
        if (from.TimeOfDay != TimeSpan.Zero)
        {
            return null;
        }

        if (from.Day == 1 && from.Month == 1 && to == from.AddYears(1))
        {
            return from.ToString("yyyy", CultureInfo.InvariantCulture);
        }

        if (from.Day == 1 && from.Month % 3 == 1 && to == from.AddMonths(3))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{from.Year}-Q{(from.Month + 2) / 3}");
        }

        if (from.Day == 1 && to == from.AddMonths(1))
        {
            return from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }

        return to == from.AddDays(1) ? from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    // Month starts read as months ("2019-01"); anything else keeps its full date.
    private static string Short(DateTime value) =>
        value.TimeOfDay == TimeSpan.Zero && value.Day == 1
            ? value.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : value.TimeOfDay == TimeSpan.Zero
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
