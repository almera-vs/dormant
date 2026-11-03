namespace Dormant.Core.History;

/// <summary>What <c>dormant status</c> shows: how complete the history is, per instance and database.</summary>
public sealed record StoreStatus(string Path, long SizeBytes, IReadOnlyList<InstanceStatus> Instances);

/// <param name="Restarts">Engine restarts stitched into the history, newest first.</param>
/// <param name="Resets">Database counter resets at unknown times, newest first.</param>
/// <param name="SyntheticCollections">Readings that were made up, not collected (the demo's backfill); 0 for real history.</param>
/// <param name="LastSyntheticUtc">The latest made-up reading, when there is one.</param>
public sealed record InstanceStatus(
    string Name,
    int Collections,
    CollectionSummary? LastCollection,
    CollectionSummary? LastSuccessfulCollection,
    IReadOnlyList<DatabaseStatus> Databases,
    IReadOnlyList<HistoryEvent> Restarts,
    IReadOnlyList<HistoryEvent> Resets,
    IReadOnlyList<HistoryEvent> Gaps,
    int SyntheticCollections = 0,
    DateTimeOffset? LastSyntheticUtc = null)
{
    public TimeSpan Window => Databases.Aggregate(TimeSpan.Zero, (sum, d) => sum + d.Window);

    public TimeSpan Observed => Databases.Aggregate(TimeSpan.Zero, (sum, d) => sum + d.Observed);

    /// <summary>Share of the history window that was actually observed; null before there is any window.</summary>
    public double? Coverage => Window > TimeSpan.Zero ? Observed / Window : null;

    public DateTimeOffset? HistoryStartUtc => Databases.Count == 0 ? null : Databases.Min(d => d.FirstReadingUtc);
}

public sealed record CollectionSummary(
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    string Outcome,
    string? Error,
    string? ServerName,
    string? Platform,
    int Databases,
    int Indexes);

/// <param name="FirstReadingUtc">The database's first reading: history starts here.</param>
/// <param name="LastReadingUtc">The most recent reading.</param>
/// <param name="Observed">Time within the window that was actually observed.</param>
/// <param name="LastOutcome">What the most recent collection did with this database: collected, skipped or failed.</param>
public sealed record DatabaseStatus(
    string Name,
    DateTimeOffset FirstReadingUtc,
    DateTimeOffset LastReadingUtc,
    TimeSpan Observed,
    string? LastOutcome,
    string? LastDetail)
{
    public TimeSpan Window => LastReadingUtc - FirstReadingUtc;

    public double? Coverage => Window > TimeSpan.Zero ? Observed / Window : null;
}

/// <param name="AtUtc">When it happened, or for gaps and resets the end of the affected interval.</param>
/// <param name="Duration">For gaps and resets, the length of the unobserved interval.</param>
public sealed record HistoryEvent(DateTimeOffset AtUtc, string Database, TimeSpan Duration, string? Reason);
