using Dormant.Core.History;
using Dormant.Store.Ingestion;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Status;

internal sealed class StatusReader
{
    private readonly SqliteConnection _connection;
    private readonly string _path;

    public StatusReader(SqliteConnection connection, string path)
    {
        _connection = connection;
        _path = path;
    }

    public async Task<StoreStatus> ReadAsync(CancellationToken ct)
    {
        var instances = new List<InstanceStatus>();
        foreach (var (key, name) in await ListInstancesAsync(ct))
        {
            var synthetic = await Sql.ScalarAsync(_connection, null, "SELECT COUNT(*) FROM collection WHERE instance_key = $i AND synthetic = 1", ct, ("$i", key)) ?? 0;
            var lastSynthetic = await Sql.ScalarAsync(_connection, null, "SELECT MAX(started_ms) FROM collection WHERE instance_key = $i AND synthetic = 1", ct, ("$i", key));
            instances.Add(new InstanceStatus(
                name,
                (int)(await Sql.ScalarAsync(_connection, null, "SELECT COUNT(*) FROM collection WHERE instance_key = $i", ct, ("$i", key)) ?? 0),
                await LastCollectionAsync(key, successfulOnly: false, ct),
                await LastCollectionAsync(key, successfulOnly: true, ct),
                await DatabasesAsync(key, ct),
                Merge(await EventsAsync(key, "instance_restart", ct)),
                await EventsAsync(key, "reset", ct),
                await EventsAsync(key, "gap", ct),
                (int)synthetic,
                lastSynthetic is { } ms ? Sql.Time(ms) : null));
        }

        return new StoreStatus(_path, FileSize(_path) + FileSize(_path + "-wal"), instances);
    }

    private async Task<List<(long Key, string Name)>> ListInstancesAsync(CancellationToken ct)
    {
        var result = new List<(long, string)>();
        await using var command = Sql.Command(_connection, null, "SELECT instance_key, name FROM instance ORDER BY name");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    private async Task<CollectionSummary?> LastCollectionAsync(long instanceKey, bool successfulOnly, CancellationToken ct)
    {
        await using var command = Sql.Command(
            _connection, null,
            $"""
            SELECT started_ms, duration_ms, outcome, error, server_name, platform, database_count, index_count
            FROM collection
            WHERE instance_key = $i {(successfulOnly ? "AND outcome <> 'failed'" : string.Empty)}
            ORDER BY started_ms DESC, collection_id DESC
            LIMIT 1
            """,
            ("$i", instanceKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CollectionSummary(
            Sql.Time(reader.GetInt64(0)),
            TimeSpan.FromMilliseconds(reader.GetInt64(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
    }

    private async Task<List<DatabaseStatus>> DatabasesAsync(long instanceKey, CancellationToken ct)
    {
        var result = new List<DatabaseStatus>();
        await using var command = Sql.Command(
            _connection, null,
            """
            SELECT d.name, d.first_seen_ms, COALESCE(b.observed_ms, d.last_seen_ms),
                   COALESCE((SELECT SUM(h.observed_ms) FROM db_day AS h WHERE h.database_key = d.database_key), 0),
                   o.outcome, o.detail
            FROM db AS d
            LEFT JOIN db_baseline AS b ON b.database_key = d.database_key
            LEFT JOIN db_observation AS o ON o.database_key = d.database_key
                 AND o.collection_id = (SELECT MAX(collection_id) FROM db_observation WHERE database_key = d.database_key)
            WHERE d.instance_key = $i
            ORDER BY d.name
            """,
            ("$i", instanceKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DatabaseStatus(
                reader.GetString(0),
                Sql.Time(reader.GetInt64(1)),
                Sql.Time(reader.GetInt64(2)),
                TimeSpan.FromMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return result;
    }

    private async Task<List<HistoryEvent>> EventsAsync(long instanceKey, string kind, CancellationToken ct)
    {
        var result = new List<HistoryEvent>();
        await using var command = Sql.Command(
            _connection, null,
            """
            SELECT e.at_ms, d.name, e.to_ms - e.from_ms, e.reason
            FROM history_event AS e JOIN db AS d ON d.database_key = e.database_key
            WHERE d.instance_key = $i AND e.kind = $kind
            ORDER BY 1 DESC, d.name
            """,
            ("$i", instanceKey), ("$kind", kind));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new HistoryEvent(
                Sql.Time(reader.GetInt64(0)),
                reader.GetString(1),
                TimeSpan.FromMilliseconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    /// <summary>One engine restart shows up once per database; report it once, naming the databases it affected.</summary>
    private static List<HistoryEvent> Merge(List<HistoryEvent> restarts) =>
        restarts
            .GroupBy(e => e.AtUtc)
            .Select(g => new HistoryEvent(g.Key, string.Join(", ", g.Select(e => e.Database).Order(StringComparer.OrdinalIgnoreCase)), g.Max(e => e.Duration), g.First().Reason))
            .OrderByDescending(e => e.AtUtc)
            .ToList();

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
