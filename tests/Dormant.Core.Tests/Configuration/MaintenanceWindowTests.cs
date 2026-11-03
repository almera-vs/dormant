using Dormant.Core.Configuration;

namespace Dormant.Core.Tests.Configuration;

public sealed class MaintenanceWindowTests
{
    private static MaintenanceWindow Parse(string text)
    {
        MaintenanceWindow.TryParse(text, out var window, out var error).ShouldBeTrue(error);
        return window!;
    }

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso + "Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Same_day_window_includes_start_and_excludes_end()
    {
        var window = Parse("Sun 01:00-05:00");

        // 2026-11-01 is a Sunday.
        window.Contains(Utc("2026-11-01T00:59:59")).ShouldBeFalse();
        window.Contains(Utc("2026-11-01T01:00:00")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T04:59:59")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T05:00:00")).ShouldBeFalse();
        window.Contains(Utc("2026-11-02T02:00:00")).ShouldBeFalse();
    }

    [Fact]
    public void Window_ending_before_it_starts_runs_past_midnight()
    {
        var window = Parse("Sat 22:00-02:00");

        window.Contains(Utc("2026-10-31T21:59:00")).ShouldBeFalse();
        window.Contains(Utc("2026-10-31T23:30:00")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T01:59:00")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T02:00:00")).ShouldBeFalse();
        // Friday night into Saturday is not part of a Saturday window.
        window.Contains(Utc("2026-10-31T01:00:00")).ShouldBeFalse();
    }

    [Fact]
    public void Daily_window_applies_every_day()
    {
        var window = Parse("Daily 23:00-24:00");

        for (var day = 1; day <= 7; day++)
        {
            window.Contains(new DateTimeOffset(2026, 11, day, 23, 30, 0, TimeSpan.Zero)).ShouldBeTrue();
            window.Contains(new DateTimeOffset(2026, 11, day, 22, 30, 0, TimeSpan.Zero)).ShouldBeFalse();
        }
    }

    [Fact]
    public void Uses_wall_clock_time_in_its_time_zone_across_daylight_saving()
    {
        var window = Parse("Sun 01:00-05:00 Europe/Warsaw");

        // Winter (UTC+1): 01:00 Warsaw is 00:00 UTC.
        window.Contains(Utc("2026-11-01T00:00:00")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T03:59:00")).ShouldBeTrue();
        window.Contains(Utc("2026-11-01T04:00:00")).ShouldBeFalse();

        // Summer (UTC+2): 01:00 Warsaw is 23:00 UTC on Saturday.
        window.Contains(Utc("2026-07-04T23:00:00")).ShouldBeTrue();
        window.Contains(Utc("2026-07-05T02:59:00")).ShouldBeTrue();
        window.Contains(Utc("2026-07-05T03:00:00")).ShouldBeFalse();

        // Spring-forward Sunday (2026-03-29, 02:00 -> 03:00 local): the window is one hour shorter in UTC.
        window.Contains(Utc("2026-03-29T00:00:00")).ShouldBeTrue();  // 01:00 CET
        window.Contains(Utc("2026-03-29T02:59:00")).ShouldBeTrue();  // 04:59 CEST
        window.Contains(Utc("2026-03-29T03:00:00")).ShouldBeFalse(); // 05:00 CEST
    }

    [Fact]
    public void Covers_only_intervals_entirely_inside_one_occurrence()
    {
        var window = Parse("Sun 01:00-05:00");

        window.Covers(Utc("2026-11-01T01:00:00"), Utc("2026-11-01T01:15:00")).ShouldBeTrue();
        window.Covers(Utc("2026-11-01T04:45:00"), Utc("2026-11-01T05:00:00")).ShouldBeTrue();
        // Partial overlaps count as use (decision D-044).
        window.Covers(Utc("2026-11-01T00:50:00"), Utc("2026-11-01T01:05:00")).ShouldBeFalse();
        window.Covers(Utc("2026-11-01T04:55:00"), Utc("2026-11-01T05:10:00")).ShouldBeFalse();
        // Longer than the window: cannot be inside it.
        window.Covers(Utc("2026-11-01T01:00:00"), Utc("2026-11-08T02:00:00")).ShouldBeFalse();
        window.Covers(Utc("2026-11-01T02:00:00"), Utc("2026-11-01T02:00:00")).ShouldBeFalse();
    }

    [Fact]
    public void Covers_respects_windows_that_run_past_midnight()
    {
        var window = Parse("Sat 22:00-02:00");

        window.Covers(Utc("2026-10-31T23:50:00"), Utc("2026-11-01T00:10:00")).ShouldBeTrue();
        window.Covers(Utc("2026-11-01T01:50:00"), Utc("2026-11-01T02:05:00")).ShouldBeFalse();
    }

    [Fact]
    public void Next_change_is_the_next_opening_or_closing_minute()
    {
        var window = Parse("Sun 01:00-05:00 Europe/Warsaw");

        // Saturday noon UTC: next opening is Sunday 01:00 Warsaw = 00:00 UTC (winter).
        window.NextChangeAfter(Utc("2026-10-31T12:00:00")).ShouldBe(Utc("2026-11-01T00:00:00"));
        // Inside the window: the next change is its closing.
        window.NextChangeAfter(Utc("2026-11-01T02:30:00")).ShouldBe(Utc("2026-11-01T04:00:00"));
        // Exactly at the opening, the next change is the closing.
        window.NextChangeAfter(Utc("2026-11-01T00:00:00")).ShouldBe(Utc("2026-11-01T04:00:00"));
    }

    [Theory]
    [InlineData("Sun 01:00-05:00", 240)]
    [InlineData("Sat 22:00-02:00", 240)]
    [InlineData("Daily 23:00-24:00", 60)]
    public void Length_is_the_wall_clock_duration(string text, int minutes) =>
        Parse(text).Length.ShouldBe(TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData("mon 00:00-01:00", DayOfWeek.Monday)]
    [InlineData("SUN 01:00-05:00 UTC", DayOfWeek.Sunday)]
    public void Parses_days_ignoring_case(string text, DayOfWeek day) =>
        Parse(text).Day.ShouldBe(day);

    [Theory]
    [InlineData("")]
    [InlineData("Sun")]
    [InlineData("Sunday 01:00-05:00")]
    [InlineData("Sun 1:00-5:00")]
    [InlineData("Sun 01:00-05:60")]
    [InlineData("Sun 24:00-01:00")]
    [InlineData("Sun 03:00-03:00")]
    [InlineData("Sun 00:00-24:00")]
    [InlineData("Sun 01:00-05:00 Nowhere/Special")]
    [InlineData("Sun 01:00-05:00 UTC extra")]
    public void Rejects_malformed_windows(string text)
    {
        MaintenanceWindow.TryParse(text, out var window, out var error).ShouldBeFalse();
        window.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}
