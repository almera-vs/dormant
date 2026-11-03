using Dormant.Core.Configuration;

namespace Dormant.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-cli-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await DormantCli.RunAsync(args, stdout, stderr, TestContext.Current.CancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Version_prints_the_version_and_succeeds()
    {
        var (exitCode, output, _) = await RunAsync("version");

        exitCode.ShouldBe(0);
        output.ShouldStartWith("dormant 0.1.0");
    }

    [Fact]
    public async Task Help_succeeds()
    {
        var (exitCode, output, _) = await RunAsync("--help");

        exitCode.ShouldBe(0);
        output.ShouldContain("validate");
    }

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("validate")]
    [InlineData("validate", "-c")]
    [InlineData("init", "--nonsense")]
    public async Task Usage_errors_exit_with_3(params string[] args)
    {
        var (exitCode, _, error) = await RunAsync(args);

        exitCode.ShouldBe(3);
        error.ShouldContain("dormant --help");
    }

    [Fact]
    public async Task Init_writes_a_configuration_that_loads()
    {
        var path = Path.Combine(_directory, "dormant.yaml");

        var (exitCode, output, _) = await RunAsync("init", "-o", path);

        exitCode.ShouldBe(0);
        output.ShouldContain(path);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(ExampleConfig.Text);
        (await ConfigLoader.LoadAsync(path, TestContext.Current.CancellationToken)).Instances.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Init_never_overwrites_without_force()
    {
        var path = Path.Combine(_directory, "dormant.yaml");
        await File.WriteAllTextAsync(path, "mine", TestContext.Current.CancellationToken);

        var (exitCode, _, error) = await RunAsync("init", "-o", path);

        exitCode.ShouldBe(3);
        error.ShouldContain("already exists");
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe("mine");

        (await RunAsync("init", "-o", path, "--force")).ExitCode.ShouldBe(0);
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).ShouldBe(ExampleConfig.Text);
    }

    [Fact]
    public async Task Validate_reports_configuration_errors_with_exit_code_3()
    {
        var path = Path.Combine(_directory, "bad.yaml");
        await File.WriteAllTextAsync(path, "version: 1\nstore: { path: x.db }\ninstances: []\n", TestContext.Current.CancellationToken);

        var (exitCode, output, error) = await RunAsync("validate", "-c", path);

        exitCode.ShouldBe(3);
        output.ShouldBeEmpty();
        error.ShouldContain($"{path}:3:12: instances: is empty; list at least one SQL Server instance");
    }

    private async Task<string> WriteConfigAsync(string storePath, string variable)
    {
        var path = Path.Combine(_directory, "dormant.yaml");
        await File.WriteAllTextAsync(
            path,
            $"version: 1\nstore: {{ path: '{storePath}' }}\ninstances:\n  - name: nowhere\n    connection: {{ env: {variable} }}\n",
            TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task Status_without_a_store_says_so_and_creates_nothing()
    {
        var store = Path.Combine(_directory, "dormant.db");
        var config = await WriteConfigAsync(store, "UNUSED");

        var (exitCode, output, _) = await RunAsync("status", "-c", config);

        exitCode.ShouldBe(0);
        output.ShouldContain("No history yet");
        File.Exists(store).ShouldBeFalse();
    }

    [Fact]
    public async Task Collect_records_an_unreachable_instance_as_a_partial_failure_that_status_shows()
    {
        var variable = $"DORMANT_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "Server=tcp:127.0.0.1,1;User ID=x;Password=hunter2;Connect Timeout=2;TrustServerCertificate=true");
        try
        {
            var config = await WriteConfigAsync(Path.Combine(_directory, "dormant.db"), variable);

            var (exitCode, output, _) = await RunAsync("collect", "-c", config, "--once", "--log-level", "None");

            exitCode.ShouldBe(1);
            output.ShouldStartWith("nowhere  not collected:");
            output.ShouldNotContain("hunter2");

            var (statusCode, status, _) = await RunAsync("status", "-c", config);
            statusCode.ShouldBe(0);
            status.ShouldContain("nowhere  last collection");
            status.ShouldContain(": failed: ");
            status.ShouldContain("No collection has reached this instance yet.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Collect_reports_an_unusable_store_with_exit_code_4()
    {
        var config = await WriteConfigAsync(_directory, "UNUSED");

        var (exitCode, _, error) = await RunAsync("collect", "-c", config);

        exitCode.ShouldBe(4);
        error.ShouldContain("could not be opened");
    }

    [Fact]
    public async Task Collect_rejects_an_unknown_instance_name()
    {
        var config = await WriteConfigAsync(Path.Combine(_directory, "dormant.db"), "UNUSED");

        var (exitCode, _, error) = await RunAsync("collect", "-c", config, "--instance", "erp-prod");

        exitCode.ShouldBe(3);
        error.ShouldContain("No instance named 'erp-prod'");
    }

    [Fact]
    public async Task Validate_reports_an_unavailable_secret_as_a_partial_failure()
    {
        var path = Path.Combine(_directory, "dormant.yaml");
        var variable = $"DORMANT_TEST_{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(
            path,
            $"version: 1\nstore: {{ path: x.db }}\ninstances:\n  - name: a\n    connection: {{ env: {variable} }}\n",
            TestContext.Current.CancellationToken);

        var (exitCode, output, _) = await RunAsync("validate", "-c", path, "--log-level", "None");

        exitCode.ShouldBe(1);
        output.ShouldContain($"Environment variable {variable} is not set or is empty.");
        output.ShouldContain("1 of 1 instance needs attention: a.");
    }
}
