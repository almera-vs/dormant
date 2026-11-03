using Dormant.Core.Attribution;
using Dormant.Store.Ingestion;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Attribution;

/// <summary>Writes harvested Query Store plans and prepares the next harvest's requests.</summary>
internal sealed class AttributionWriter
{
    /// <summary>
    /// The next read starts this long before the newest plan seen, so plans whose runtime statistics Query Store had not
    /// yet recorded at the last read are picked up. Known plans are cheap to see again.
    /// </summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(1);

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;

    public AttributionWriter(SqliteConnection connection, SqliteTransaction? transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>One request per collected database of the instance, from where its last read stopped.</summary>
    /// <param name="databases">Identity and current name of each database collected this time.</param>
    public async Task<IReadOnlyList<QueryStoreRequest>> RequestsAsync(
        string instanceName, IReadOnlyList<(string Identity, string Name)> databases, DateTimeOffset nowUtc, TimeSpan lookback, CancellationToken ct)
    {
        var requests = new List<QueryStoreRequest>();
        foreach (var (identity, name) in databases)
        {
            var databaseKey = await DatabaseKeyAsync(instanceName, identity, ct);
            var since = nowUtc - lookback;
            var known = new HashSet<PlanKey>();
            if (databaseKey is { } key)
            {
                var watermark = await Sql.ScalarAsync(_connection, _transaction, "SELECT watermark_ms FROM qs_state WHERE database_key = $db", ct, ("$db", key));
                if (watermark is { } ms && Sql.Time(ms) - Overlap > since)
                {
                    since = Sql.Time(ms) - Overlap;
                }

                await using var command = Sql.Command(_connection, _transaction, "SELECT plan_id, plan_hash FROM qs_plan WHERE database_key = $db", ("$db", key));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    known.Add(new PlanKey(reader.GetInt64(0), reader.GetString(1)));
                }
            }

            requests.Add(new QueryStoreRequest(identity, name, since, known));
        }

        return requests;
    }

    public async Task WriteAsync(string instanceName, IReadOnlyList<QueryStoreHarvest> harvests, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var objects = await ObjectsByNameAsync(instanceName, ct);
        foreach (var harvest in harvests)
        {
            if (await DatabaseKeyAsync(instanceName, harvest.DatabaseIdentity, ct) is not { } databaseKey)
            {
                continue;
            }

            foreach (var plan in harvest.Plans)
            {
                await Sql.ExecuteAsync(
                    _connection, _transaction,
                    """
                    INSERT INTO qs_plan (database_key, plan_id, plan_hash, query_hash, procedure_name, query_text, last_execution_ms, parsed)
                    VALUES ($db, $plan, $hash, $query, $proc, $text, $last, $parsed)
                    ON CONFLICT (database_key, plan_id, plan_hash) DO UPDATE SET
                        last_execution_ms = MAX(last_execution_ms, excluded.last_execution_ms),
                        procedure_name = COALESCE(excluded.procedure_name, procedure_name)
                    """,
                    ct,
                    ("$db", databaseKey), ("$plan", plan.Key.PlanId), ("$hash", plan.Key.PlanHash), ("$query", plan.QueryHash),
                    ("$proc", plan.Procedure), ("$text", plan.QueryText), ("$last", Sql.Ms(plan.LastExecutionUtc)),
                    ("$parsed", plan.References is not null));

                foreach (var reference in plan.References ?? [])
                {
                    var database = reference.Database ?? harvest.DatabaseName;
                    if (!objects.TryGetValue((database, reference.Schema, reference.Table), out var objectKey))
                    {
                        // Not a table Dormant tracks (another instance, a system object, or dropped since).
                        continue;
                    }

                    await Sql.ExecuteAsync(
                        _connection, _transaction,
                        """
                        INSERT OR IGNORE INTO qs_plan_object (database_key, plan_id, plan_hash, object_key, index_name, access)
                        VALUES ($db, $plan, $hash, $obj, $index, $access)
                        """,
                        ct,
                        ("$db", databaseKey), ("$plan", plan.Key.PlanId), ("$hash", plan.Key.PlanHash), ("$obj", objectKey),
                        ("$index", reference.Index ?? string.Empty), ("$access", reference.Access == PlanAccess.Read ? "read" : "write"));
                }
            }

            var newest = harvest.Plans.Count == 0 ? (long?)null : harvest.Plans.Max(p => Sql.Ms(p.LastExecutionUtc));
            await Sql.ExecuteAsync(
                _connection, _transaction,
                """
                INSERT INTO qs_state (database_key, state, watermark_ms, read_ms, error) VALUES ($db, $state, $mark, $now, $error)
                ON CONFLICT (database_key) DO UPDATE SET
                    state = excluded.state,
                    watermark_ms = COALESCE(MAX(watermark_ms, excluded.watermark_ms), watermark_ms, excluded.watermark_ms),
                    read_ms = excluded.read_ms,
                    error = excluded.error
                """,
                ct,
                ("$db", databaseKey), ("$state", harvest.State), ("$mark", newest), ("$now", Sql.Ms(nowUtc)), ("$error", harvest.Error));
        }
    }

    private Task<long?> DatabaseKeyAsync(string instanceName, string identity, CancellationToken ct) =>
        Sql.ScalarAsync(
            _connection, _transaction,
            "SELECT d.database_key FROM db AS d JOIN instance AS i ON i.instance_key = d.instance_key WHERE i.name = $i AND d.identity = $id",
            ct, ("$i", instanceName), ("$id", identity));

    /// <summary>Current tables of the instance by (database, schema, name), ignoring case as SQL Server usually does.</summary>
    private async Task<Dictionary<(string, string, string), long>> ObjectsByNameAsync(string instanceName, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), long>(NameComparer.Instance);
        await using var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT d.name, o.schema_name, o.name, o.object_key
            FROM obj AS o JOIN db AS d ON d.database_key = o.database_key JOIN instance AS i ON i.instance_key = d.instance_key
            WHERE i.name = $i
            ORDER BY o.last_seen_ms
            """,
            ("$i", instanceName));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Ordered by last seen, so a name reused by a recreated table maps to the newest one.
            result[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = reader.GetInt64(3);
        }

        return result;
    }

    private sealed class NameComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly NameComparer Instance = new();

        public bool Equals((string, string, string) x, (string, string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item3, y.Item3);

        public int GetHashCode((string, string, string) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3));
    }
}

/// <summary>Readers of an object, from the plans stored so far.</summary>
internal sealed class AttributionReader
{
    private readonly SqliteConnection _connection;

    public AttributionReader(SqliteConnection connection)
    {
        _connection = connection;
    }

    public async Task<IReadOnlyList<ObjectReader>> ReadersAsync(ObjectKey key, DateTimeOffset since, CancellationToken ct)
    {
        var readers = await QueryAsync(
            """
            SELECT q.query_hash, q.procedure_name, MAX(q.last_execution_ms), MAX(q.query_text), GROUP_CONCAT(DISTINCT r.index_name)
            FROM qs_plan_object AS r
            JOIN qs_plan AS q ON q.database_key = r.database_key AND q.plan_id = r.plan_id AND q.plan_hash = r.plan_hash
            JOIN obj AS o ON o.object_key = r.object_key
            JOIN db AS d ON d.database_key = o.database_key
            JOIN instance AS i ON i.instance_key = d.instance_key
            WHERE r.access = 'read' AND i.name = $i AND d.name = $d AND o.schema_name = $s AND o.name = $t
              AND q.last_execution_ms >= $since
            GROUP BY q.query_hash, q.procedure_name
            ORDER BY 3 DESC
            """,
            ct,
            ("$i", key.Instance), ("$d", key.Database), ("$s", key.Schema), ("$t", key.Table), ("$since", Sql.Ms(since)));
        return readers.Select(r => r.Reader).ToList();
    }

    /// <summary>The most recent reader of every object that has one.</summary>
    public async Task<Dictionary<long, ObjectReader>> LastReadersAsync(CancellationToken ct)
    {
        var rows = await QueryAsync(
            """
            SELECT q.query_hash, q.procedure_name, q.last_execution_ms, q.query_text, r.index_name, r.object_key
            FROM qs_plan_object AS r
            JOIN qs_plan AS q ON q.database_key = r.database_key AND q.plan_id = r.plan_id AND q.plan_hash = r.plan_hash
            WHERE r.access = 'read'
            ORDER BY r.object_key, q.last_execution_ms DESC
            """,
            ct);
        var result = new Dictionary<long, ObjectReader>();
        foreach (var (reader, objectKey) in rows)
        {
            result.TryAdd(objectKey!.Value, reader);
        }

        return result;
    }

    private async Task<List<(ObjectReader Reader, long? ObjectKey)>> QueryAsync(string sql, CancellationToken ct, params (string, object?)[] parameters)
    {
        var result = new List<(ObjectReader, long?)>();
        await using var command = Sql.Command(_connection, null, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var indexes = reader.IsDBNull(4)
                ? []
                : reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries);
            result.Add((
                new ObjectReader(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    Sql.Time(reader.GetInt64(2)),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    indexes),
                reader.FieldCount > 5 ? reader.GetInt64(5) : null));
        }

        return result;
    }
}
