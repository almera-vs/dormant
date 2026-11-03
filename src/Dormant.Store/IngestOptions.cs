using Dormant.Core.Configuration;

namespace Dormant.Store;

/// <summary>How one instance's collections are turned into history.</summary>
/// <param name="MaxObservedSpan">Intervals longer than this are gaps, not observed time (decision D-027).</param>
/// <param name="MaintenanceWindows">Activity in intervals entirely inside one of these is kept apart (decision D-044).</param>
/// <param name="Synthetic">
/// The snapshot was made up, not collected (the demo's backfill). It is recorded as such, and status, reports and exports
/// say so wherever its history shows (decision D-058).
/// </param>
public sealed record IngestOptions(TimeSpan MaxObservedSpan, IReadOnlyList<MaintenanceWindow> MaintenanceWindows, bool Synthetic = false)
{
    public static IngestOptions For(DormantConfig config, InstanceOptions instance)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(instance);
        return new IngestOptions(config.Collection.MaxObservedSpan, instance.MaintenanceWindows);
    }

    /// <summary>True when activity between <paramref name="fromUtc"/> and <paramref name="toUtc"/> happened inside a window.</summary>
    public bool IsMaintenance(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        MaintenanceWindows.Any(w => w.Covers(fromUtc, toUtc));
}
