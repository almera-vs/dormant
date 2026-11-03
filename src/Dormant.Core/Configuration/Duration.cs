using System.Globalization;

namespace Dormant.Core.Configuration;

/// <summary>
/// Parses durations written as a whole number and a unit: <c>30s</c>, <c>15m</c>, <c>6h</c>, <c>30d</c>, <c>3y</c>
/// (a year is 365 days).
/// </summary>
public static class Duration
{
    // About 100 years; keeps arithmetic elsewhere far from overflow.
    private const long MaxSeconds = 36_500L * 86_400;

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.Length < 2)
        {
            return false;
        }

        var unit = s[^1];
        if (!long.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount > MaxSeconds)
        {
            return false;
        }

        long? seconds = unit switch
        {
            's' => amount,
            'm' => amount * 60,
            'h' => amount * 3600,
            'd' => amount * 86400,
            'y' => amount * 365 * 86400,
            _ => null,
        };

        if (seconds is null || seconds > MaxSeconds)
        {
            return false;
        }

        value = TimeSpan.FromSeconds(seconds.Value);
        return true;
    }

    public static string Format(TimeSpan value)
    {
        if (value > TimeSpan.Zero && value.Ticks % (TimeSpan.TicksPerDay * 365) == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalDays / 365}y");
        }

        if (value.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalDays}d");
        }

        if (value.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalHours}h");
        }

        if (value.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalMinutes}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(long)value.TotalSeconds}s");
    }
}
