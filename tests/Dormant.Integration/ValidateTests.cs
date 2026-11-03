using System.Security.Cryptography;
using Dormant.Collectors.SqlServer;
using Dormant.Core.Configuration;
using Dormant.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dormant.Integration;

/// <summary>
/// Proves the permission model in docs/permissions.md against real SQL Server builds: a login with no grants is
/// told exactly what is missing, and the documented least-privilege grants are sufficient.
/// </summary>
public sealed class ValidateTests
{
    public static TheoryData<string, string, string, string> Versions => new()
    {
        { "mcr.microsoft.com/mssql/server:2019-latest", "SQL Server 2019", "VIEW SERVER STATE", "sqlserver-2016-2019.sql" },
        { "mcr.microsoft.com/mssql/server:2022-latest", "SQL Server 2022", "VIEW SERVER PERFORMANCE STATE", "sqlserver-2022-and-later.sql" },
        { "mcr.microsoft.com/mssql/server:2025-latest", "SQL Server 2025", "VIEW SERVER PERFORMANCE STATE", "sqlserver-2022-and-later.sql" },
    };

    private const string Schema = """
        CREATE DATABASE Sales;
        GO
        ALTER DATABASE Sales SET QUERY_STORE = ON;
        GO
        USE Sales;
        CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL, Total decimal(12, 2) NOT NULL);
        CREATE INDEX IX_Orders_CustomerId ON dbo.Orders (CustomerId);
        INSERT dbo.Orders (Id, CustomerId, Total) VALUES (1, 10, 9.50), (2, 11, 12.00);
        GO
        CREATE DATABASE Archive;
        """;

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Names_each_missing_permission_and_accepts_the_shipped_grant_script(string image, string platform, string serverPermission, string script)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sql = await SqlServerInstance.StartAsync(image, ct);
        await sql.ExecuteAsync(Schema, ct);

        var password = "Dz1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await sql.ExecuteAsync($"CREATE LOGIN dormant WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", ct);
        var instance = Instance("dormant-test");
        var validator = new SqlServerValidator(
            new SecretResolver(_ => sql.ConnectionStringFor("dormant", password)),
            NullLogger<SqlServerValidator>.Instance);

        // No grants: the login can connect, but nothing collection needs is readable.
        var before = await validator.ValidateAsync(instance, ct);

        before.HasFailures.ShouldBeTrue();
        Check(before, "instance", "connect").Status.ShouldBe(CheckStatus.Ok);
        Check(before, "instance", "platform").Detail.ShouldStartWith(platform);
        var serverState = Check(before, "instance", serverPermission);
        serverState.Status.ShouldBe(CheckStatus.Failed);
        serverState.Remedy.ShouldBe($"GRANT {serverPermission} TO [dormant];");
        Check(before, "Sales", "access").Status.ShouldBe(CheckStatus.Failed);
        Check(before, "Archive", "access").Status.ShouldBe(CheckStatus.Failed);

        // The shipped least-privilege script (deploy/sql), exactly as a DBA would run it.
        var grants = Demo.SqlScript.Batches(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", script), ct));
        await sql.ExecuteAsync(string.Join("\nGO\n", grants), ct);

        var after = await validator.ValidateAsync(instance, ct);

        after.Checks.Where(c => c.Status == CheckStatus.Failed).ShouldBeEmpty();
        after.Info.ShouldNotBeNull();
        after.Info.Platform.ShouldBe(platform);
        after.Info.Login.ShouldBe("dormant");
        var started = after.Info.StartTimeUtc.ShouldNotBeNull();
        started.ShouldBeLessThan(DateTimeOffset.UtcNow);
        started.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(-1));
        Check(after, "Sales", "size statistics").Status.ShouldBe(CheckStatus.Ok);
        Check(after, "Sales", "Query Store").Status.ShouldBe(CheckStatus.Ok);
        after.Checks.ShouldNotContain(c => c.Scope == "master" || c.Scope == "msdb");
    }

    [Fact]
    public async Task Skips_offline_and_single_user_databases_without_touching_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sql = await SqlServerInstance.StartAsync("mcr.microsoft.com/mssql/server:2022-latest", ct);
        await sql.ExecuteAsync("""
            CREATE DATABASE Parked;
            GO
            ALTER DATABASE Parked SET OFFLINE;
            GO
            CREATE DATABASE Maintenance;
            GO
            ALTER DATABASE Maintenance SET SINGLE_USER;
            """, ct);
        var validator = new SqlServerValidator(new SecretResolver(_ => sql.AdminConnectionString), NullLogger<SqlServerValidator>.Instance);

        var result = await validator.ValidateAsync(Instance("skip-test"), ct);

        result.HasFailures.ShouldBeFalse();
        Check(result, "Parked", "state").Status.ShouldBe(CheckStatus.Warning);
        Check(result, "Maintenance", "state").Detail.ShouldStartWith("SINGLE_USER");
        result.Checks.ShouldNotContain(c => c.Scope == "Maintenance" && c.Name == "size statistics");
    }

    [Fact]
    public async Task Reports_an_unreachable_instance_as_a_failed_connect()
    {
        var validator = new SqlServerValidator(
            new SecretResolver(_ => "Server=tcp:127.0.0.1,1;User ID=x;Password=y;Connect Timeout=2;TrustServerCertificate=true"),
            NullLogger<SqlServerValidator>.Instance);

        var result = await validator.ValidateAsync(Instance("nowhere"), TestContext.Current.CancellationToken);

        var connect = result.Checks.ShouldHaveSingleItem();
        connect.Name.ShouldBe("connect");
        connect.Status.ShouldBe(CheckStatus.Failed);
        connect.Detail.ShouldNotContain("Password");
        result.Info.ShouldBeNull();
    }

    private static InstanceOptions Instance(string name) => new(
        name,
        new EnvironmentSecret("UNUSED"),
        DatabaseFilter.Default,
        QueryStoreOptions.Default with { Enabled = true },
        []);

    private static CheckResult Check(InstanceValidation result, string scope, string name) =>
        result.Checks.SingleOrDefault(c => c.Scope == scope && c.Name == name)
        ?? throw new ShouldAssertException(
            $"No check '{name}' in scope '{scope}'. Checks:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Checks.Select(c => $"  {c.Status} {c.Scope} {c.Name}: {c.Detail}")));
}
