using System.Globalization;

namespace Dormant.Core;

/// <summary>Formatting shared by the plain-text command outputs. Times are always shown in UTC.</summary>
public static class Text
{
    public static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Span(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h"),
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m"),
        { TotalMinutes: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.0}s"),
    };

    public static string Percent(double? fraction) =>
        fraction is { } f ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(f * 1000) / 10:0.0}%") : "n/a";

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Binary units (1 GB = 1024³ bytes), as SQL Server reports sizes.</summary>
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.0} TB"),
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
    };

    public static string Plural(long count, string noun, string? plural = null) =>
        count == 1 ? $"1 {noun}" : $"{Count(count)} {plural ?? noun + "s"}";
}
