using System.Diagnostics;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dormant.Collectors.SqlServer;

/// <summary>
/// Reads one SQL Server instance. Holds at most one open connection at a time, reads only metadata and counters, and
/// never touches a database that is not ONLINE, not MULTI_USER, or set to AUTO_CLOSE.
/// </summary>
public sealed partial class SqlServerCollector : ICollector
{
    private readonly string _connectionString;
    private readonly DatabaseFilter _databases;
    private readonly ILogger _logger;

    /// <param name="connectionString">The user's connection string; Dormant's connection settings are applied on top.</param>
    /// <param name="databases">Which databases to read.</param>
    /// <param name="logger">Receives collection timings and per-database problems.</param>
    public SqlServerCollector(string connectionString, DatabaseFilter databases, ILogger<SqlServerCollector>? logger = null)
    {
        _connectionString = SqlServerConnections.Prepare(connectionString);
        _databases = databases;
        _logger = logger ?? NullLogger<SqlServerCollector>.Instance;
    }

    public async Task<InstanceInfo> DescribeAsync(CancellationToken ct)
    {
        var connection = await SqlServerConnections.OpenAsync(_connectionString, ct);
        await using (connection)
        {
            var facts = await SqlServerFacts.ReadAsync(connection, ct);
            var start = await SqlServerStartTime.ReadAsync(connection, ct);
            return ToInstanceInfo(facts, start);
        }
    }

    public async Task<Snapshot> CollectAsync(CancellationToken ct)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var databases = new List<DatabaseSnapshot>();
        SqlServerFacts facts;
        DateTimeOffset? engineStart = null;

        var connection = await SqlServerConnections.OpenAsync(_connectionString, ct);
        List<DatabaseRow> selected;
        await using (connection)
        {
            facts = await SqlServerFacts.ReadAsync(connection, ct);
            if (facts.Platform == SqlPlatform.Unsupported || !facts.IsVersionSupported)
            {
                throw new CollectionException($"{facts.PlatformDisplayName} {facts.ProductVersion} is not supported. Run 'dormant validate' for details.");
            }

            var perDatabase = facts.Platform == SqlPlatform.AzureSqlDatabase;
            var rows = await DatabaseRow.ListAsync(connection, includeAccess: !perDatabase, ct);
            selected = [];
            foreach (var row in rows.Where(r => _databases.Matches(r.Name)))
            {
                if (SkipReasonFor(row) is { } skip)
                {
                    databases.Add(skip);
                }
                else
                {
                    selected.Add(row);
                }
            }

            if (!perDatabase)
            {
                var start = await SqlServerStartTime.ReadAsync(connection, ct);
                engineStart = start.Utc;
                foreach (var row in selected)
                {
                    databases.Add(await ReadInContextAsync(connection, row, start.Engine, ct));
                }

                selected.Clear();
            }
        }

        // Azure SQL Database: one connection per database, opened only after the previous one is closed. Each database
        // reports the engine incarnation of the node it runs on.
        foreach (var row in selected)
        {
            var snapshot = await ReadOnOwnConnectionAsync(row, ct);
            engineStart ??= snapshot.Engine?.StartTimeUtc;
            databases.Add(snapshot);
        }

        stopwatch.Stop();
        var result = new Snapshot(
            new InstanceInfo(facts.ServerName, facts.PlatformDisplayName, facts.ProductVersion, facts.Edition, facts.Login, engineStart),
            startedUtc,
            stopwatch.Elapsed,
            databases.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList());

        var collected = result.Databases.Count(d => d.Outcome == DatabaseOutcome.Collected);
        var skipped = result.Databases.Count(d => d.Outcome == DatabaseOutcome.Skipped);
        var failed = result.Databases.Count - collected - skipped;
        LogCollected(_logger, facts.ServerName, collected, skipped, failed, result.IndexCount, (long)stopwatch.Elapsed.TotalMilliseconds);
        return result;
    }

    internal static InstanceInfo ToInstanceInfo(SqlServerFacts facts, SqlServerStartTime start) =>
        new(facts.ServerName, facts.PlatformDisplayName, facts.ProductVersion, facts.Edition, facts.Login, start.Utc);

    private static DatabaseSnapshot? SkipReasonFor(DatabaseRow row)
    {
        if (!string.Equals(row.State, "ONLINE", StringComparison.Ordinal))
        {
            return DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.NotOnline, row.State);
        }

        if (!string.Equals(row.UserAccess, "MULTI_USER", StringComparison.Ordinal))
        {
            return DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.RestrictedAccess, row.UserAccess);
        }

        if (row.IsAutoClose)
        {
            return DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.AutoClose, "AUTO_CLOSE is ON");
        }

        if (row.HasAccess == false)
        {
            return DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.NoAccess, "the login cannot open this database");
        }

        return null;
    }

    private async Task<DatabaseSnapshot> ReadInContextAsync(SqlConnection connection, DatabaseRow row, EngineIncarnation engine, CancellationToken ct)
    {
        try
        {
            return await SqlServerUsageReader.ReadAsync(connection, row.Name, row, identityByCreateDate: false, engine, ct);
        }
        catch (SqlException ex) when (connection.State == System.Data.ConnectionState.Open)
        {
            // The connection survived, so the problem is this database's alone; keep going with the others.
            return DatabaseProblem(row, ex);
        }
    }

    private async Task<DatabaseSnapshot> ReadOnOwnConnectionAsync(DatabaseRow row, CancellationToken ct)
    {
        SqlConnection connection;
        try
        {
            connection = await SqlServerConnections.OpenAsync(SqlServerConnections.Prepare(_connectionString, row.Name), ct);
        }
        catch (SqlException ex)
        {
            return DatabaseProblem(row, ex);
        }

        await using (connection)
        {
            try
            {
                var start = await SqlServerStartTime.ReadAsync(connection, ct);
                return await SqlServerUsageReader.ReadAsync(connection, null, row, identityByCreateDate: true, start.Engine, ct);
            }
            catch (SqlException ex) when (connection.State == System.Data.ConnectionState.Open)
            {
                return DatabaseProblem(row, ex);
            }
        }
    }

    private DatabaseSnapshot DatabaseProblem(DatabaseRow row, SqlException ex)
    {
        LogDatabaseProblem(_logger, row.Name, ex.Number, ex);
        return ex.Number switch
        {
            SqlErrors.NotReadableSecondary => DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.NotReadableSecondary, "non-readable availability group secondary"),
            916 or 4060 => DatabaseSnapshot.Skipped(null, row.Name, row.DatabaseId, SkipReason.NoAccess, SqlErrors.Describe(ex)),
            _ => DatabaseSnapshot.Failed(null, row.Name, row.DatabaseId, SqlErrors.Describe(ex)),
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collected {Server}: {Collected} databases read, {Skipped} skipped, {Failed} failed, {Indexes} indexes in {DurationMs} ms")]
    private static partial void LogCollected(ILogger logger, string server, int collected, int skipped, int failed, int indexes, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read database {Database} (SQL error {Number})")]
    private static partial void LogDatabaseProblem(ILogger logger, string database, int number, Exception exception);
}

/// <summary>Collection cannot proceed on this instance, for a reason that is not a SQL error.</summary>
public sealed class CollectionException : Exception
{
    public CollectionException()
    {
    }

    public CollectionException(string message)
        : base(message)
    {
    }

    public CollectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
