using Dormant.Collectors.SqlServer;

namespace Dormant.Collectors.Tests.SqlServer;

public sealed class SqlServerFactsTests
{
    private static SqlServerFacts Facts(int engineEdition, string version, bool rds = false) =>
        new(engineEdition, version, "Enterprise Edition (64-bit)", "sql01", @"CORP\svc_dormant", "dbo", "master", rds);

    [Theory]
    [InlineData(2, "13.0.7037.1", false, "SqlServer", "SQL Server 2016")]
    [InlineData(3, "15.0.4385.2", false, "SqlServer", "SQL Server 2019")]
    [InlineData(4, "17.0.1000.7", false, "SqlServer", "SQL Server 2025")]
    [InlineData(3, "16.0.4135.4", true, "AmazonRds", "Amazon RDS for SQL Server 2022")]
    [InlineData(5, "12.0.2000.8", false, "AzureSqlDatabase", "Azure SQL Database")]
    [InlineData(8, "12.0.2000.8", false, "AzureSqlManagedInstance", "Azure SQL Managed Instance")]
    [InlineData(6, "10.0.0.0", false, "Unsupported", "unsupported engine edition 6")]
    public void Detects_the_platform(int engineEdition, string version, bool rds, string platform, string name)
    {
        var facts = Facts(engineEdition, version, rds);

        facts.Platform.ToString().ShouldBe(platform);
        facts.PlatformDisplayName.ShouldBe(name);
    }

    [Theory]
    [InlineData(3, "12.0.6024.0", false)]
    [InlineData(3, "13.0.7037.1", true)]
    [InlineData(3, "18.0.1.0", true)]
    [InlineData(5, "12.0.2000.8", true)]
    [InlineData(8, "12.0.2000.8", true)]
    [InlineData(6, "16.0.0.0", false)]
    public void Supports_SQL_Server_2016_and_later_and_Azure(int engineEdition, string version, bool supported) =>
        Facts(engineEdition, version).IsVersionSupported.ShouldBe(supported);

    [Theory]
    [InlineData(3, "15.0.4385.2", "VIEW SERVER STATE", "VIEW DATABASE STATE")]
    [InlineData(3, "16.0.4135.4", "VIEW SERVER PERFORMANCE STATE", "VIEW DATABASE PERFORMANCE STATE")]
    [InlineData(8, "12.0.2000.8", "VIEW SERVER STATE", "VIEW DATABASE STATE")]
    [InlineData(5, "12.0.2000.8", null, "VIEW DATABASE STATE")]
    public void Chooses_the_documented_permission_per_platform(int engineEdition, string version, string? server, string database)
    {
        var permissions = new SqlServerPermissions(Facts(engineEdition, version));

        permissions.ServerStatePermission.ShouldBe(server);
        permissions.DatabaseStatePermission.ShouldBe(database);
        permissions.IsPerDatabase.ShouldBe(engineEdition == 5);
    }

    [Fact]
    public void Remedies_name_the_actual_login_safely_quoted()
    {
        var facts = Facts(3, "16.0.4135.4") with { Login = "evil]login" };
        var permissions = new SqlServerPermissions(facts);

        permissions.GrantServerState().ShouldBe("GRANT VIEW SERVER PERFORMANCE STATE TO [evil]]login];");
    }

    [Fact]
    public void Azure_remedies_target_the_database_user()
    {
        var permissions = new SqlServerPermissions(Facts(5, "12.0.2000.8"));

        permissions.GrantDatabaseState("Sales", "dormant").ShouldBe("-- in [Sales]: GRANT VIEW DATABASE STATE TO [dormant];");
        permissions.GrantDefinition("Sales", "dormant").ShouldBe("-- in [Sales]: GRANT VIEW DEFINITION TO [dormant];");
    }
}
