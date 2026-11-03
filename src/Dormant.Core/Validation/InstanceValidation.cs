using Dormant.Core.Collection;
using Dormant.Core.Configuration;

namespace Dormant.Core.Validation;

public enum CheckStatus
{
    Ok,

    /// <summary>Collection works, but something limits what Dormant can see (e.g. Query Store is off).</summary>
    Warning,

    /// <summary>Collection cannot work as configured until this is fixed.</summary>
    Failed,
}

/// <param name="Scope">"instance", or the database the check ran in.</param>
/// <param name="Name">What was checked, e.g. "connect" or "VIEW SERVER STATE".</param>
/// <param name="Status">Whether collection can work with what was found.</param>
/// <param name="Detail">What was found, in plain words.</param>
/// <param name="Remedy">A statement or step that fixes a failed check, when one is known.</param>
public sealed record CheckResult(string Scope, string Name, CheckStatus Status, string Detail, string? Remedy = null);

/// <summary>The outcome of <c>dormant validate</c> for one configured instance.</summary>
public sealed record InstanceValidation(string InstanceName, InstanceInfo? Info, IReadOnlyList<CheckResult> Checks)
{
    public bool HasFailures => Checks.Any(c => c.Status == CheckStatus.Failed);
}

/// <summary>Checks connectivity and permissions for an instance without collecting anything.</summary>
public interface IInstanceValidator
{
    Task<InstanceValidation> ValidateAsync(InstanceOptions instance, CancellationToken ct);
}
