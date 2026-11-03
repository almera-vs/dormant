using System.Globalization;
using Dormant.Core.Configuration;

namespace Dormant.Core.Usage;

public enum UsageClass
{
    Hot,
    Warm,
    Cold,
    Dormant,

    /// <summary>Would be Cold or Dormant, but too little of the window was observed to say so.</summary>
    InsufficientHistory,
}

/// <summary>
/// What is known about one table, index or partition, condensed over UTC days. Only user reads outside maintenance
/// windows count as reads; system activity never does.
/// </summary>
/// <param name="ReadDaysInHotWindow">Days within the last <c>hot_days</c> days with at least one user read.</param>
/// <param name="LastReadDay">The last UTC day (days since 1970-01-01) with a user read, from any evidence; null if none known.</param>
/// <param name="ObservedInWarmWindow">Time observed, while the object existed, within the last <c>warm_days</c> days.</param>
/// <param name="ObservedInDormantWindow">Time observed, while the object existed, within the last <c>dormant_days</c> days.</param>
/// <param name="UpdatesInWarmWindow">User updates (statements) within the last <c>warm_days</c> days, outside maintenance.</param>
/// <param name="MaintenanceReadsInDormantWindow">User reads inside maintenance windows within the last <c>dormant_days</c> days.</param>
/// <param name="LastWriteDay">The last UTC day with a user write outside maintenance windows, from any evidence; shown, never classified on.</param>
public sealed record UsageEvidence(
    int ReadDaysInHotWindow,
    long? LastReadDay,
    TimeSpan ObservedInWarmWindow,
    TimeSpan ObservedInDormantWindow,
    long UpdatesInWarmWindow,
    long MaintenanceReadsInDormantWindow,
    long? LastWriteDay = null);

/// <param name="WarmCoverage">Share of the last <c>warm_days</c> days observed while the object existed.</param>
/// <param name="DormantCoverage">Share of the last <c>dormant_days</c> days observed while the object existed.</param>
/// <param name="WriteOnly">Written to but not read within the last <c>warm_days</c> days, with enough coverage to say so.</param>
/// <param name="Explanation">Why, in one sentence, with the coverage that supports it.</param>
/// <param name="LastWriteDay">The last UTC day with a user write outside maintenance windows, when known.</param>
public sealed record Classification(
    UsageClass Class,
    double WarmCoverage,
    double DormantCoverage,
    long? LastReadDay,
    bool WriteOnly,
    string Explanation,
    long? LastWriteDay = null)
{
    /// <summary>The coverage of the window that decided the class.</summary>
    public double DecidingCoverage => Class is UsageClass.Cold ? WarmCoverage : DormantCoverage;
}

/// <summary>
/// Hot, Warm, Cold, Dormant or Insufficient history, from usage evidence (decision D-048). A false "Dormant" is the worst
/// possible error, so every negative conclusion requires the configured coverage of its window, and anything short of
/// that is Insufficient history with the reason stated.
/// </summary>
public sealed class Classifier
{
    private readonly ClassificationOptions _options;

    public Classifier(ClassificationOptions options)
    {
        _options = options;
    }

    /// <param name="today">The current UTC day (days since 1970-01-01).</param>
    /// <param name="elapsedToday">How much of today has passed; windows end now, not at midnight.</param>
    public Classification Classify(UsageEvidence evidence, long today, TimeSpan elapsedToday)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var warmCoverage = Coverage(evidence.ObservedInWarmWindow, _options.WarmDays, elapsedToday);
        var dormantCoverage = Coverage(evidence.ObservedInDormantWindow, _options.DormantDays, elapsedToday);
        var last = evidence.LastReadDay;
        var writeOnly = (last is null || last <= today - _options.WarmDays)
            && evidence.UpdatesInWarmWindow > 0
            && warmCoverage >= _options.MinCoverage;

        Classification Result(UsageClass usageClass, string explanation) =>
            new(usageClass, warmCoverage, dormantCoverage, last, writeOnly, explanation, evidence.LastWriteDay);

        // "Most days": more than half of them.
        if (evidence.ReadDaysInHotWindow * 2 > _options.HotDays)
        {
            return Result(UsageClass.Hot, Text($"read on {evidence.ReadDaysInHotWindow} of the last {_options.HotDays} days"));
        }

        if (last is { } readDay && readDay > today - _options.WarmDays)
        {
            return Result(UsageClass.Warm, $"last read {Date(readDay)} ({Ago(today - readDay)})");
        }

        var maintenanceNote = evidence.MaintenanceReadsInDormantWindow > 0
            ? "; reads inside maintenance windows are not counted as use"
            : string.Empty;

        if (last is { } coldDay && coldDay > today - _options.DormantDays)
        {
            // Read within the dormant window, not within the warm window: Cold, if the warm window was watched.
            return warmCoverage >= _options.MinCoverage
                ? Result(UsageClass.Cold, Text($"no reads in the last {_options.WarmDays} days ({Percent(warmCoverage)} observed); last read {Date(coldDay)}{maintenanceNote}"))
                : Result(UsageClass.InsufficientHistory, Text($"no reads seen in the last {_options.WarmDays} days, but only {Percent(warmCoverage)} of them were observed (Cold needs {Percent(_options.MinCoverage)}); last read {Date(coldDay)}"));
        }

        if (dormantCoverage >= _options.MinCoverage)
        {
            var lastNote = last is { } oldDay ? $"; last read {Date(oldDay)}" : string.Empty;
            return Result(UsageClass.Dormant, Text($"no reads in the last {_options.DormantDays} days ({Percent(dormantCoverage)} observed){lastNote}{maintenanceNote}"));
        }

        return Result(
            UsageClass.InsufficientHistory,
            Text($"no reads seen, but only {Percent(dormantCoverage)} of the last {_options.DormantDays} days were observed (Dormant needs {Percent(_options.MinCoverage)}){maintenanceNote}"));
    }

    /// <summary>Observed share of a window of <paramref name="days"/> days that ends now.</summary>
    public static double Coverage(TimeSpan observed, int days, TimeSpan elapsedToday)
    {
        var window = TimeSpan.FromDays(days - 1) + elapsedToday;
        return window <= TimeSpan.Zero ? 0 : Math.Clamp(observed / window, 0, 1);
    }

    public static string Date(long day) =>
        DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(day)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Floored, so 89.96% never reads as 90.0%.
    public static string Percent(double fraction) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(fraction * 1000) / 10:0.0}%");

    private static string Ago(long days) => days switch
    {
        0 => "today",
        1 => "yesterday",
        _ => string.Create(CultureInfo.InvariantCulture, $"{days} days ago"),
    };

    private static string Text(FormattableString text) => FormattableString.Invariant(text);
}
