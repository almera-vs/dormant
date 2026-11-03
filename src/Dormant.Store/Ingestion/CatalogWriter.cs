using System.Globalization;
using Dormant.Core.Collection;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>
/// Maps observed tables and indexes to their stable keys, creating new ones where identity rules say so:
/// a table is (object_id, create_date), so a dropped-and-recreated table is new and a renamed one is not; an index is
/// (index_id, name, type) within its table, so a renamed or retyped index starts a new history.
/// </summary>
internal sealed class CatalogWriter
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public CatalogWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <returns>The index key of each observation, in the same order.</returns>
    public async Task<IReadOnlyList<long>> WriteAsync(long databaseKey, IReadOnlyList<IndexObservation> observations, long observedMs, CancellationToken ct)
    {
        var objects = await LoadObjectsAsync(databaseKey, ct);
        var indexes = await LoadIndexesAsync(databaseKey, ct);
        var keys = new List<long>(observations.Count);
        var touchedObjects = new HashSet<long>();

        foreach (var observation in observations)
        {
            var table = observation.Table;
            var objectIdentity = (table.ObjectId, CreateDate(table.CreateDate));
            if (!objects.TryGetValue(objectIdentity, out var obj))
            {
                var key = await Sql.InsertAsync(
                    _connection, _transaction,
                    """
                    INSERT INTO obj (database_key, object_id, create_date, schema_name, name, type, is_memory_optimized, first_seen_ms, last_seen_ms)
                    VALUES ($db, $id, $created, $schema, $name, $type, $memory, $t, $t)
                    """,
                    ct,
                    ("$db", databaseKey), ("$id", table.ObjectId), ("$created", objectIdentity.Item2),
                    ("$schema", table.Schema), ("$name", table.Name), ("$type", table.Type), ("$memory", table.IsMemoryOptimized), ("$t", observedMs));
                obj = new StoredObject(key, table.Schema, table.Name, table.IsMemoryOptimized);
                objects[objectIdentity] = obj;
            }
            else if (touchedObjects.Add(obj.Key)
                && (obj.Schema != table.Schema || obj.Name != table.Name || obj.IsMemoryOptimized != table.IsMemoryOptimized))
            {
                await Sql.ExecuteAsync(
                    _connection, _transaction,
                    "UPDATE obj SET schema_name = $schema, name = $name, is_memory_optimized = $memory WHERE object_key = $key",
                    ct, ("$schema", table.Schema), ("$name", table.Name), ("$memory", table.IsMemoryOptimized), ("$key", obj.Key));
                objects[objectIdentity] = obj with { Schema = table.Schema, Name = table.Name, IsMemoryOptimized = table.IsMemoryOptimized };
            }

            keys.Add(await IndexKeyAsync(obj.Key, observation, indexes, observedMs, ct));
        }

        // Everything observed now is still there: one statement each rather than a row at a time.
        await Sql.ExecuteAsync(
            _connection, _transaction,
            "UPDATE obj SET last_seen_ms = $t WHERE database_key = $db AND object_key IN (SELECT value FROM json_each($keys))",
            ct, ("$t", observedMs), ("$db", databaseKey), ("$keys", Json(touchedObjects)));
        await Sql.ExecuteAsync(
            _connection, _transaction,
            "UPDATE idx SET last_seen_ms = $t WHERE index_key IN (SELECT value FROM json_each($keys))",
            ct, ("$t", observedMs), ("$keys", Json(keys)));
        return keys;
    }

    private async Task<long> IndexKeyAsync(
        long objectKey, IndexObservation observation, Dictionary<(long, int), StoredIndex> indexes, long observedMs, CancellationToken ct)
    {
        var slot = (objectKey, observation.IndexId);
        if (indexes.TryGetValue(slot, out var existing)
            && existing.Name == observation.IndexName
            && existing.Type == observation.IndexType)
        {
            if (existing.IsPrimaryKey != observation.IsPrimaryKey || existing.IsUnique != observation.IsUnique
                || existing.IsUniqueConstraint != observation.IsUniqueConstraint || existing.IsDisabled != observation.IsDisabled)
            {
                await Sql.ExecuteAsync(
                    _connection, _transaction,
                    """
                    UPDATE idx SET is_primary_key = $pk, is_unique = $uq, is_unique_constraint = $uc, is_disabled = $dis
                    WHERE index_key = $key
                    """,
                    ct,
                    ("$pk", observation.IsPrimaryKey), ("$uq", observation.IsUnique), ("$uc", observation.IsUniqueConstraint),
                    ("$dis", observation.IsDisabled), ("$key", existing.Key));
                indexes[slot] = existing with
                {
                    IsPrimaryKey = observation.IsPrimaryKey,
                    IsUnique = observation.IsUnique,
                    IsUniqueConstraint = observation.IsUniqueConstraint,
                    IsDisabled = observation.IsDisabled,
                };
            }

            return existing.Key;
        }

        var key = await Sql.InsertAsync(
            _connection, _transaction,
            """
            INSERT INTO idx (object_key, index_id, name, type, is_primary_key, is_unique, is_unique_constraint, is_disabled,
                             first_seen_ms, last_seen_ms)
            VALUES ($obj, $id, $name, $type, $pk, $uq, $uc, $dis, $t, $t)
            """,
            ct,
            ("$obj", objectKey), ("$id", observation.IndexId), ("$name", observation.IndexName), ("$type", observation.IndexType),
            ("$pk", observation.IsPrimaryKey), ("$uq", observation.IsUnique), ("$uc", observation.IsUniqueConstraint),
            ("$dis", observation.IsDisabled), ("$t", observedMs));
        indexes[slot] = new StoredIndex(key, observation.IndexName, observation.IndexType, observation.IsPrimaryKey, observation.IsUnique, observation.IsUniqueConstraint, observation.IsDisabled);
        return key;
    }

    private async Task<Dictionary<(int, string), StoredObject>> LoadObjectsAsync(long databaseKey, CancellationToken ct)
    {
        var result = new Dictionary<(int, string), StoredObject>();
        await using var command = Sql.Command(
            _connection, _transaction,
            "SELECT object_key, object_id, create_date, schema_name, name, is_memory_optimized FROM obj WHERE database_key = $db",
            ("$db", databaseKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetInt32(1), reader.GetString(2))] = new StoredObject(reader.GetInt64(0), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
        }

        return result;
    }

    // The most recent index for each (object, index_id) slot.
    private async Task<Dictionary<(long, int), StoredIndex>> LoadIndexesAsync(long databaseKey, CancellationToken ct)
    {
        var result = new Dictionary<(long, int), StoredIndex>();
        await using var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT i.index_key, i.object_key, i.index_id, i.name, i.type, i.is_primary_key, i.is_unique, i.is_unique_constraint, i.is_disabled
            FROM idx AS i JOIN obj AS o ON o.object_key = i.object_key
            WHERE o.database_key = $db
            ORDER BY i.index_key
            """,
            ("$db", databaseKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetInt64(1), reader.GetInt32(2))] = new StoredIndex(
                reader.GetInt64(0),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8));
        }

        return result;
    }

    /// <summary>create_date exactly as reported, as text with milliseconds; part of the table's identity.</summary>
    internal static string CreateDate(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static string Json(IEnumerable<long> keys) => "[" + string.Join(',', keys) + "]";

    private sealed record StoredObject(long Key, string Schema, string Name, bool IsMemoryOptimized);

    private sealed record StoredIndex(long Key, string? Name, string Type, bool IsPrimaryKey, bool IsUnique, bool IsUniqueConstraint, bool IsDisabled);
}
