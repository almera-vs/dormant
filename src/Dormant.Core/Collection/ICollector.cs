namespace Dormant.Core.Collection;

/// <summary>Reads usage and size statistics from one monitored instance. Implementations are read-only.</summary>
public interface ICollector
{
    /// <summary>Version, edition, start time and identity of the instance.</summary>
    Task<InstanceInfo> DescribeAsync(CancellationToken ct);

    /// <summary>Usage counters for every index in every selected database.</summary>
    Task<Snapshot> CollectAsync(CancellationToken ct);
}
