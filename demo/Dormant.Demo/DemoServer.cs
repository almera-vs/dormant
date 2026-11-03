using Microsoft.Data.SqlClient;

namespace Dormant.Demo;

/// <summary>The SQL Server the demo runs against.</summary>
public interface IDemoServer
{
    /// <summary>A sysadmin connection, used only to build the demo database and run the workload.</summary>
    string AdminConnectionString { get; }

    /// <summary>The least-privilege 'dormant' login, as Dormant itself connects.</summary>
    string DormantConnectionString { get; }

    /// <summary>Restarts SQL Server and returns once every database is online again.</summary>
    Task RestartAsync(CancellationToken ct);
}

/// <summary>The docker-compose demo's server: restarted by a shutdown, brought back by the container's restart policy.</summary>
public sealed class ComposeServer : IDemoServer
{
    public ComposeServer(string adminConnectionString, string dormantPassword)
    {
        AdminConnectionString = adminConnectionString;
        DormantConnectionString = DemoSetup.DormantConnectionString(adminConnectionString, dormantPassword);
    }

    public string AdminConnectionString { get; }

    public string DormantConnectionString { get; }

    public async Task RestartAsync(CancellationToken ct)
    {
        SqlConnection.ClearAllPools();
        try
        {
            await using var connection = new SqlConnection(AdminConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand("SHUTDOWN WITH NOWAIT;", connection);
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException)
        {
            // The server closes this connection as it stops.
        }

        SqlConnection.ClearAllPools();
        await Sql.WaitUntilOnlineAsync(AdminConnectionString, TimeSpan.FromMinutes(5), ct);
    }
}

internal static class Sql
{
    /// <summary>Waits until the server accepts connections and every database is online.</summary>
    public static async Task WaitUntilOnlineAsync(string connectionString, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5, Pooling = false };
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(ct);
                await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE state_desc <> 'ONLINE';", connection);
                if ((int)(await command.ExecuteScalarAsync(ct))! == 0)
                {
                    return;
                }
            }
            catch (SqlException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Still starting.
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"SQL Server was not ready within {timeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public static async Task ExecuteAsync(string connectionString, IEnumerable<string> batches, CancellationToken ct)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);
        foreach (var batch in batches)
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 600 };
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }
}
