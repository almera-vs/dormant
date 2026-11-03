using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

/// <param name="HasAccess">HAS_DBACCESS for the current login; null when not checked (Azure SQL Database) or unknown.</param>
internal sealed record DatabaseRow(string Name, int DatabaseId, string State, string UserAccess, bool IsAutoClose, bool? HasAccess)
{
    // sys.databases is readable by every login (VIEW ANY DATABASE is granted to public by default), and reading it
    // does not open databases. sys.database_recovery_status is deliberately not read here: it starts databases
    // that are closed (AUTO_CLOSE).
    private const string QueryWithAccess = """
        SELECT d.name, d.database_id, d.state_desc, d.user_access_desc, d.is_auto_close_on, HAS_DBACCESS(d.name)
        FROM sys.databases AS d
        ORDER BY d.name;
        """;

    private const string QueryWithoutAccess = """
        SELECT d.name, d.database_id, d.state_desc, d.user_access_desc, d.is_auto_close_on, CAST(NULL AS int)
        FROM sys.databases AS d
        ORDER BY d.name;
        """;

    public static async Task<IReadOnlyList<DatabaseRow>> ListAsync(SqlConnection connection, bool includeAccess, CancellationToken ct)
    {
        await using var command = SqlServerConnections.CreateCommand(connection, includeAccess ? QueryWithAccess : QueryWithoutAccess);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<DatabaseRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DatabaseRow(
                Name: reader.GetString(0),
                DatabaseId: reader.GetInt32(1),
                State: reader.IsDBNull(2) ? "UNKNOWN" : reader.GetString(2),
                UserAccess: reader.IsDBNull(3) ? "UNKNOWN" : reader.GetString(3),
                IsAutoClose: !reader.IsDBNull(4) && reader.GetBoolean(4),
                HasAccess: reader.IsDBNull(5) ? null : reader.GetInt32(5) == 1));
        }

        return rows;
    }
}
