using Microsoft.Data.Sqlite;

namespace Dormant.Store.Ingestion;

/// <summary>Small helpers over SqliteCommand for the store's own, fixed statements.</summary>
internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static async Task<long> InsertAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql + " RETURNING rowid;", parameters);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    public static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<long?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static long Ms(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    public static long? Ms(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    public static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));
}
