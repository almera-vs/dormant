using System.Diagnostics;
using System.Globalization;
using Dormant.Core.Configuration;
using Dormant.Core.Validation;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Dormant.Collectors.SqlServer;

/// <summary>
/// Checks that Dormant can connect to an instance and read everything collection will need, and says exactly what
/// is missing. Reads only server properties, permission checks and one row from each DMV; never row data.
/// </summary>
public sealed partial class SqlServerValidator : IInstanceValidator
{
    private const string InstanceScope = "instance";

    private readonly SecretResolver _secrets;
    private readonly ILogger<SqlServerValidator> _logger;

    public SqlServerValidator(SecretResolver secrets, ILogger<SqlServerValidator> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<InstanceValidation> ValidateAsync(InstanceOptions instance, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var checks = new CheckList(instance.Name, _logger);

        string connectionString;
        try
        {
            var secret = await _secrets.ResolveAsync(instance.Connection, ct);
            connectionString = SqlServerConnections.Prepare(secret);
        }
        catch (SecretUnavailableException ex)
        {
            checks.Fail(InstanceScope, "connection string", ex.Message, $"Provide the connection string through {instance.Connection.Describe()}.");
            return checks.ToResult(info: null);
        }
        catch (InvalidConnectionStringException ex)
        {
            checks.Fail(InstanceScope, "connection string", ex.Message);
            return checks.ToResult(info: null);
        }

        var stopwatch = Stopwatch.StartNew();
        SqlConnection connection;
        try
        {
            connection = await SqlServerConnections.OpenAsync(connectionString, ct);
        }
        catch (SqlException ex)
        {
            LogSqlError(_logger, instance.Name, "connect", ex);
            checks.Fail(InstanceScope, "connect", $"{SqlServerConnections.DescribeTarget(connectionString)}: {SqlErrors.Describe(ex)}");
            return checks.ToResult(info: null);
        }

        SqlServerFacts facts;
        IReadOnlyList<DatabaseRow> databases;
        Core.Collection.InstanceInfo? info = null;
        await using (connection)
        {
            checks.Ok(InstanceScope, "connect", string.Create(
                CultureInfo.InvariantCulture,
                $"{SqlServerConnections.DescribeTarget(connectionString)} in {stopwatch.ElapsedMilliseconds} ms"));

            facts = await SqlServerFacts.ReadAsync(connection, ct);
            if (!CheckPlatform(facts, checks))
            {
                return checks.ToResult(info: null);
            }

            var permissions = new SqlServerPermissions(facts);
            if (!permissions.IsPerDatabase)
            {
                info = await CheckServerStateAsync(connection, facts, permissions, checks, ct);
            }

            databases = await DatabaseRow.ListAsync(connection, includeAccess: !permissions.IsPerDatabase, ct);

            if (!permissions.IsPerDatabase)
            {
                var selected = SelectDatabases(databases, instance, checks);
                foreach (var database in selected)
                {
                    await new SqlServerDatabaseChecks(instance, permissions, checks)
                        .RunInContextAsync(connection, database, ct);
                }

                return checks.ToResult(info);
            }
        }

        // Azure SQL Database: each database is its own connection. The server connection is closed first so that
        // Dormant never holds more than one connection to an instance.
        var azurePermissions = new SqlServerPermissions(facts);
        foreach (var database in SelectDatabases(databases, instance, checks))
        {
            var databaseInfo = await new SqlServerDatabaseChecks(instance, azurePermissions, checks)
                .RunOnOwnConnectionAsync(connectionString, database, facts, ct);
            info ??= databaseInfo;
        }

        return checks.ToResult(info);
    }

    private static bool CheckPlatform(SqlServerFacts facts, CheckList checks)
    {
        var description = $"{facts.PlatformDisplayName}, {facts.Edition}, build {facts.ProductVersion}, connected as {facts.Login}";
        if (facts.Platform == SqlPlatform.Unsupported)
        {
            checks.Fail(InstanceScope, "platform", $"{description}. Dormant supports SQL Server 2016 and later, Azure SQL Database, Azure SQL Managed Instance and Amazon RDS for SQL Server.");
            return false;
        }

        if (!facts.IsVersionSupported)
        {
            checks.Fail(InstanceScope, "platform", $"{description}. Dormant supports SQL Server 2016 (13.x) and later.");
            return false;
        }

        checks.Ok(InstanceScope, "platform", description);
        return true;
    }

    private async Task<Core.Collection.InstanceInfo?> CheckServerStateAsync(
        SqlConnection connection, SqlServerFacts facts, SqlServerPermissions permissions, CheckList checks, CancellationToken ct)
    {
        var permission = permissions.ServerStatePermission!;
        await using (var command = SqlServerConnections.CreateCommand(connection, "SELECT HAS_PERMS_BY_NAME(NULL, NULL, @permission);"))
        {
            command.Parameters.Add(new SqlParameter("@permission", System.Data.SqlDbType.NVarChar, 128) { Value = permission });
            var granted = await command.ExecuteScalarAsync(ct) is int value && value == 1;
            if (granted)
            {
                checks.Ok(InstanceScope, permission, "granted");
            }
            else
            {
                checks.Fail(InstanceScope, permission, "not granted; usage statistics and the instance start time cannot be read", permissions.GrantServerState());
            }
        }

        try
        {
            var start = await SqlServerStartTime.ReadAsync(connection, ct);
            checks.Ok(InstanceScope, "start time", $"last started {start.Utc:yyyy-MM-dd HH:mm} UTC (sys.dm_os_sys_info)");
            await SqlProbe.ReadOneRowAsync(connection, "SELECT TOP (1) 1 FROM sys.dm_db_index_usage_stats;", ct);
            checks.Ok(InstanceScope, "index usage", "sys.dm_db_index_usage_stats is readable");
            return SqlServerCollector.ToInstanceInfo(facts, start);
        }
        catch (SqlException ex) when (SqlErrors.IsPermissionDenied(ex))
        {
            LogSqlError(_logger, InstanceScope, "server state", ex);
            checks.Fail(InstanceScope, "usage statistics", SqlErrors.Describe(ex), permissions.GrantServerState());
            return null;
        }
    }

    private static List<DatabaseRow> SelectDatabases(IReadOnlyList<DatabaseRow> all, InstanceOptions instance, CheckList checks)
    {
        var matched = all.Where(d => instance.Databases.Matches(d.Name)).ToList();
        var selected = new List<DatabaseRow>();
        foreach (var database in matched)
        {
            if (!string.Equals(database.State, "ONLINE", StringComparison.Ordinal))
            {
                checks.Warn(database.Name, "state", $"{database.State}; skipped until it is online");
            }
            else if (!string.Equals(database.UserAccess, "MULTI_USER", StringComparison.Ordinal))
            {
                checks.Warn(database.Name, "state", $"{database.UserAccess}; skipped so that Dormant never takes a restricted connection");
            }
            else
            {
                selected.Add(database);
            }
        }

        if (matched.Count == 0)
        {
            checks.Fail(InstanceScope, "databases", string.Create(
                CultureInfo.InvariantCulture,
                $"none of the {all.Count} visible databases match include [{string.Join(", ", instance.Databases.Include)}] and exclude [{string.Join(", ", instance.Databases.Exclude)}]"));
        }
        else
        {
            checks.Ok(InstanceScope, "databases", string.Create(
                CultureInfo.InvariantCulture,
                $"{matched.Count} of {all.Count} visible databases selected, {selected.Count} online and open to all users"));
        }

        return selected;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "SQL error during validation of {Instance} ({Step})")]
    internal static partial void LogSqlError(ILogger logger, string instance, string step, Exception exception);
}
