using Dormant.Collectors.SqlServer;
using Microsoft.Data.SqlClient;

namespace Dormant.Collectors.Tests.SqlServer;

public sealed class SqlServerConnectionsTests
{
    [Fact]
    public void Identifies_every_connection_as_Dormant()
    {
        var prepared = SqlServerConnections.Prepare("Server=sql01;Integrated Security=true;Application Name=Something Else");

        new SqlConnectionStringBuilder(prepared).ApplicationName.ShouldBe("Dormant");
    }

    [Fact]
    public void Keeps_the_users_settings_and_can_target_a_database()
    {
        var prepared = SqlServerConnections.Prepare("Server=sql01;User ID=dormant;Password=p;Encrypt=true;Connect Timeout=7", "Sales");

        var builder = new SqlConnectionStringBuilder(prepared);
        builder.DataSource.ShouldBe("sql01");
        builder.UserID.ShouldBe("dormant");
        builder.ConnectTimeout.ShouldBe(7);
        builder.InitialCatalog.ShouldBe("Sales");
    }

    [Theory]
    [InlineData("Server=x;Password=hunter2;Bogus Keyword=1")]
    [InlineData("Password=hunter2;Server")]
    [InlineData("Server=x;Password='hunter2")]
    public void Malformed_strings_fail_without_revealing_them(string connectionString)
    {
        var ex = Should.Throw<InvalidConnectionStringException>(() => SqlServerConnections.Prepare(connectionString));

        ex.Message.ShouldNotContain("hunter2");
        ex.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Describes_the_target_without_credentials()
    {
        var prepared = SqlServerConnections.Prepare("Server=sql01;User ID=dormant;Password=hunter2", "Sales");

        SqlServerConnections.DescribeTarget(prepared).ShouldBe("sql01/Sales");
    }

    [Theory]
    [InlineData("Sales", "[Sales]")]
    [InlineData("we]ird", "[we]]ird]")]
    [InlineData("a b", "[a b]")]
    public void Quotes_identifiers_like_QUOTENAME(string name, string expected) =>
        SqlServerConnections.QuoteName(name).ShouldBe(expected);

    [Fact]
    public void Quotes_literals_for_generated_statements() =>
        SqlServerConnections.QuoteLiteral("O'Brien").ShouldBe("N'O''Brien'");
}
