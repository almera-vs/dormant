using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

/// <summary>Opens connections the way Dormant always connects: identified, short lock waits, low deadlock priority.</summary>
internal static class SqlServerConnections
{
    public const string ApplicationName = "Dormant";

    /// <summary>How long any statement may wait for a lock before giving up, in milliseconds.</summary>
    public const int LockTimeoutMs = 2000;

    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>
    /// Applies Dormant's connection settings to a user-supplied connection string.
    /// Throws <see cref="InvalidConnectionStringException"/> when the string is malformed.
    /// </summary>
    public static string Prepare(string connectionString, string? database = null)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            // The driver's message can quote fragments of the string, which may include a password,
            // so neither the message nor the inner exception is carried forward.
            throw new InvalidConnectionStringException(ex.GetType().Name);
        }

        builder.ApplicationName = ApplicationName;
        if (database is not null)
        {
            builder.InitialCatalog = database;
        }

        return builder.ConnectionString;
    }

    /// <summary>A description of the target that is safe to log: server and database only.</summary>
    public static string DescribeTarget(string preparedConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(preparedConnectionString);
        return string.IsNullOrEmpty(builder.InitialCatalog) ? builder.DataSource : $"{builder.DataSource}/{builder.InitialCatalog}";
    }

    public static async Task<SqlConnection> OpenAsync(string preparedConnectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(preparedConnectionString);
        try
        {
            await connection.OpenAsync(ct);

            // Session settings are re-applied on every open because pooled connections may not keep them.
            // READ UNCOMMITTED: Dormant reads only metadata and counters, and must not wait on (or hold) shared locks.
            await using var command = CreateCommand(
                connection,
                $"SET LOCK_TIMEOUT {LockTimeoutMs}; SET DEADLOCK_PRIORITY LOW; SET NOCOUNT ON; " +
                "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;");
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static SqlCommand CreateCommand(SqlConnection connection, string sql, int timeoutSeconds = DefaultCommandTimeoutSeconds) =>
        new(sql, connection) { CommandTimeout = timeoutSeconds };

    /// <summary>
    /// A command that runs <paramref name="sql"/> inside <paramref name="database"/> through sp_executesql, over the
    /// same connection; or directly when <paramref name="database"/> is null (Azure SQL Database, one connection per
    /// database).
    /// </summary>
    public static SqlCommand CreateCommandIn(SqlConnection connection, string? database, string sql, int timeoutSeconds = DefaultCommandTimeoutSeconds)
    {
        if (database is null)
        {
            return CreateCommand(connection, sql, timeoutSeconds);
        }

        var command = CreateCommand(connection, $"EXEC {QuoteName(database)}.sys.sp_executesql @stmt;", timeoutSeconds);
        command.Parameters.Add(new SqlParameter("@stmt", System.Data.SqlDbType.NVarChar, -1) { Value = sql });
        return command;
    }

    /// <summary>Quotes an identifier exactly like T-SQL QUOTENAME does for brackets.</summary>
    public static string QuoteName(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Quotes a value as an N'...' literal for use in generated GRANT statements shown to the user.</summary>
    public static string QuoteLiteral(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
