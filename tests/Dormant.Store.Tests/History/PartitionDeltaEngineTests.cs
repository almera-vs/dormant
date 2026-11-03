using Dormant.Core.Collection;
using Dormant.Store.History;

namespace Dormant.Store.Tests.History;

public sealed class PartitionDeltaEngineTests
{
    private static OperationalCounters Ops(long scans = 0, long lookups = 0, long inserts = 0) => new(scans, lookups, inserts, 0, 0);

    private static Dictionary<long, PartitionReading> Readings(params (long Key, long PartitionId, OperationalCounters? Ops)[] items) =>
        items.ToDictionary(i => i.Key, i => new PartitionReading(i.PartitionId, i.Ops));

    [Fact]
    public void Same_rowset_with_counters_at_both_readings_gives_exact_deltas()
    {
        var result = PartitionDeltaEngine.Compare(
            IntervalKind.Continuous,
            Readings((1, 501, Ops(scans: 10)), (2, 502, Ops(lookups: 4))),
            Readings((1, 501, Ops(scans: 13)), (2, 502, Ops(lookups: 4))));

        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.PartitionKey.ShouldBe(1);
        delta.RangeScans.ShouldBe(3);
        delta.LowerBound.ShouldBeFalse();
        result.Unknown.ShouldBeEmpty();
    }

    [Fact]
    public void Scenario_8_a_partition_missing_from_operational_stats_is_unknown_not_zero()
    {
        var result = PartitionDeltaEngine.Compare(
            IntervalKind.Continuous,
            Readings((1, 501, Ops(scans: 10))),
            Readings((1, 501, null)));

        result.Deltas.ShouldBeEmpty();
        result.Unknown.ShouldBe([1L]);
    }

    [Fact]
    public void Counters_that_appear_after_being_absent_are_a_lower_bound()
    {
        // The metadata was loaded at some point inside the interval; counting started then.
        var result = PartitionDeltaEngine.Compare(IntervalKind.Continuous, Readings((1, 501, null)), Readings((1, 501, Ops(scans: 2))));

        result.Deltas.ShouldHaveSingleItem().LowerBound.ShouldBeTrue();
        result.Unknown.ShouldBe([1L]);
    }

    [Fact]
    public void Counters_that_go_backwards_were_reloaded_and_are_a_lower_bound()
    {
        var result = PartitionDeltaEngine.Compare(IntervalKind.Continuous, Readings((1, 501, Ops(scans: 50))), Readings((1, 501, Ops(scans: 5))));

        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.RangeScans.ShouldBe(5);
        delta.LowerBound.ShouldBeTrue();
        result.Unknown.ShouldBe([1L]);
    }

    [Fact]
    public void A_new_rowset_for_the_same_range_starts_counting_again()
    {
        var result = PartitionDeltaEngine.Compare(IntervalKind.Continuous, Readings((1, 501, Ops(scans: 50))), Readings((1, 777, Ops(scans: 60))));

        result.Deltas.ShouldHaveSingleItem().RangeScans.ShouldBe(60);
        result.Unknown.ShouldBe([1L]);
    }

    [Fact]
    public void A_partition_new_in_this_reading_is_unknown_for_the_interval()
    {
        var result = PartitionDeltaEngine.Compare(IntervalKind.Continuous, Readings(), Readings((9, 900, Ops())));

        result.Deltas.ShouldBeEmpty();
        result.Unknown.ShouldBe([9L]);
    }

    [Theory]
    [InlineData(IntervalKind.InstanceRestart)]
    [InlineData(IntervalKind.Reset)]
    public void After_a_restart_or_reset_no_partition_counter_is_comparable(IntervalKind kind)
    {
        var result = PartitionDeltaEngine.Compare(kind, Readings((1, 501, Ops(scans: 1))), Readings((1, 501, Ops(scans: 3))));

        var delta = result.Deltas.ShouldHaveSingleItem();
        delta.RangeScans.ShouldBe(3);
        delta.LowerBound.ShouldBeTrue();
        result.Unknown.ShouldBe([1L]);
    }

    [Fact]
    public void Exact_counts_survive_a_gap_but_the_gap_itself_is_unobserved()
    {
        var result = PartitionDeltaEngine.Compare(IntervalKind.Gap, Readings((1, 501, Ops(scans: 1))), Readings((1, 501, Ops(scans: 3))));

        result.Deltas.ShouldHaveSingleItem().LowerBound.ShouldBeFalse();
        result.Unknown.ShouldBeEmpty();
    }

    [Fact]
    public void A_partition_that_disappeared_is_simply_gone()
    {
        var result = PartitionDeltaEngine.Compare(IntervalKind.Continuous, Readings((1, 501, Ops(scans: 1))), Readings());

        result.Deltas.ShouldBeEmpty();
        result.Unknown.ShouldBeEmpty();
    }
}
