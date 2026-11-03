using DotNet.Testcontainers.Builders;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Dormant.Integration;

/// <summary>
/// A disposable SQL Server in a container. DORMANT_CONTAINERS controls what happens when one cannot start:
/// <c>require</c> (Linux CI) fails the test, because CI must never silently skip these; <c>skip</c> (Windows CI, which
/// cannot run Linux images) skips without trying; unset (a developer machine) skips only when Docker is unavailable.
/// </summary>
internal sealed class SqlServerInstance : IAsyncDisposable
{
    private readonly MsSqlContainer _container;

    private SqlServerInstance(MsSqlContainer container)
    {
        _container = container;
    }

    /// <summary>Connection string for the sa login.</summary>
    public string AdminConnectionString => _container.GetConnectionString();

    /// <summary>Starts a container, or skips the calling test when Docker is unavailable (and not required).</summary>
    public static async Task<SqlServerInstance> StartAsync(string image, CancellationToken ct)
    {
        var (instance, unavailable) = await TryStartAsync(image, ct);
        if (instance is null)
        {
            Assert.Skip(unavailable!);
        }

        return instance;
    }

    /// <returns>The running instance, or null and the reason when Docker is unavailable and not required.</returns>
    public static async Task<(SqlServerInstance? Instance, string? Unavailable)> TryStartAsync(string image, CancellationToken ct)
    {
        var mode = Environment.GetEnvironmentVariable("DORMANT_CONTAINERS");
        if (string.Equals(mode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "SQL Server container suites are skipped here (DORMANT_CONTAINERS=skip).");
        }

        MsSqlContainer container;
        try
        {
            // Build() checks that Docker answers; StartAsync() pulls and starts the image.
            container = new MsSqlBuilder(image).Build();
        }
        catch (DockerUnavailableException ex)
        {
            if (string.Equals(mode, "require", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }

            return (null, $"Docker is not available: {ex.Message}");
        }

        try
        {
            await container.StartAsync(ct);
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }

        return (new SqlServerInstance(container), null);
    }

    /// <summary>A connection string for another login on the same instance.</summary>
    public string ConnectionStringFor(string login, string password) =>
        new SqlConnectionStringBuilder(AdminConnectionString) { UserID = login, Password = password, InitialCatalog = "master" }.ConnectionString;

    public Task ExecuteAsync(string sql, CancellationToken ct) => ExecuteInAsync("master", sql, ct);

    /// <summary>Runs batches (separated by GO lines) in <paramref name="database"/> as sa.</summary>
    public async Task ExecuteInAsync(string database, string sql, CancellationToken ct)
    {
        var connectionString = new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = database, Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        foreach (var batch in sql.Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Query Store's text for the given query hashes (0x… form), for diagnostics.</summary>
    public async Task<IReadOnlyList<string>> QueryTextsAsync(string database, IEnumerable<string> queryHashes, CancellationToken ct)
    {
        var hashes = queryHashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectionString = new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = database, Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT CONVERT(varchar(18), q.query_hash, 1), t.query_sql_text
            FROM sys.query_store_query AS q JOIN sys.query_store_query_text AS t ON t.query_text_id = q.query_text_id
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            if (hashes.Contains(reader.GetString(0)))
            {
                result.Add($"{reader.GetString(0)}: {reader.GetString(1)}");
            }
        }

        return result;
    }

    /// <summary>True once Query Store holds an executed plan of a query whose text contains <paramref name="text"/>.</summary>
    public async Task<bool> QueryStoreHoldsAsync(string database, string text, CancellationToken ct)
    {
        var connectionString = new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = database, Pooling = false }.ConnectionString;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.query_store_plan AS p
            JOIN sys.query_store_query AS q ON q.query_id = p.query_id
            JOIN sys.query_store_query_text AS t ON t.query_text_id = q.query_text_id
            WHERE p.last_execution_time IS NOT NULL AND t.query_sql_text LIKE '%' + @text + '%';
            """,
            connection);
        command.Parameters.AddWithValue("@text", text);
        return (int)(await command.ExecuteScalarAsync(ct))! > 0;
    }

    /// <summary>Stops and starts the SQL Server process; its databases survive, its usage counters do not.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        SqlConnection.ClearAllPools();
        await _container.StopAsync(ct);
        await _container.StartAsync(ct);

        // The server accepts connections before every database has finished recovery.
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (true)
        {
            await using var connection = new SqlConnection(AdminConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE state_desc <> 'ONLINE';", connection);
            if ((int)(await command.ExecuteScalarAsync(ct))! == 0)
            {
                return;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Databases did not come online within 2 minutes of the restart.");
            }

            await Task.Delay(500, ct);
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
