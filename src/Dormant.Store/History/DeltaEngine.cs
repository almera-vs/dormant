using Dormant.Core.Collection;

namespace Dormant.Store.History;

public enum IntervalKind
{
    /// <summary>Counters compared cleanly with the previous reading. Observed, unless longer than the maximum span.</summary>
    Continuous,

    /// <summary>Counters compared cleanly, but the interval is too long to count as observed (agent downtime).</summary>
    Gap,

    /// <summary>The engine restarted at a known time inside the interval. Observed from the restart.</summary>
    InstanceRestart,

    /// <summary>Counters were reset at an unknown time inside the interval. Never observed.</summary>
    Reset,
}

/// <summary>What Dormant knew about a database after its previous successful reading.</summary>
/// <param name="ObservedUtc">When it was read, by the server's clock.</param>
/// <param name="DatabaseId">database_id at that time.</param>
/// <param name="Engine">The engine incarnation the counters belonged to.</param>
/// <param name="Interrupted">
/// Set when a later collection found the database in a state that discards counters (offline, AUTO_CLOSE, missing),
/// so the next reading cannot be compared with this one.
/// </param>
/// <param name="InterruptedReason">Why, for the record.</param>
/// <param name="Indexes">Counters per index key; a null value means the index existed but had no usage row.</param>
public sealed record DatabaseBaseline(
    DateTimeOffset ObservedUtc,
    int DatabaseId,
    EngineIncarnation Engine,
    bool Interrupted,
    string? InterruptedReason,
    IReadOnlyDictionary<long, UsageCounters?> Indexes);

/// <summary>A database reading, with indexes already mapped to their stable keys.</summary>
public sealed record DatabaseReading(
    DateTimeOffset ObservedUtc,
    int DatabaseId,
    EngineIncarnation Engine,
    IReadOnlyDictionary<long, UsageCounters?> Indexes);

/// <summary>Usage of one index within one interval.</summary>
/// <param name="LowerBound">
/// The counters were reset at an unknown time inside the interval, so these are the counts since that reset: real
/// activity within the interval, but possibly not all of it.
/// </param>
/// <param name="LastUserReadUtc">The engine's last user read time, when it falls inside the interval.</param>
/// <param name="LastUserUpdateUtc">The engine's last user update time, when it falls inside the interval.</param>
public sealed record IndexDelta(
    long IndexKey,
    long UserSeeks,
    long UserScans,
    long UserLookups,
    long UserUpdates,
    long SystemSeeks,
    long SystemScans,
    long SystemLookups,
    long SystemUpdates,
    bool LowerBound,
    DateTimeOffset? LastUserReadUtc,
    DateTimeOffset? LastUserUpdateUtc)
{
    public long UserReads => UserSeeks + UserScans + UserLookups;
}

/// <summary>The result of comparing two readings of one database.</summary>
/// <param name="ObservedFromUtc">
/// Start of the part of the interval Dormant actually observed; null when none of it was. "No reads" may only be
/// concluded for the observed part.
/// </param>
/// <param name="ResetAtUtc">For <see cref="IntervalKind.InstanceRestart"/>, when the engine started.</param>
/// <param name="Deltas">Only indexes with activity; an absent index had none in the observed part.</param>
public sealed record IntervalResult(
    IntervalKind Kind,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset? ObservedFromUtc,
    DateTimeOffset? ResetAtUtc,
    string? Reason,
    IReadOnlyList<IndexDelta> Deltas);

/// <summary>
/// Turns two readings of resettable, cumulative counters into usage for the interval between them, deciding how much of
/// the interval was really observed. This is where restart-proof history is made; every rule errs towards "unknown".
/// See docs/decisions.md D-024 to D-029.
/// </summary>
public static class DeltaEngine
{
    public static IntervalResult Compare(DatabaseBaseline previous, DatabaseReading current, TimeSpan maxObservedSpan)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var from = previous.ObservedUtc;
        var to = current.ObservedUtc;

        // Database-level events come first: they can happen after an engine restart in the same interval, and then
        // the counters date from the database's return, not from the restart.
        if (previous.Interrupted)
        {
            return Result(IntervalKind.Reset, from, to, null, null, previous.InterruptedReason ?? "database was unavailable", current, lowerBound: true);
        }

        if (current.DatabaseId != previous.DatabaseId)
        {
            return Result(IntervalKind.Reset, from, to, null, null, $"database_id changed from {previous.DatabaseId} to {current.DatabaseId} (detached and attached, or restored)", current, lowerBound: true);
        }

        if (!current.Engine.IsSameAs(previous.Engine))
        {
            var startedAt = current.Engine.StartTimeUtc;
            var withinInterval = startedAt > from && startedAt <= to;
            if (withinInterval)
            {
                // Counters restarted from zero at a known moment: everything since then is exactly observed.
                var observedFrom = to - startedAt <= maxObservedSpan ? startedAt : (DateTimeOffset?)null;
                return Result(IntervalKind.InstanceRestart, from, to, observedFrom, startedAt, $"engine restarted at {startedAt:yyyy-MM-dd HH:mm:ss} UTC", current, lowerBound: false);
            }

            // A different engine that started before the previous reading: the database moved to another host
            // (Azure SQL Database reconfiguration, failover). Its counters restarted at an unknown time.
            return Result(IntervalKind.Reset, from, to, null, null, "database moved to an engine that started earlier (failover or reconfiguration)", current, lowerBound: true);
        }

        if (FindRemovedRow(previous, current) is { } removed)
        {
            // Learn documents only database-wide removal of usage rows (restart, detach, close). A row that vanished
            // or went backwards for an index that still exists is treated as that: every index's counters restarted.
            return Result(IntervalKind.Reset, from, to, null, null, removed, current, lowerBound: true);
        }

        var deltas = new List<IndexDelta>();
        foreach (var (key, usage) in current.Indexes)
        {
            if (usage is null)
            {
                continue;
            }

            var before = previous.Indexes.GetValueOrDefault(key);
            var delta = before is null ? Full(key, usage, lowerBound: false, from) : Difference(key, usage, before, from);
            if (delta is not null)
            {
                deltas.Add(delta);
            }
        }

        if (to <= from)
        {
            return new IntervalResult(IntervalKind.Gap, from, to, null, null, "the server clock moved backwards", deltas);
        }

        return to - from <= maxObservedSpan
            ? new IntervalResult(IntervalKind.Continuous, from, to, from, null, null, deltas)
            : new IntervalResult(IntervalKind.Gap, from, to, null, null, $"no reading for {Describe(to - from)}", deltas);
    }

    private static string? FindRemovedRow(DatabaseBaseline previous, DatabaseReading current)
    {
        foreach (var (key, before) in previous.Indexes)
        {
            if (before is null || !current.Indexes.TryGetValue(key, out var now))
            {
                continue;
            }

            if (now is null)
            {
                return "usage rows were removed while their indexes still exist (database restarted or closed)";
            }

            if (now.DecreasedSince(before))
            {
                return "usage counters went backwards (database restarted or closed)";
            }
        }

        return null;
    }

    private static IntervalResult Result(
        IntervalKind kind, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? observedFrom, DateTimeOffset? resetAt,
        string reason, DatabaseReading current, bool lowerBound)
    {
        var deltas = current.Indexes
            .Where(i => i.Value is not null)
            .Select(i => Full(i.Key, i.Value!, lowerBound, from))
            .OfType<IndexDelta>()
            .ToList();
        return new IntervalResult(kind, from, to, observedFrom, resetAt, reason, deltas);
    }

    private static IndexDelta? Full(long key, UsageCounters c, bool lowerBound, DateTimeOffset from) =>
        Create(key, c.UserSeeks, c.UserScans, c.UserLookups, c.UserUpdates, c.SystemSeeks, c.SystemScans, c.SystemLookups, c.SystemUpdates, lowerBound, c, from);

    private static IndexDelta? Difference(long key, UsageCounters c, UsageCounters p, DateTimeOffset from) =>
        Create(
            key,
            c.UserSeeks - p.UserSeeks,
            c.UserScans - p.UserScans,
            c.UserLookups - p.UserLookups,
            c.UserUpdates - p.UserUpdates,
            c.SystemSeeks - p.SystemSeeks,
            c.SystemScans - p.SystemScans,
            c.SystemLookups - p.SystemLookups,
            c.SystemUpdates - p.SystemUpdates,
            lowerBound: false,
            c,
            from);

    private static IndexDelta? Create(
        long key, long userSeeks, long userScans, long userLookups, long userUpdates,
        long systemSeeks, long systemScans, long systemLookups, long systemUpdates,
        bool lowerBound, UsageCounters current, DateTimeOffset from)
    {
        if (userSeeks == 0 && userScans == 0 && userLookups == 0 && userUpdates == 0
            && systemSeeks == 0 && systemScans == 0 && systemLookups == 0 && systemUpdates == 0)
        {
            return null;
        }

        var reads = userSeeks + userScans + userLookups;
        return new IndexDelta(
            key, userSeeks, userScans, userLookups, userUpdates, systemSeeks, systemScans, systemLookups, systemUpdates, lowerBound,
            LastUserReadUtc: reads > 0 && current.LastUserReadUtc > from ? current.LastUserReadUtc : null,
            LastUserUpdateUtc: userUpdates > 0 && current.LastUserUpdateUtc > from ? current.LastUserUpdateUtc : null);
    }

    internal static string Describe(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        _ => $"{(int)span.TotalMinutes}m",
    };
}
