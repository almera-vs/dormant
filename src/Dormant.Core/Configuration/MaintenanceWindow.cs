using System.Globalization;

namespace Dormant.Core.Configuration;

/// <summary>
/// A recurring weekly (or daily) window in a given time zone, written as
/// <c>Sun 01:00-05:00 Europe/Warsaw</c> or <c>Daily 22:00-02:00 UTC</c>. When the end is earlier than the start,
/// the window runs past midnight into the next day. Times are wall-clock times in the window's time zone, so
/// a window keeps its local hours across daylight-saving changes.
/// </summary>
public sealed record MaintenanceWindow
{
    private const int MinutesPerDay = 24 * 60;

    private MaintenanceWindow(DayOfWeek? day, int startMinute, int endMinute, TimeZoneInfo timeZone, string text)
    {
        Day = day;
        StartMinute = startMinute;
        EndMinute = endMinute;
        TimeZone = timeZone;
        Text = text;
    }

    /// <summary>The day the window starts on, or null for every day.</summary>
    public DayOfWeek? Day { get; }

    /// <summary>Minutes after local midnight, 0..1439.</summary>
    public int StartMinute { get; }

    /// <summary>Minutes after local midnight, 1..1440. Less than or equal to <see cref="StartMinute"/> means the next day.</summary>
    public int EndMinute { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>How long one occurrence lasts, by the wall clock.</summary>
    public TimeSpan Length => TimeSpan.FromMinutes(EndMinute > StartMinute ? EndMinute - StartMinute : EndMinute + MinutesPerDay - StartMinute);

    /// <summary>The text as written in the configuration.</summary>
    public string Text { get; }

    public bool Contains(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        var minute = (local.Hour * 60) + local.Minute;
        var day = local.DayOfWeek;

        if (EndMinute > StartMinute)
        {
            return StartsOn(day) && minute >= StartMinute && minute < EndMinute;
        }

        var previousDay = (DayOfWeek)(((int)day + 6) % 7);
        return (StartsOn(day) && minute >= StartMinute) || (StartsOn(previousDay) && minute < EndMinute);
    }

    /// <summary>
    /// True when every minute of [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) lies inside the window. An interval
    /// that only overlaps a window is not covered: its activity counts as use (decision D-044).
    /// </summary>
    public bool Covers(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc || toUtc - fromUtc > Length)
        {
            return false;
        }

        for (var t = fromUtc; t < toUtc; t = t.AddMinutes(1))
        {
            if (!Contains(t))
            {
                return false;
            }
        }

        return Contains(toUtc.AddTicks(-1));
    }

    /// <summary>
    /// The first minute after <paramref name="nowUtc"/> at which the window opens or closes; null if that is more than
    /// eight days away (it never is for a valid window). Collections are scheduled at these moments so that maintenance
    /// activity falls into whole intervals.
    /// </summary>
    public DateTimeOffset? NextChangeAfter(DateTimeOffset nowUtc)
    {
        var inside = Contains(nowUtc);
        var t = new DateTimeOffset(nowUtc.Ticks - (nowUtc.Ticks % TimeSpan.TicksPerMinute), TimeSpan.Zero).AddMinutes(1);
        var limit = nowUtc.AddDays(8);
        for (; t <= limit; t = t.AddMinutes(1))
        {
            if (Contains(t) != inside)
            {
                return t;
            }
        }

        return null;
    }

    private bool StartsOn(DayOfWeek day) => Day is null || Day == day;

    public static bool TryParse(string? text, out MaintenanceWindow? window, out string? error)
    {
        window = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "expected '<Day> HH:MM-HH:MM [TimeZone]', e.g. 'Sun 01:00-05:00 Europe/Warsaw'";
            return false;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            error = "expected '<Day> HH:MM-HH:MM [TimeZone]', e.g. 'Sun 01:00-05:00 Europe/Warsaw'";
            return false;
        }

        if (!TryParseDay(parts[0], out var day))
        {
            error = $"'{parts[0]}' is not a day; use Mon, Tue, Wed, Thu, Fri, Sat, Sun or Daily";
            return false;
        }

        var range = parts[1].Split('-');
        if (range.Length != 2 || !TryParseTime(range[0], allow24: false, out var start) || !TryParseTime(range[1], allow24: true, out var end))
        {
            error = $"'{parts[1]}' is not a time range; use HH:MM-HH:MM in 24-hour time, e.g. 01:00-05:00";
            return false;
        }

        if (start == end % MinutesPerDay)
        {
            error = "the window starts and ends at the same time; a window must be shorter than 24 hours and not empty";
            return false;
        }

        var timeZone = TimeZoneInfo.Utc;
        if (parts.Length == 3 && !TryFindTimeZone(parts[2], out timeZone))
        {
            error = $"'{parts[2]}' is not a known time zone; use an IANA name such as Europe/Warsaw, or UTC";
            return false;
        }

        window = new MaintenanceWindow(day, start, end, timeZone, string.Join(' ', parts));
        return true;
    }

    private static bool TryParseDay(string text, out DayOfWeek? day)
    {
        day = text.ToUpperInvariant() switch
        {
            "MON" => DayOfWeek.Monday,
            "TUE" => DayOfWeek.Tuesday,
            "WED" => DayOfWeek.Wednesday,
            "THU" => DayOfWeek.Thursday,
            "FRI" => DayOfWeek.Friday,
            "SAT" => DayOfWeek.Saturday,
            "SUN" => DayOfWeek.Sunday,
            _ => null,
        };
        return day is not null || string.Equals(text, "Daily", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTime(string text, bool allow24, out int minuteOfDay)
    {
        minuteOfDay = 0;
        if (text.Length != 5 || text[2] != ':')
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || minutes > 59)
        {
            return false;
        }

        if (hours == 24 && minutes == 0 && allow24)
        {
            minuteOfDay = MinutesPerDay;
            return true;
        }

        if (hours > 23)
        {
            return false;
        }

        minuteOfDay = (hours * 60) + minutes;
        return true;
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo timeZone)
    {
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            timeZone = TimeZoneInfo.Utc;
            return true;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out timeZone!);
    }
}
