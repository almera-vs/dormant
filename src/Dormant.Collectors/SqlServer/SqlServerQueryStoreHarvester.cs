using System.Globalization;
using System.Xml;
using Dormant.Attribution;
using Dormant.Core.Attribution;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dormant.Collectors.SqlServer;

/// <summary>
/// Reads Query Store incrementally (decision D-053): plan metadata for plans that ran since the last read, plan XML only
/// for plans not seen before, at most <see cref="MaxNewPlansPerDatabase"/> of them per database per run, oldest first,
/// so a large Query Store is read over several runs instead of all at once. Query text only on request.
/// One connection at a time, read-only, with Dormant's usual session settings.
/// </summary>
public sealed partial class SqlServerQueryStoreHarvester : IQueryStoreHarvester
{
    public const int MaxNewPlansPerDatabase = 1000;
    private const int PlansPerBatch = 50;

    private const string State = "SELECT actual_state_desc FROM sys.database_query_store_options;";

    private readonly string _connectionString;
    private readonly ILogger _logger;

    public SqlServerQueryStoreHarvester(string connectionString, ILogger<SqlServerQueryStoreHarvester>? logger = null)
    {
        _connectionString = SqlServerConnections.Prepare(connectionString);
        _logger = logger ?? NullLogger<SqlServerQueryStoreHarvester>.Instance;
    }

    public async Task<IReadOnlyList<QueryStoreHarvest>> HarvestAsync(IReadOnlyList<QueryStoreRequest> requests, bool storeText, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var results = new List<QueryStoreHarvest>();
        bool perDatabase;
        var connection = await SqlServerConnections.OpenAsync(_connectionString, ct);
        await using (connection)
        {
            perDatabase = (await SqlServerFacts.ReadAsync(connection, ct)).Platform == SqlPlatform.AzureSqlDatabase;
            if (!perDatabase)
            {
                foreach (var request in requests)
                {
                    results.Add(await HarvestDatabaseAsync(connection, request.DatabaseName, request, storeText, ct));
                }

                return results;
            }
        }

        // Azure SQL Database: each database on its own connection, one at a time.
        foreach (var request in requests)
        {
            try
            {
                var own = await SqlServerConnections.OpenAsync(SqlServerConnections.Prepare(_connectionString, request.DatabaseName), ct);
                await using (own)
                {
                    results.Add(await HarvestDatabaseAsync(own, null, request, storeText, ct));
                }
            }
            catch (SqlException ex)
            {
                results.Add(Failed(request, ex));
            }
        }

        return results;
    }

    private async Task<QueryStoreHarvest> HarvestDatabaseAsync(
        SqlConnection connection, string? inDatabase, QueryStoreRequest request, bool storeText, CancellationToken ct)
    {
        try
        {
            string? state;
            await using (var command = SqlServerConnections.CreateCommandIn(connection, inDatabase, State))
            {
                state = await command.ExecuteScalarAsync(ct) as string;
            }

            if (state is not ("READ_WRITE" or "READ_ONLY"))
            {
                return new QueryStoreHarvest(request.DatabaseIdentity, request.DatabaseName, state ?? "OFF", [], null);
            }

            var metadata = await ReadMetadataAsync(connection, inDatabase, request.Since, ct);

            // Oldest first; stop after the cap so the next run continues where this one ended.
            var newPlans = metadata.Where(m => !request.KnownPlans.Contains(m.Key)).ToList();
            var included = metadata;
            if (newPlans.Count > MaxNewPlansPerDatabase)
            {
                var cutoff = newPlans[MaxNewPlansPerDatabase - 1].LastExecutionUtc;
                included = metadata.Where(m => m.LastExecutionUtc <= cutoff).ToList();
                newPlans = newPlans.Where(m => m.LastExecutionUtc <= cutoff).ToList();
            }

            var xml = await ReadPlansAsync(connection, inDatabase, newPlans.Select(p => p.Key.PlanId).ToList(), ct);
            var texts = storeText
                ? await ReadTextsAsync(connection, inDatabase, newPlans.Select(p => p.QueryId).Distinct().ToList(), ct)
                : [];

            var plans = new List<HarvestedPlan>(included.Count);
            foreach (var m in included)
            {
                if (request.KnownPlans.Contains(m.Key))
                {
                    plans.Add(new HarvestedPlan(m.Key, m.QueryHash, m.Procedure, m.LastExecutionUtc, null, null, ParseFailed: false));
                    continue;
                }

                IReadOnlyList<PlanObjectReference>? references = null;
                var failed = false;
                if (xml.TryGetValue(m.Key.PlanId, out var planXml))
                {
                    try
                    {
                        references = ShowplanParser.Parse(planXml);
                    }
                    catch (XmlException ex)
                    {
                        failed = true;
                        LogUnreadablePlan(_logger, request.DatabaseName, m.Key.PlanId, ex);
                    }
                }
                else
                {
                    // Gone between the two reads (plan removed from Query Store): nothing to attribute.
                    failed = true;
                }

                plans.Add(new HarvestedPlan(m.Key, m.QueryHash, m.Procedure, m.LastExecutionUtc, texts.GetValueOrDefault(m.QueryId), references, failed));
            }

            return new QueryStoreHarvest(request.DatabaseIdentity, request.DatabaseName, state, plans, null);
        }
        catch (SqlException ex) when (connection.State == System.Data.ConnectionState.Open)
        {
            return Failed(request, ex);
        }
    }

    private QueryStoreHarvest Failed(QueryStoreRequest request, SqlException ex)
    {
        LogFailed(_logger, request.DatabaseName, ex.Number, ex);
        return new QueryStoreHarvest(request.DatabaseIdentity, request.DatabaseName, "UNREADABLE", [], SqlErrors.Describe(ex));
    }

    private sealed record PlanMetadata(PlanKey Key, long QueryId, string QueryHash, string? Procedure, DateTimeOffset LastExecutionUtc);

    private static async Task<List<PlanMetadata>> ReadMetadataAsync(SqlConnection connection, string? inDatabase, DateTimeOffset since, CancellationToken ct)
    {
        // The time is written as a literal Dormant formats itself; nothing user-supplied reaches the statement.
        var sinceLiteral = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'", CultureInfo.InvariantCulture);
        var sql = $"""
            SELECT p.plan_id, CONVERT(varchar(18), p.query_plan_hash, 1), p.query_id, CONVERT(varchar(18), q.query_hash, 1),
                   OBJECT_SCHEMA_NAME(q.object_id), OBJECT_NAME(q.object_id), p.last_execution_time
            FROM sys.query_store_plan AS p
            JOIN sys.query_store_query AS q ON q.query_id = p.query_id
            WHERE p.last_execution_time >= CAST(N'{sinceLiteral}' AS datetimeoffset)
            ORDER BY p.last_execution_time, p.plan_id;
            """;

        var result = new List<PlanMetadata>();
        await using var command = SqlServerConnections.CreateCommandIn(connection, inDatabase, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var procedure = reader.IsDBNull(4) || reader.IsDBNull(5) ? null : $"{reader.GetString(4)}.{reader.GetString(5)}";
            result.Add(new PlanMetadata(
                new PlanKey(reader.GetInt64(0), reader.GetString(1)),
                reader.GetInt64(2),
                reader.GetString(3),
                procedure,
                reader.GetDateTimeOffset(6)));
        }

        return result;
    }

    private static async Task<Dictionary<long, string>> ReadPlansAsync(SqlConnection connection, string? inDatabase, List<long> planIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var batch in planIds.Chunk(PlansPerBatch))
        {
            var ids = string.Join(',', batch.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            await using var command = SqlServerConnections.CreateCommandIn(
                connection, inDatabase, $"SELECT plan_id, query_plan FROM sys.query_store_plan WHERE plan_id IN ({ids});", timeoutSeconds: 120);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                {
                    result[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
        }

        return result;
    }

    private static async Task<Dictionary<long, string>> ReadTextsAsync(SqlConnection connection, string? inDatabase, List<long> queryIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var batch in queryIds.Chunk(PlansPerBatch))
        {
            var ids = string.Join(',', batch.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            await using var command = SqlServerConnections.CreateCommandIn(
                connection,
                inDatabase,
                $"""
                SELECT q.query_id, LEFT(t.query_sql_text, 4000)
                FROM sys.query_store_query AS q JOIN sys.query_store_query_text AS t ON t.query_text_id = q.query_text_id
                WHERE q.query_id IN ({ids});
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                {
                    result[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Query Store of {Database} could not be read (SQL error {Number})")]
    private static partial void LogFailed(ILogger logger, string database, int number, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Plan {PlanId} in {Database} is not readable XML; it is skipped")]
    private static partial void LogUnreadablePlan(ILogger logger, string database, long planId, Exception exception);
}
