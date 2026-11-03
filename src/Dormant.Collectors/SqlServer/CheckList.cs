using Dormant.Core.Collection;
using Dormant.Core.Validation;
using Microsoft.Extensions.Logging;

namespace Dormant.Collectors.SqlServer;

/// <summary>Accumulates validation results for one instance and logs the ones that need attention.</summary>
internal sealed partial class CheckList
{
    private readonly List<CheckResult> _checks = [];
    private readonly string _instanceName;
    private readonly ILogger _logger;

    public CheckList(string instanceName, ILogger logger)
    {
        _instanceName = instanceName;
        _logger = logger;
    }

    public void Ok(string scope, string name, string detail) =>
        _checks.Add(new CheckResult(scope, name, CheckStatus.Ok, detail));

    public void Warn(string scope, string name, string detail, string? remedy = null)
    {
        _checks.Add(new CheckResult(scope, name, CheckStatus.Warning, detail, remedy));
        LogWarning(_logger, _instanceName, scope, name, detail);
    }

    public void Fail(string scope, string name, string detail, string? remedy = null)
    {
        _checks.Add(new CheckResult(scope, name, CheckStatus.Failed, detail, remedy));
        LogFailure(_logger, _instanceName, scope, name, detail);
    }

    public InstanceValidation ToResult(InstanceInfo? info) => new(_instanceName, info, _checks.ToList());

    [LoggerMessage(Level = LogLevel.Debug, Message = "Validation warning for {Instance} {Scope} {Check}: {Detail}")]
    private static partial void LogWarning(ILogger logger, string instance, string scope, string check, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Validation failed for {Instance} {Scope} {Check}: {Detail}")]
    private static partial void LogFailure(ILogger logger, string instance, string scope, string check, string detail);
}
