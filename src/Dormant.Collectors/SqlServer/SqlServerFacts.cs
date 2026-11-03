using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.SqlServer;

internal enum SqlPlatform
{
    SqlServer,
    AmazonRds,
    AzureSqlDatabase,
    AzureSqlManagedInstance,
    Unsupported,
}

/// <summary>What the instance reports about itself; readable by any login, no extra permissions needed.</summary>
internal sealed record SqlServerFacts(
    int EngineEdition,
    string ProductVersion,
    string Edition,
    string ServerName,
    string Login,
    string UserName,
    string CurrentDatabase,
    bool HasRdsAdmin)
{
    /// <summary>The first version Dormant supports: SQL Server 2016 (13.x).</summary>
    public const int MinimumMajorVersion = 13;

    // SERVERPROPERTY('EngineEdition'): 1-4 boxed SQL Server editions, 5 Azure SQL Database, 8 Azure SQL Managed Instance.
    public SqlPlatform Platform => EngineEdition switch
    {
        1 or 2 or 3 or 4 => HasRdsAdmin ? SqlPlatform.AmazonRds : SqlPlatform.SqlServer,
        5 => SqlPlatform.AzureSqlDatabase,
        8 => SqlPlatform.AzureSqlManagedInstance,
        _ => SqlPlatform.Unsupported,
    };

    public int MajorVersion =>
        int.TryParse(ProductVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;

    /// <summary>
    /// Boxed SQL Server and RDS follow the version; Azure SQL reports a fixed 12.x version and is always current.
    /// </summary>
    public bool IsVersionSupported => Platform switch
    {
        SqlPlatform.SqlServer or SqlPlatform.AmazonRds => MajorVersion >= MinimumMajorVersion,
        SqlPlatform.AzureSqlDatabase or SqlPlatform.AzureSqlManagedInstance => true,
        _ => false,
    };

    /// <summary>SQL Server 2022 (16.x) split VIEW SERVER STATE and VIEW DATABASE STATE into narrower permissions.</summary>
    public bool UsesGranularPermissions =>
        Platform is SqlPlatform.SqlServer or SqlPlatform.AmazonRds && MajorVersion >= 16;

    public string PlatformDisplayName => Platform switch
    {
        SqlPlatform.SqlServer => ProductName(),
        SqlPlatform.AmazonRds => $"Amazon RDS for {ProductName()}",
        SqlPlatform.AzureSqlDatabase => "Azure SQL Database",
        SqlPlatform.AzureSqlManagedInstance => "Azure SQL Managed Instance",
        _ => string.Create(CultureInfo.InvariantCulture, $"unsupported engine edition {EngineEdition}"),
    };

    private string ProductName() => MajorVersion switch
    {
        13 => "SQL Server 2016",
        14 => "SQL Server 2017",
        15 => "SQL Server 2019",
        16 => "SQL Server 2022",
        17 => "SQL Server 2025",
        _ => string.Create(CultureInfo.InvariantCulture, $"SQL Server ({MajorVersion}.x)"),
    };

    private const string Query = """
        SELECT
            CAST(SERVERPROPERTY('EngineEdition') AS int),
            CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),
            CAST(SERVERPROPERTY('Edition') AS nvarchar(128)),
            CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)),
            SUSER_SNAME(),
            USER_NAME(),
            DB_NAME(),
            CASE WHEN DB_ID(N'rdsadmin') IS NULL THEN 0 ELSE 1 END;
        """;

    public static async Task<SqlServerFacts> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = SqlServerConnections.CreateCommand(connection, Query);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException("The server returned no row for SERVERPROPERTY.");
        }

        return new SqlServerFacts(
            EngineEdition: reader.GetInt32(0),
            ProductVersion: reader.IsDBNull(1) ? "unknown" : reader.GetString(1),
            Edition: reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
            ServerName: reader.IsDBNull(3) ? "unknown" : reader.GetString(3),
            Login: reader.IsDBNull(4) ? "unknown" : reader.GetString(4),
            UserName: reader.IsDBNull(5) ? "unknown" : reader.GetString(5),
            CurrentDatabase: reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            HasRdsAdmin: reader.GetInt32(7) == 1);
    }
}
