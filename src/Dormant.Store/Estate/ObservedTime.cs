using Dormant.Store.History;

namespace Dormant.Store.Estate;

internal static class ObservedTime
{
    /// <summary>
    /// A partition's observed time per day: the database's, except on days when the table was read but the partition's
    /// own counters were unknown, because then it cannot be told whether this partition served the reads. On days the
    /// whole table went unread, the partition certainly went unread too.
    /// </summary>
    /// <param name="observedPerDay">Observed milliseconds per UTC day, starting at <paramref name="firstDay"/>.</param>
    public static long[] Adjust(long[] observedPerDay, long firstDay, IEnumerable<long>? tableReadDays, List<(long From, long To)>? unknown)
    {
        if (tableReadDays is null || unknown is null || unknown.Count == 0)
        {
            return observedPerDay;
        }

        var adjusted = (long[])observedPerDay.Clone();
        foreach (var day in tableReadDays)
        {
            var index = day - firstDay;
            if (index < 0 || index >= adjusted.Length)
            {
                continue;
            }

            var dayStart = day * RollupWriter.MsPerDay;
            var dayEnd = dayStart + RollupWriter.MsPerDay;
            var overlap = unknown.Sum(span => Math.Max(0, Math.Min(span.To, dayEnd) - Math.Max(span.From, dayStart)));
            adjusted[index] = Math.Max(0, adjusted[index] - overlap);
        }

        return adjusted;
    }
}
