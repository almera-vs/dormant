using Dormant.Core.Collection;

namespace Dormant.Store.History;

/// <summary>A partition's reading: the rowset it lives in and its operational counters, if cached.</summary>
public sealed record PartitionReading(long PartitionId, OperationalCounters? Operations);

/// <summary>Operational activity of one partition within one interval.</summary>
/// <param name="LowerBound">
/// The counters could not be compared with the previous reading (new rowset, reloaded metadata, reset interval), so these
/// are the counts since they last started: real activity inside the interval, but possibly not all of it.
/// </param>
public sealed record PartitionDelta(
    long PartitionKey,
    long RangeScans,
    long SingletonLookups,
    long LeafInserts,
    long LeafUpdates,
    long LeafDeletes,
    bool LowerBound)
{
    public long Reads => RangeScans + SingletonLookups;
}

/// <param name="Deltas">Partitions with activity.</param>
/// <param name="Unknown">
/// Partitions whose activity in the interval is not fully known: no "no activity" conclusion may be drawn for them.
/// Includes every lower-bound partition.
/// </param>
public sealed record PartitionIntervalResult(IReadOnlyList<PartitionDelta> Deltas, IReadOnlyList<long> Unknown);

/// <summary>
/// Turns two readings of sys.dm_db_index_operational_stats into per-partition activity. Learn: the counters start at
/// zero whenever a partition's metadata enters the cache and vanish when it leaves, and "can't conclusively determine
/// whether an index was used". So an exact delta needs the same rowset, a row at both readings and no counter going
/// backwards; anything else is unknown (decision D-038).
/// </summary>
public static class PartitionDeltaEngine
{
    public static PartitionIntervalResult Compare(
        IntervalKind kind,
        IReadOnlyDictionary<long, PartitionReading> previous,
        IReadOnlyDictionary<long, PartitionReading> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        // After a restart or a reset the metadata cache started again too.
        var comparable = kind is IntervalKind.Continuous or IntervalKind.Gap;
        var deltas = new List<PartitionDelta>();
        var unknown = new List<long>();

        foreach (var (key, now) in current)
        {
            if (now.Operations is not { } counters)
            {
                unknown.Add(key);
                continue;
            }

            if (comparable
                && previous.TryGetValue(key, out var before)
                && before.PartitionId == now.PartitionId
                && before.Operations is { } earlier
                && !counters.DecreasedSince(earlier))
            {
                AddIfActive(deltas, key, counters, earlier, lowerBound: false);
                continue;
            }

            unknown.Add(key);
            AddIfActive(deltas, key, counters, earlier: null, lowerBound: true);
        }

        return new PartitionIntervalResult(deltas, unknown);
    }

    private static void AddIfActive(List<PartitionDelta> deltas, long key, OperationalCounters now, OperationalCounters? earlier, bool lowerBound)
    {
        var delta = new PartitionDelta(
            key,
            now.RangeScans - (earlier?.RangeScans ?? 0),
            now.SingletonLookups - (earlier?.SingletonLookups ?? 0),
            now.LeafInserts - (earlier?.LeafInserts ?? 0),
            now.LeafUpdates - (earlier?.LeafUpdates ?? 0),
            now.LeafDeletes - (earlier?.LeafDeletes ?? 0),
            lowerBound);

        if (delta.RangeScans != 0 || delta.SingletonLookups != 0 || delta.LeafInserts != 0 || delta.LeafUpdates != 0 || delta.LeafDeletes != 0)
        {
            deltas.Add(delta);
        }
    }
}
