using Dormant.Core.Collection;
using Dormant.Store.History;

namespace Dormant.Store.Tests.History;

public sealed class DeltaEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxSpan = TimeSpan.FromMinutes(45);
    private static readonly EngineIncarnation Engine = new(StartMsTicks: 1_000, MsTicks: 5_000_000, StartTimeUtc: T0.AddDays(-10));

    private static UsageCounters Usage(long seeks = 0, long scans = 0, long lookups = 0, long updates = 0, long systemScans = 0, DateTimeOffset? lastRead = null) =>
        new(seeks, scans, lookups, updates, 0, systemScans, 0, 0, lastRead, null);

    private static DatabaseBaseline Baseline(Dictionary<long, UsageCounters?> indexes, int databaseId = 5, EngineIncarnation? engine = null, bool interrupted = false) =>
        new(T0, databaseId, engine ?? Engine, interrupted, interrupted ? "database was OFFLINE" : null, indexes);

    private static DatabaseReading Reading(Dictionary<long, UsageCounters?> indexes, TimeSpan? after = null, int databaseId = 5, EngineIncarnation? engine = null) =>
        new(T0 + (after ?? TimeSpan.FromMinutes(15)), databaseId, engine ?? Engine with { MsTicks = Engine.MsTicks + 900_000 }, indexes);

    [Fact]
    public void Continuous_interval_reports_differences_for_active_indexes_only()
    {
        var before = Baseline(new() { [1] = Usage(seeks: 10, updates: 3), [2] = Usage(scans: 4), [3] = null });
        var now = Reading(new() { [1] = Usage(seeks: 15, updates: 3), [2] = Usage(scans: 4), [3] = null });

        var result = DeltaEngine.Compare(before, now, MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Continuous);
        result.ObservedFromUtc.ShouldBe(T0);
        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.IndexKey.ShouldBe(1);
        delta.UserSeeks.ShouldBe(5);
        delta.UserUpdates.ShouldBe(0);
        delta.LowerBound.ShouldBeFalse();
    }

    [Fact]
    public void First_use_since_the_counters_started_counts_in_full()
    {
        // No row means "not used since the counters were initialised", so everything in the new row is new.
        var result = DeltaEngine.Compare(Baseline(new() { [1] = null }), Reading(new() { [1] = Usage(seeks: 2) }), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Continuous);
        result.Deltas.ShouldHaveSingleItem().UserSeeks.ShouldBe(2);
    }

    [Fact]
    public void Scenario_3_a_counter_that_goes_backwards_resets_the_database_and_is_never_negative()
    {
        var before = Baseline(new() { [1] = Usage(seeks: 100), [2] = Usage(scans: 50) });
        var now = Reading(new() { [1] = Usage(seeks: 7), [2] = Usage(scans: 60) });

        var result = DeltaEngine.Compare(before, now, MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Reset);
        result.ObservedFromUtc.ShouldBeNull();
        result.Reason.ShouldBe("usage counters went backwards (database restarted or closed)");
        result.Deltas.ShouldAllBe(d => d.LowerBound && d.UserReads >= 0);
        result.Deltas.Single(d => d.IndexKey == 1).UserSeeks.ShouldBe(7);
        // Index 2 cannot be trusted either: the reset may have wiped it too; its counts are a lower bound.
        result.Deltas.Single(d => d.IndexKey == 2).UserScans.ShouldBe(60);
    }

    [Fact]
    public void A_usage_row_that_vanishes_while_its_index_exists_resets_the_database()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 4), [2] = null }), Reading(new() { [1] = null, [2] = null }), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Reset);
        result.ObservedFromUtc.ShouldBeNull();
        result.Deltas.ShouldBeEmpty();
    }

    [Fact]
    public void A_dropped_index_is_not_a_reset()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 4), [2] = Usage(scans: 1) }), Reading(new() { [2] = Usage(scans: 1) }), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Continuous);
        result.Deltas.ShouldBeEmpty();
    }

    [Fact]
    public void Scenario_1_engine_restart_inside_the_interval_is_stitched_from_the_restart()
    {
        var restartedAt = T0.AddMinutes(5);
        var restarted = new EngineIncarnation(StartMsTicks: 9_000_000, MsTicks: 9_600_000, StartTimeUtc: restartedAt);
        var before = Baseline(new() { [1] = Usage(seeks: 1_000) });
        var now = Reading(new() { [1] = Usage(seeks: 3) }, engine: restarted);

        var result = DeltaEngine.Compare(before, now, MaxSpan);

        result.Kind.ShouldBe(IntervalKind.InstanceRestart);
        result.ResetAtUtc.ShouldBe(restartedAt);
        result.ObservedFromUtc.ShouldBe(restartedAt);
        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.UserSeeks.ShouldBe(3);
        delta.LowerBound.ShouldBeFalse();
    }

    [Fact]
    public void A_host_reboot_is_a_restart_even_if_the_start_tick_happens_to_match()
    {
        var rebooted = new EngineIncarnation(StartMsTicks: Engine.StartMsTicks, MsTicks: 2_000, StartTimeUtc: T0.AddMinutes(10));

        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 9) }), Reading(new() { [1] = Usage(seeks: 1) }, engine: rebooted), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.InstanceRestart);
    }

    [Fact]
    public void Moving_to_an_engine_that_started_earlier_is_a_reset_at_an_unknown_time()
    {
        var otherNode = new EngineIncarnation(StartMsTicks: 77, MsTicks: 99_999_999, StartTimeUtc: T0.AddDays(-30));

        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 9) }), Reading(new() { [1] = Usage(seeks: 1) }, engine: otherNode), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Reset);
        result.ObservedFromUtc.ShouldBeNull();
        result.Deltas.ShouldHaveSingleItem().LowerBound.ShouldBeTrue();
    }

    [Fact]
    public void An_interruption_outranks_a_restart_because_the_database_may_have_returned_later()
    {
        var restarted = new EngineIncarnation(StartMsTicks: 9_000_000, MsTicks: 9_600_000, StartTimeUtc: T0.AddMinutes(5));

        var result = DeltaEngine.Compare(Baseline(new() { [1] = null }, interrupted: true), Reading(new() { [1] = Usage(seeks: 1) }, engine: restarted), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Reset);
        result.Reason.ShouldBe("database was OFFLINE");
        result.ObservedFromUtc.ShouldBeNull();
    }

    [Fact]
    public void Scenario_7_a_new_database_id_means_counters_restarted_with_the_attach()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 9) }, databaseId: 5), Reading(new() { [1] = Usage(seeks: 9) }, databaseId: 8), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Reset);
        result.Reason.ShouldBe("database_id changed from 5 to 8 (detached and attached, or restored)");
        // Equal counters after a reset are new activity, not "no change".
        result.Deltas.ShouldHaveSingleItem().UserSeeks.ShouldBe(9);
    }

    [Fact]
    public void Scenario_2_agent_downtime_is_a_gap_never_inactivity()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = null }), Reading(new() { [1] = null }, after: TimeSpan.FromHours(6)), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Gap);
        result.ObservedFromUtc.ShouldBeNull();
        result.Reason.ShouldBe("no reading for 6h 0m");
    }

    [Fact]
    public void Reads_inside_a_gap_are_still_recorded_exactly()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage(seeks: 1) }), Reading(new() { [1] = Usage(seeks: 4) }, after: TimeSpan.FromHours(6)), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Gap);
        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.UserSeeks.ShouldBe(3);
        delta.LowerBound.ShouldBeFalse();
    }

    [Fact]
    public void A_restart_long_before_the_reading_is_not_counted_as_observed()
    {
        var restarted = new EngineIncarnation(StartMsTicks: 9_000_000, MsTicks: 99_000_000, StartTimeUtc: T0.AddMinutes(10));

        var result = DeltaEngine.Compare(Baseline(new() { [1] = null }), Reading(new() { [1] = null }, after: TimeSpan.FromHours(5), engine: restarted), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.InstanceRestart);
        result.ObservedFromUtc.ShouldBeNull();
    }

    [Fact]
    public void A_clock_that_moves_backwards_is_not_observed_time()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = null }), Reading(new() { [1] = null }, after: TimeSpan.FromMinutes(-3)), MaxSpan);

        result.Kind.ShouldBe(IntervalKind.Gap);
        result.ObservedFromUtc.ShouldBeNull();
    }

    [Fact]
    public void System_activity_is_kept_apart_from_user_reads()
    {
        var result = DeltaEngine.Compare(Baseline(new() { [1] = Usage() }), Reading(new() { [1] = Usage(systemScans: 5) }), MaxSpan);

        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.SystemScans.ShouldBe(5);
        delta.UserReads.ShouldBe(0);
        delta.LastUserReadUtc.ShouldBeNull();
    }

    [Fact]
    public void Keeps_the_engines_last_read_time_only_when_it_falls_inside_the_interval()
    {
        var inside = T0.AddMinutes(7);
        var result = DeltaEngine.Compare(
            Baseline(new() { [1] = Usage(seeks: 1, lastRead: T0.AddMinutes(-60)) }),
            Reading(new() { [1] = Usage(seeks: 2, lastRead: inside) }),
            MaxSpan);

        result.Deltas.ShouldHaveSingleItem().LastUserReadUtc.ShouldBe(inside);
    }
}
