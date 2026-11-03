namespace Dormant.Collectors.SqlServer;

/// <summary>
/// The permissions Dormant needs on each platform, and the statement that grants each one.
/// Sources and reasoning: docs/sqlserver-internals.md and docs/permissions.md.
/// </summary>
internal sealed class SqlServerPermissions
{
    private readonly SqlServerFacts _facts;

    public SqlServerPermissions(SqlServerFacts facts)
    {
        _facts = facts;
    }

    /// <summary>
    /// Server-level permission that covers the usage DMVs (and implies the database-level state permission in
    /// every database). Null on Azure SQL Database, which has no server-level grant for this.
    /// </summary>
    public string? ServerStatePermission => _facts.Platform switch
    {
        SqlPlatform.AzureSqlDatabase => null,
        _ when _facts.UsesGranularPermissions => "VIEW SERVER PERFORMANCE STATE",
        _ => "VIEW SERVER STATE",
    };

    /// <summary>Database-level permission needed for sys.dm_db_partition_stats and Query Store views.</summary>
    public string DatabaseStatePermission =>
        _facts.UsesGranularPermissions ? "VIEW DATABASE PERFORMANCE STATE" : "VIEW DATABASE STATE";

    /// <summary>
    /// Database-level permission that makes table and index metadata visible. VIEW DEFINITION covers the
    /// narrower SQL Server 2022 permissions (VIEW SECURITY DEFINITION, VIEW PERFORMANCE DEFINITION).
    /// </summary>
    public static string DefinitionPermission => "VIEW DEFINITION";

    public bool IsPerDatabase => _facts.Platform == SqlPlatform.AzureSqlDatabase;

    public string GrantServerState() => $"GRANT {ServerStatePermission} TO {Login};";

    public string GrantDatabaseAccess() =>
        $"GRANT CONNECT ANY DATABASE TO {Login};  -- or create a user for the login in this database";

    /// <param name="database">The database the permission is missing in.</param>
    /// <param name="databaseUser">The user Dormant maps to in that database.</param>
    public string GrantDatabaseState(string database, string databaseUser) => IsPerDatabase
        ? $"-- in {SqlServerConnections.QuoteName(database)}: GRANT VIEW DATABASE STATE TO {SqlServerConnections.QuoteName(databaseUser)};"
        : GrantServerState();

    /// <param name="database">The database the permission is missing in.</param>
    /// <param name="databaseUser">The user Dormant maps to in that database.</param>
    public string GrantDefinition(string database, string databaseUser) => IsPerDatabase
        ? $"-- in {SqlServerConnections.QuoteName(database)}: GRANT VIEW DEFINITION TO {SqlServerConnections.QuoteName(databaseUser)};"
        : $"GRANT VIEW ANY DEFINITION TO {Login};  -- or in {SqlServerConnections.QuoteName(database)}: GRANT VIEW DEFINITION TO {SqlServerConnections.QuoteName(databaseUser)};";

    /// <summary>Azure SQL Database Basic, S0, S1 and elastic pools only accept the server role for usage DMVs.</summary>
    public string GrantAzureServerStateReader() =>
        $"-- in master: ALTER SERVER ROLE ##MS_ServerStateReader## ADD MEMBER {Login};";

    private string Login => SqlServerConnections.QuoteName(_facts.Login);
}
