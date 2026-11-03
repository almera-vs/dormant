namespace Dormant.Core.Collection;

/// <summary>What a monitored instance says about itself.</summary>
/// <param name="ServerName">The name the server reports for itself.</param>
/// <param name="Platform">Human-readable platform, e.g. "SQL Server 2022" or "Azure SQL Database".</param>
/// <param name="ProductVersion">Engine build, e.g. 16.0.4135.4.</param>
/// <param name="Edition">Engine edition, e.g. "Enterprise Edition (64-bit)".</param>
/// <param name="Login">The login Dormant is connected as.</param>
/// <param name="StartTimeUtc">
/// When the engine last started, in UTC. Usage counters reset at this moment. Null when it could not be read (for
/// Azure SQL Database it is read per database, from the node that hosts it).
/// </param>
public sealed record InstanceInfo(
    string ServerName,
    string Platform,
    string ProductVersion,
    string Edition,
    string Login,
    DateTimeOffset? StartTimeUtc);
