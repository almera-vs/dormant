using Dormant.Core.Collection;
using Dormant.Store.History;

namespace Dormant.Store.Ingestion;

/// <summary>What one collection added to the history, per database.</summary>
public sealed record IngestResult(IReadOnlyList<DatabaseIngest> Databases);

/// <param name="Interval">
/// The interval this reading closed; null for a database's first reading (history starts there) and for databases
/// that were not collected.
/// </param>
/// <param name="Detail">For a skipped or failed database, why.</param>
public sealed record DatabaseIngest(
    string Name,
    DatabaseOutcome Outcome,
    int IndexCount,
    IntervalResult? Interval,
    string? Detail)
{
    public int PartitionCount { get; init; }

    public long ReservedBytes { get; init; }

    /// <summary>Operational activity per partition in <see cref="Interval"/>; null when there is no interval.</summary>
    public PartitionIntervalResult? Partitions { get; init; }
}
