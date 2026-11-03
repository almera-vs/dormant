namespace Dormant.Core.Configuration;

/// <summary>
/// Where a secret (such as a connection string) lives. Secrets are never written inline in the
/// configuration, and the resolved value is never logged.
/// </summary>
public abstract record SecretReference
{
    /// <summary>A description that is safe to log or print, e.g. <c>env:DORMANT_ERP_PROD</c>.</summary>
    public abstract string Describe();
}

public sealed record EnvironmentSecret(string VariableName) : SecretReference
{
    public override string Describe() => $"env:{VariableName}";
}

/// <param name="Path">Absolute path, resolved against the configuration file's directory.</param>
public sealed record FileSecret(string Path) : SecretReference
{
    public override string Describe() => $"file:{Path}";
}
