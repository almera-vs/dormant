namespace Dormant.Core.Attribution;

/// <summary>A table or indexed view, by the names it currently has.</summary>
public sealed record ObjectKey(string Instance, string Database, string Schema, string Table);

/// <summary>
/// A query or stored procedure whose execution plan reads an object, as recorded by Query Store. Supporting
/// information only: Query Store may not capture every query, so the absence of a reader proves nothing (decision D-052).
/// </summary>
/// <param name="QueryHash">The query hash (binary(8), as 0x… text), which identifies the query when its text is not kept.</param>
/// <param name="Procedure">schema.name of the module the query belongs to, when it is part of one.</param>
/// <param name="LastExecutionUtc">When a plan of this query that reads the object last ran.</param>
/// <param name="QueryText">The query text, only when <c>query_store.store_text</c> is true.</param>
/// <param name="Indexes">The object's indexes the plans read.</param>
public sealed record ObjectReader(
    string QueryHash,
    string? Procedure,
    DateTimeOffset LastExecutionUtc,
    string? QueryText,
    IReadOnlyList<string> Indexes)
{
    /// <summary>How a person refers to the reader: the procedure if there is one, otherwise the query hash.</summary>
    public string Name => Procedure ?? $"query {QueryHash}";
}

/// <summary>Answers who has been reading an object.</summary>
public interface IAttributionSource
{
    /// <summary>Readers that ran since <paramref name="since"/>, most recent first.</summary>
    Task<IReadOnlyList<ObjectReader>> GetReadersAsync(ObjectKey key, DateTimeOffset since, CancellationToken ct);
}

public enum PlanAccess
{
    /// <summary>The plan scans, seeks or looks up rows in the object.</summary>
    Read,

    /// <summary>The plan inserts, updates or deletes rows in the object.</summary>
    Write,
}

/// <summary>An object an execution plan touches, as named in the plan.</summary>
/// <param name="Database">The database named in the plan, or null when the plan does not say (then: the plan's own).</param>
/// <param name="Index">The index used, or null for a heap.</param>
public sealed record PlanObjectReference(string? Database, string Schema, string Table, string? Index, PlanAccess Access);

/// <summary>Query Store's identity for a plan. A cleared Query Store can reuse plan ids, so the plan hash is part of it.</summary>
public readonly record struct PlanKey(long PlanId, string PlanHash);

/// <summary>What to read from one database's Query Store.</summary>
/// <param name="Since">Plans that last ran before this are not read.</param>
/// <param name="KnownPlans">Plans already parsed: only their last execution time is refreshed, their XML is not fetched again.</param>
public sealed record QueryStoreRequest(string DatabaseIdentity, string DatabaseName, DateTimeOffset Since, IReadOnlySet<PlanKey> KnownPlans);

/// <param name="References">The objects the plan touches; null for a known plan (not re-read) or one whose XML could not be parsed.</param>
/// <param name="ParseFailed">The plan's XML was fetched but could not be read.</param>
public sealed record HarvestedPlan(
    PlanKey Key,
    string QueryHash,
    string? Procedure,
    DateTimeOffset LastExecutionUtc,
    string? QueryText,
    IReadOnlyList<PlanObjectReference>? References,
    bool ParseFailed);

/// <param name="State">Query Store's actual state (READ_WRITE, READ_ONLY, OFF, ERROR) or why it was not read.</param>
/// <param name="Error">Why the database's Query Store could not be read, if it could not.</param>
public sealed record QueryStoreHarvest(
    string DatabaseIdentity,
    string DatabaseName,
    string State,
    IReadOnlyList<HarvestedPlan> Plans,
    string? Error);

/// <summary>Reads Query Store from one instance.</summary>
public interface IQueryStoreHarvester
{
    /// <param name="storeText">Read query text too; otherwise only hashes and procedure names.</param>
    Task<IReadOnlyList<QueryStoreHarvest>> HarvestAsync(IReadOnlyList<QueryStoreRequest> requests, bool storeText, CancellationToken ct);
}
