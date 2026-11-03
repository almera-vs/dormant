namespace Dormant.Demo.Tests;

public sealed class SqlScriptTests
{
    [Fact]
    public void Variables_are_replaced_and_batches_split_on_go()
    {
        var batches = SqlScript.Batches(":setvar Who \"dormant\"\nGRANT X TO [$(Who)];\nGO\n\nSELECT 1;\ngo\n");

        batches.ShouldBe(["GRANT X TO [dormant];", "SELECT 1;"]);
    }

    [Fact]
    public void Overrides_win_over_setvar_lines()
    {
        SqlScript.Batches(":setvar Who \"dormant\"\nGRANT X TO [$(Who)];", new Dictionary<string, string> { ["Who"] = "svc" })
            .ShouldBe(["GRANT X TO [svc];"]);
    }

    [Fact]
    public void An_unset_variable_is_an_error_not_an_empty_name()
    {
        Should.Throw<InvalidOperationException>(() => SqlScript.Batches("GRANT X TO [$(Who)];"));
    }

    [Theory]
    [InlineData("sqlserver-2022-and-later.sql", "GRANT VIEW SERVER PERFORMANCE STATE TO [dormant];")]
    [InlineData("sqlserver-2016-2019.sql", "GRANT VIEW SERVER STATE TO [dormant];")]
    [InlineData("azure-sql-database.sql", "GRANT VIEW DATABASE STATE TO [dormant];")]
    public void The_shipped_scripts_grant_read_only_permissions_to_the_named_login(string script, string grant)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sql", script));

        var sql = string.Join("\n", SqlScript.Batches(text));

        sql.ShouldContain(grant);
        sql.ShouldNotContain("$(");
        // Read-only: nothing that writes, alters or controls.
        foreach (var forbidden in new[] { "ALTER ANY", "CONTROL", "db_owner", "sysadmin", "INSERT", "UPDATE", "DELETE", "EXECUTE" })
        {
            sql.ToUpperInvariant().ShouldNotContain(forbidden.ToUpperInvariant(), Case.Sensitive, script);
        }
    }
}
