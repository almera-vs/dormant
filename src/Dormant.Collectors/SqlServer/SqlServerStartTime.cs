using Dormant.Core.Collection;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

/// <param name="Reported">sqlserver_start_time exactly as reported, in the server's local time; for display only.</param>
/// <param name="Engine">
/// The engine incarnation. Its start time in UTC is derived from the tick counters so that it does not depend on the
/// server's time zone or daylight-saving rules; it is rounded to the second.
/// </param>
internal sealed record SqlServerStartTime(DateTime Reported, EngineIncarnation Engine)
{
    public DateTimeOffset Utc => Engine.StartTimeUtc;

    private const string Query = """
        SELECT sqlserver_start_time, ms_ticks, sqlserver_start_time_ms_ticks, SYSUTCDATETIME()
        FROM sys.dm_os_sys_info;
        """;

    /// <summary>Requires VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server 2022+; VIEW DATABASE STATE on Azure SQL Database).</summary>
    public static async Task<SqlServerStartTime> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = SqlServerConnections.CreateCommand(connection, Query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("sys.dm_os_sys_info returned no row.");
        }

        var reported = reader.GetDateTime(0);
        var msTicks = reader.GetInt64(1);
        var startMsTicks = reader.GetInt64(2);
        var nowUtc = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        var startUtc = nowUtc - TimeSpan.FromMilliseconds(msTicks - startMsTicks);
        startUtc = new DateTimeOffset(startUtc.Ticks - (startUtc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        return new SqlServerStartTime(reported, new EngineIncarnation(startMsTicks, msTicks, startUtc));
    }
}
