using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

public static class SqlErrors
{
    // 229: permission denied on object; 297: no permission to perform this action;
    // 300: a server/database permission (e.g. VIEW SERVER STATE) was denied;
    // 916: the login cannot access the database under the current security context.
    private static readonly HashSet<int> PermissionDenied = [229, 297, 300, 916];

    /// <summary>The database is a non-readable availability group secondary.</summary>
    internal const int NotReadableSecondary = 976;

    internal static bool IsPermissionDenied(SqlException ex) => PermissionDenied.Contains(ex.Number);

    /// <summary>The server's message and error number; never contains connection details.</summary>
    public static string Describe(SqlException ex) =>
        string.Create(CultureInfo.InvariantCulture, $"{ex.Message.Trim()} (error {ex.Number})");
}

internal static class SqlProbe
{
    /// <summary>Runs a statement that returns at most one row and discards it; used to prove a DMV is readable.</summary>
    public static async Task ReadOneRowAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = SqlServerConnections.CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
        }
    }
}
