using Dormant.Core.Collection;
using Dormant.Store.History;
using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>
/// Maps observed partitions to stable keys by the range of values they hold, so that a SPLIT or MERGE that renumbers
/// partitions leaves the others' histories intact, and records sizes as they change.
/// </summary>
internal sealed class PartitionWriter
{
    private const long MsPerDay = 86_400_000;

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    public PartitionWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <param name="indexKeys">The index key of each observation, in the same order.</param>
    /// <returns>Each partition's reading by partition key.</returns>
    public async Task<Dictionary<long, PartitionReading>> WriteAsync(
        long databaseKey, IReadOnlyList<IndexObservation> observations, IReadOnlyList<long> indexKeys, long observedMs, CancellationToken ct)
    {
        var stored = await LoadAsync(databaseKey, ct);
        var readings = new Dictionary<long, PartitionReading>();
        var day = observedMs / MsPerDay;

        for (var i = 0; i < observations.Count; i++)
        {
            foreach (var partition in observations[i].Partitions)
            {
                var range = partition.Range;
                var slot = (indexKeys[i], range.Key);
                long key;
                if (!stored.TryGetValue(slot, out var existing))
                {
                    key = await Sql.InsertAsync(
                        _connection, _transaction,
                        """
                        INSERT INTO part (index_key, range_key, label, range_text, partition_number, partition_id, row_count,
                                          reserved_pages, used_pages, compression, first_seen_ms, last_seen_ms)
                        VALUES ($index, $range, $label, $text, $number, $id, $rows, $reserved, $used, $compression, $t, $t)
                        """,
                        ct,
                        ("$index", indexKeys[i]), ("$range", range.Key), ("$label", range.Label), ("$text", range.Description),
                        ("$number", partition.PartitionNumber), ("$id", partition.PartitionId), ("$rows", partition.RowCount),
                        ("$reserved", partition.ReservedPages), ("$used", partition.UsedPages), ("$compression", partition.Compression),
                        ("$t", observedMs));
                    await RecordSizeAsync(key, day, partition, ct);
                }
                else
                {
                    key = existing.Key;
                    var sizeChanged = existing.RowCount != partition.RowCount || existing.ReservedPages != partition.ReservedPages
                        || existing.UsedPages != partition.UsedPages;
                    if (sizeChanged || existing.PartitionNumber != partition.PartitionNumber || existing.PartitionId != partition.PartitionId
                        || existing.Compression != partition.Compression || existing.Label != range.Label || existing.Text != range.Description)
                    {
                        await Sql.ExecuteAsync(
                            _connection, _transaction,
                            """
                            UPDATE part SET label = $label, range_text = $text, partition_number = $number, partition_id = $id,
                                            row_count = $rows, reserved_pages = $reserved, used_pages = $used, compression = $compression
                            WHERE partition_key = $key
                            """,
                            ct,
                            ("$label", range.Label), ("$text", range.Description), ("$number", partition.PartitionNumber),
                            ("$id", partition.PartitionId), ("$rows", partition.RowCount), ("$reserved", partition.ReservedPages),
                            ("$used", partition.UsedPages), ("$compression", partition.Compression), ("$key", key));
                    }

                    if (sizeChanged)
                    {
                        await RecordSizeAsync(key, day, partition, ct);
                    }
                }

                readings[key] = new PartitionReading(partition.PartitionId, partition.Operations);
            }
        }

        await Sql.ExecuteAsync(
            _connection, _transaction,
            "UPDATE part SET last_seen_ms = $t WHERE partition_key IN (SELECT value FROM json_each($keys))",
            ct, ("$t", observedMs), ("$keys", "[" + string.Join(',', readings.Keys) + "]"));
        return readings;
    }

    private Task<int> RecordSizeAsync(long partitionKey, long day, PartitionObservation partition, CancellationToken ct) =>
        Sql.ExecuteAsync(
            _connection, _transaction,
            """
            INSERT OR REPLACE INTO part_size (partition_key, day, row_count, reserved_pages, used_pages)
            VALUES ($key, $day, $rows, $reserved, $used)
            """,
            ct,
            ("$key", partitionKey), ("$day", day), ("$rows", partition.RowCount),
            ("$reserved", partition.ReservedPages), ("$used", partition.UsedPages));

    private async Task<Dictionary<(long, string), StoredPartition>> LoadAsync(long databaseKey, CancellationToken ct)
    {
        var result = new Dictionary<(long, string), StoredPartition>();
        await using var command = Sql.Command(
            _connection, _transaction,
            """
            SELECT p.partition_key, p.index_key, p.range_key, p.label, p.range_text, p.partition_number, p.partition_id,
                   p.row_count, p.reserved_pages, p.used_pages, p.compression
            FROM part AS p
            JOIN idx AS i ON i.index_key = p.index_key
            JOIN obj AS o ON o.object_key = i.object_key
            WHERE o.database_key = $db
            """,
            ("$db", databaseKey));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetInt64(1), reader.GetString(2))] = new StoredPartition(
                reader.GetInt64(0), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6),
                reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetString(10));
        }

        return result;
    }

    private sealed record StoredPartition(
        long Key, string Label, string Text, int PartitionNumber, long PartitionId, long RowCount, long ReservedPages, long UsedPages, string Compression);
}
