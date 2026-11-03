using Dormant.Core.Configuration;

namespace Dormant.Cli.Running;

/// <summary>When <c>run</c> collects next (decision D-046).</summary>
public static class CollectionSchedule
{
    /// <summary>
    /// The next collection time after <paramref name="nowUtc"/>: the next multiple of the interval on the UTC clock
    /// (15m gives :00, :15, :30, :45, so intervals line up across restarts), or the next moment a maintenance window opens
    /// or closes, whichever comes first, so that maintenance activity falls into whole intervals.
    /// </summary>
    public static DateTimeOffset Next(DateTimeOffset nowUtc, TimeSpan interval, IReadOnlyList<MaintenanceWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var ticks = nowUtc.UtcTicks;
        var next = new DateTimeOffset(ticks - (ticks % interval.Ticks) + interval.Ticks, TimeSpan.Zero);
        foreach (var window in windows)
        {
            if (window.NextChangeAfter(nowUtc) is { } change && change < next)
            {
                next = change;
            }
        }

        return next;
    }
}
