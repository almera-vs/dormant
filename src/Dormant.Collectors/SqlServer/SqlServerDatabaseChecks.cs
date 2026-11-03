using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

/// <summary>Permission and readability checks for one database.</summary>
internal sealed class SqlServerDatabaseChecks
{
    private readonly InstanceOptions _instance;
    private readonly SqlServerPermissions _permissions;
    private readonly CheckList _checks;

    public SqlServerDatabaseChecks(InstanceOptions instance, SqlServerPermissions permissions, CheckList checks)
    {
        _instance = instance;
        _permissions = permissions;
        _checks = checks;
    }

    /// <summary>SQL Server, Managed Instance and RDS: runs inside the database over the instance's one connection.</summary>
    public async Task RunInContextAsync(SqlConnection connection, DatabaseRow database, CancellationToken ct)
    {
        if (database.HasAccess == false)
        {
            _checks.Fail(database.Name, "access", "the login cannot open this database", _permissions.GrantDatabaseAccess());
            return;
        }

        if (database.IsAutoClose)
        {
            // Collection skips these databases; validation does not open them either.
            _checks.Warn(
                database.Name,
                "AUTO_CLOSE",
                "is ON, so Dormant skips this database: the engine discards its usage counters whenever it closes, and reading it would keep waking it.",
                $"ALTER DATABASE {SqlServerConnections.QuoteName(database.Name)} SET AUTO_CLOSE OFF;");
            return;
        }

        try
        {
            await RunChecksAsync(new Executor(connection, database.Name), database.Name, ct);
        }
        catch (SqlException ex) when (ex.Number == SqlErrors.NotReadableSecondary)
        {
            _checks.Warn(database.Name, "access", "non-readable availability group secondary; monitor it on the primary replica");
        }
        catch (SqlException ex) when (SqlErrors.IsPermissionDenied(ex))
        {
            _checks.Fail(database.Name, "access", SqlErrors.Describe(ex), _permissions.GrantDatabaseAccess());
        }
    }

    /// <summary>Azure SQL Database: opens a connection to this database only, after the previous one is closed.</summary>
    public async Task<InstanceInfo?> RunOnOwnConnectionAsync(
        string connectionString, DatabaseRow database, SqlServerFacts serverFacts, CancellationToken ct)
    {
        SqlConnection connection;
        try
        {
            connection = await SqlServerConnections.OpenAsync(SqlServerConnections.Prepare(connectionString, database.Name), ct);
        }
        catch (SqlException ex)
        {
            _checks.Fail(database.Name, "connect", SqlErrors.Describe(ex), "Create a user for the login in this database: CREATE USER [name] FROM LOGIN [name]; (or FROM EXTERNAL PROVIDER for Microsoft Entra ID)");
            return null;
        }

        await using (connection)
        {
            var executor = new Executor(connection, database: null);
            await RunChecksAsync(executor, database.Name, ct);

            try
            {
                await SqlProbe.ReadOneRowAsync(connection, "SELECT TOP (1) 1 FROM sys.dm_db_index_usage_stats;", ct);
                var start = await SqlServerStartTime.ReadAsync(connection, ct);
                _checks.Ok(database.Name, "index usage", "sys.dm_db_index_usage_stats and sys.dm_os_sys_info are readable");
                var facts = await SqlServerFacts.ReadAsync(connection, ct);
                return SqlServerCollector.ToInstanceInfo(facts with { ServerName = serverFacts.ServerName }, start);
            }
            catch (SqlException ex) when (SqlErrors.IsPermissionDenied(ex))
            {
                _checks.Fail(
                    database.Name,
                    "index usage",
                    $"{SqlErrors.Describe(ex)} Basic, S0 and S1 databases and databases in elastic pools need the ##MS_ServerStateReader## server role instead of VIEW DATABASE STATE.",
                    _permissions.GrantAzureServerStateReader());
                return null;
            }
        }
    }

    private async Task RunChecksAsync(Executor executor, string database, CancellationToken ct)
    {
        var statePermission = _permissions.DatabaseStatePermission;
        var definitionPermission = SqlServerPermissions.DefinitionPermission;

        bool hasState, hasDefinition;
        string user;
        await using (var command = executor.Command(
            $"SELECT HAS_PERMS_BY_NAME(NULL, N'DATABASE', {SqlServerConnections.QuoteLiteral(statePermission)}), " +
            $"HAS_PERMS_BY_NAME(NULL, N'DATABASE', {SqlServerConnections.QuoteLiteral(definitionPermission)}), USER_NAME();"))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException("HAS_PERMS_BY_NAME returned no row.");
            }

            hasState = !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
            hasDefinition = !reader.IsDBNull(1) && reader.GetInt32(1) == 1;
            user = reader.IsDBNull(2) ? "unknown" : reader.GetString(2);
        }

        if (hasState)
        {
            _checks.Ok(database, statePermission, "granted");
        }
        else
        {
            _checks.Fail(database, statePermission, "not granted; size and partition statistics cannot be read", _permissions.GrantDatabaseState(database, user));
        }

        if (hasDefinition)
        {
            _checks.Ok(database, definitionPermission, "granted");
        }
        else
        {
            _checks.Fail(database, definitionPermission, "not granted; table and index names would be hidden from Dormant", _permissions.GrantDefinition(database, user));
        }

        try
        {
            await using var probe = executor.Command("SELECT TOP (1) 1 FROM sys.dm_db_partition_stats;");
            await using var reader = await probe.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
            }

            _checks.Ok(database, "size statistics", "sys.dm_db_partition_stats is readable");
        }
        catch (SqlException ex) when (SqlErrors.IsPermissionDenied(ex))
        {
            _checks.Fail(database, "size statistics", SqlErrors.Describe(ex), _permissions.GrantDatabaseState(database, user));
        }

        if (_instance.QueryStore.Enabled)
        {
            await CheckQueryStoreAsync(executor, database, user, ct);
        }
    }

    private async Task CheckQueryStoreAsync(Executor executor, string database, string user, CancellationToken ct)
    {
        string? state;
        try
        {
            await using var command = executor.Command("SELECT actual_state_desc FROM sys.database_query_store_options;");
            state = await command.ExecuteScalarAsync(ct) as string;
        }
        catch (SqlException ex) when (SqlErrors.IsPermissionDenied(ex))
        {
            _checks.Fail(database, "Query Store", SqlErrors.Describe(ex), _permissions.GrantDatabaseState(database, user));
            return;
        }

        switch (state)
        {
            case "READ_WRITE":
                _checks.Ok(database, "Query Store", "on (READ_WRITE); readers can be attributed");
                break;
            case "READ_ONLY":
                _checks.Warn(database, "Query Store", "READ_ONLY; attribution uses only what it already holds, new reads are not captured");
                break;
            default:
                _checks.Warn(database, "Query Store", $"{state ?? "not available"}; readers cannot be attributed in this database. Usage history is not affected.");
                break;
        }
    }

    /// <summary>Runs statements either directly or inside another database through sp_executesql.</summary>
    private sealed class Executor(SqlConnection connection, string? database)
    {
        public SqlCommand Command(string sql) => SqlServerConnections.CreateCommandIn(connection, database, sql);
    }
}
