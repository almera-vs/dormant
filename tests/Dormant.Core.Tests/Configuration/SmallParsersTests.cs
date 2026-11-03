using Dormant.Core.Configuration;

namespace Dormant.Core.Tests.Configuration;

public sealed class DurationTests
{
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("15m", 900)]
    [InlineData("6h", 21600)]
    [InlineData("30d", 2592000)]
    [InlineData(" 5m ", 300)]
    public void Parses_whole_numbers_with_a_unit(string text, long seconds)
    {
        Duration.TryParse(text, out var value).ShouldBeTrue();
        value.ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("15")]
    [InlineData("m")]
    [InlineData("1.5h")]
    [InlineData("-5m")]
    [InlineData("5 m")]
    [InlineData("5M")]
    [InlineData("1w")]
    [InlineData("99999999999999d")]
    public void Rejects_anything_else(string? text) =>
        Duration.TryParse(text, out _).ShouldBeFalse();

    [Theory]
    [InlineData(900, "15m")]
    [InlineData(21600, "6h")]
    [InlineData(2592000, "30d")]
    [InlineData(90, "90s")]
    public void Formats_with_the_largest_whole_unit(long seconds, string expected) =>
        Duration.Format(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
}

public sealed class DatabaseFilterTests
{
    [Theory]
    [InlineData("*", "Sales", true)]
    [InlineData("Sales*", "sales_2021", true)]
    [InlineData("Sales*", "OldSales", false)]
    [InlineData("*_archive", "orders_ARCHIVE", true)]
    [InlineData("db?", "db1", true)]
    [InlineData("db?", "db12", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    public void Matches_glob_patterns_ignoring_case(string pattern, string name, bool expected) =>
        new DatabaseFilter([pattern], []).Matches(name).ShouldBe(expected);

    [Fact]
    public void Exclude_wins_over_include()
    {
        var filter = new DatabaseFilter(["*"], ["tempdb", "*_scratch"]);

        filter.Matches("Sales").ShouldBeTrue();
        filter.Matches("TempDB").ShouldBeFalse();
        filter.Matches("etl_scratch").ShouldBeFalse();
    }

    [Fact]
    public void Default_selects_user_databases_only()
    {
        DatabaseFilter.Default.Matches("ERP").ShouldBeTrue();
        foreach (var system in DatabaseFilter.SystemDatabases)
        {
            DatabaseFilter.Default.Matches(system).ShouldBeFalse();
        }
    }
}

public sealed class SecretResolverTests
{
    [Fact]
    public async Task Reads_environment_variables()
    {
        var resolver = new SecretResolver(name => name == "DB" ? "Server=x" : null);

        (await resolver.ResolveAsync(new EnvironmentSecret("DB"), TestContext.Current.CancellationToken)).ShouldBe("Server=x");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_or_blank_variables_are_unavailable(string? value)
    {
        var resolver = new SecretResolver(_ => value);

        var ex = await Should.ThrowAsync<SecretUnavailableException>(
            () => resolver.ResolveAsync(new EnvironmentSecret("DB"), TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("Environment variable DB is not set or is empty.");
    }

    [Fact]
    public async Task Reads_files_and_trims_trailing_newlines()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Server=x;Password=p\n", TestContext.Current.CancellationToken);

            var value = await new SecretResolver().ResolveAsync(new FileSecret(path), TestContext.Current.CancellationToken);

            value.ShouldBe("Server=x;Password=p");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_files_are_unavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dormant-{Guid.NewGuid():N}.secret");

        var ex = await Should.ThrowAsync<SecretUnavailableException>(
            () => new SecretResolver().ResolveAsync(new FileSecret(path), TestContext.Current.CancellationToken));
        ex.Message.ShouldStartWith($"Secret file {path} could not be read");
    }

    [Fact]
    public void Descriptions_name_the_source_never_the_value()
    {
        new EnvironmentSecret("DORMANT_ERP_PROD").Describe().ShouldBe("env:DORMANT_ERP_PROD");
        new FileSecret("/etc/dormant/erp.secret").Describe().ShouldBe("file:/etc/dormant/erp.secret");
    }
}
