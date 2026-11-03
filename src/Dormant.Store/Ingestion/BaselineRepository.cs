using Dormant.Core.Collection;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>The last reading of each database, kept so the next reading can be compared with it.</summary>
internal sealed class BaselineRepository
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public BaselineRepository(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task<DatabaseBaseline?> LoadAsync(long databaseKey, CancellationToken ct)
    {
        DateTimeOffset observed;
        int databaseId;
        EngineIncarnation engine;
        bool interrupted;
        string? reason;
        await using (var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT observed_ms, database_id, engine_start_ticks, engine_ms_ticks, engine_start_ms, interrupted, interrupted_reason
            FROM db_baseline WHERE database_key = $db
            """,
            ("$db", databaseKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            observed = Sql.Time(reader.GetInt64(0));
            databaseId = reader.GetInt32(1);
            engine = new EngineIncarnation(reader.GetInt64(2), reader.GetInt64(3), Sql.Time(reader.GetInt64(4)));
            interrupted = reader.GetBoolean(5);
            reason = reader.IsDBNull(6) ? null : reader.GetString(6);
        }

        var indexes = new Dictionary<long, UsageCounters?>();
        await using (var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT index_key, has_usage, user_seeks, user_scans, user_lookups, user_updates,
                   system_seeks, system_scans, system_lookups, system_updates, last_user_read_ms, last_user_update_ms
            FROM index_baseline WHERE database_key = $db
            """,
            ("$db", databaseKey)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                indexes[reader.GetInt64(0)] = reader.GetBoolean(1)
                    ? new UsageCounters(
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                        reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                        Sql.Time(reader, 10), Sql.Time(reader, 11))
                    : null;
            }
        }

        return new DatabaseBaseline(observed, databaseId, engine, interrupted, reason, indexes);
    }

    public async Task SaveAsync(long databaseKey, DatabaseReading reading, CancellationToken ct)
    {
        await Sql.ExecuteAsync(
            _connection, _transaction,
            """
            INSERT OR REPLACE INTO db_baseline
                (database_key, observed_ms, database_id, engine_start_ticks, engine_ms_ticks, engine_start_ms, interrupted, interrupted_reason)
            VALUES ($db, $observed, $id, $startTicks, $msTicks, $startMs, 0, NULL)
            """,
            ct,
            ("$db", databaseKey),
            ("$observed", Sql.Ms(reading.ObservedUtc)),
            ("$id", reading.DatabaseId),
            ("$startTicks", reading.Engine.StartMsTicks),
            ("$msTicks", reading.Engine.MsTicks),
            ("$startMs", Sql.Ms(reading.Engine.StartTimeUtc)));

        await Sql.ExecuteAsync(_connection, _transaction, "DELETE FROM index_baseline WHERE database_key = $db", ct, ("$db", databaseKey));

        await using var command = Sql.Command(
            _connection, _transaction,
            """
            INSERT INTO index_baseline (index_key, database_key, has_usage, user_seeks, user_scans, user_lookups, user_updates,
                                        system_seeks, system_scans, system_lookups, system_updates, last_user_read_ms, last_user_update_ms)
            VALUES ($index, $db, $has, $us, $usc, $ul, $uu, $ss, $ssc, $sl, $su, $lr, $lu)
            """,
            ("$db", databaseKey));
        var p = command.Parameters;
        string[] names = ["$index", "$has", "$us", "$usc", "$ul", "$uu", "$ss", "$ssc", "$sl", "$su", "$lr", "$lu"];
        foreach (var name in names)
        {
            p.Add(new SqliteParameter(name, null));
        }

        foreach (var (key, usage) in reading.Indexes)
        {
            p["$index"].Value = key;
            p["$has"].Value = usage is null ? 0 : 1;
            p["$us"].Value = usage?.UserSeeks ?? 0;
            p["$usc"].Value = usage?.UserScans ?? 0;
            p["$ul"].Value = usage?.UserLookups ?? 0;
            p["$uu"].Value = usage?.UserUpdates ?? 0;
            p["$ss"].Value = usage?.SystemSeeks ?? 0;
            p["$ssc"].Value = usage?.SystemScans ?? 0;
            p["$sl"].Value = usage?.SystemLookups ?? 0;
            p["$su"].Value = usage?.SystemUpdates ?? 0;
            p["$lr"].Value = (object?)Sql.Ms(usage?.LastUserReadUtc) ?? DBNull.Value;
            p["$lu"].Value = (object?)Sql.Ms(usage?.LastUserUpdateUtc) ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The next reading of this database must not be compared with its baseline. Keeps the first reason.</summary>
    public Task<int> MarkInterruptedAsync(long databaseKey, string reason, CancellationToken ct) =>
        Sql.ExecuteAsync(
            _connection, _transaction,
            """
            UPDATE db_baseline SET interrupted = 1, interrupted_reason = COALESCE(interrupted_reason, $reason)
            WHERE database_key = $db
            """,
            ct, ("$db", databaseKey), ("$reason", reason));

    public async Task<IReadOnlyList<long>> DatabasesOfInstanceAsync(long instanceKey, CancellationToken ct)
    {
        var keys = new List<long>();
        await using var command = Sql.Command(
            _connection, _transaction,
            "SELECT b.database_key FROM db_baseline AS b JOIN db AS d ON d.database_key = b.database_key WHERE d.instance_key = $i",
            ("$i", instanceKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetInt64(0));
        }

        return keys;
    }
}
