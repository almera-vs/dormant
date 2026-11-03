using System.Text.Json;
using Dormant.Store;
using Dormant.Store.Tests;
using Microsoft.Data.Sqlite;
using static Dormant.Store.Tests.SnapshotBuilder;

namespace Dormant.Cli.Tests;

/// <summary><c>dormant report</c> and <c>dormant export</c> against a small store: files, standard output and exit codes.</summary>
public sealed class ReportCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-report-cli-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private string StorePath => Path.Combine(_directory, "dormant.db");

    private static async Task<(int ExitCode, string Out, string Err)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await DormantCli.RunAsync(args, stdout, stderr, TestContext.Current.CancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> ConfigAsync()
    {
        var path = Path.Combine(_directory, "dormant.yaml");
        await File.WriteAllTextAsync(
            path,
            "version: 1\nstore: { path: dormant.db }\ninstances:\n  - name: erp-prod\n    connection: { env: UNUSED }\n  - name: other\n    connection: { env: UNUSED }\nreport: { out_dir: reports }\n",
            TestContext.Current.CancellationToken);
        return path;
    }

    private async Task SeedAsync()
    {
        await using var store = await DormantStore.OpenAsync(StorePath, TestContext.Current.CancellationToken);
        for (var day = 0; day <= 3; day++)
        {
            var snapshot = new SnapshotBuilder(TimeSpan.FromDays(day))
                .Database("Sales", "guid:6f9619ff-8b86-d011-b42d-00c04fc964ff", 5, Index("Orders", 1, "PK_Orders", seeks: day * 10, type: "CLUSTERED", primaryKey: true))
                .Build();
            await store.IngestAsync("erp-prod", snapshot, new IngestOptions(TimeSpan.FromHours(25), []), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Report_writes_one_html_file_into_the_report_directory()
    {
        var config = await ConfigAsync();
        await SeedAsync();

        var (exitCode, output, error) = await RunAsync("report", "-c", config);

        exitCode.ShouldBe(0, error);
        var file = Directory.GetFiles(Path.Combine(_directory, "reports")).ShouldHaveSingleItem();
        Path.GetFileName(file).ShouldStartWith("dormant-report-");
        output.ShouldContain(file);
        output.ShouldContain("180 days");
        var html = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("Orders");
    }

    [Fact]
    public async Task Report_honours_out_since_and_instance()
    {
        var config = await ConfigAsync();
        await SeedAsync();
        var path = Path.Combine(_directory, "nested", "heatmap.html");

        var (exitCode, output, _) = await RunAsync("report", "-c", config, "--since", "30d", "--instance", "erp-prod", "--out", path);

        exitCode.ShouldBe(0);
        output.ShouldContain("30 days");
        var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        html.ShouldContain("user reads over the last 30 days");
        html.ShouldNotContain("<h3>other</h3>");
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("0d")]
    [InlineData("12h")]
    [InlineData("soon")]
    [InlineData("4y")]
    public async Task Report_rejects_a_window_it_cannot_show(string since)
    {
        var config = await ConfigAsync();

        var (exitCode, _, error) = await RunAsync("report", "-c", config, "--since", since);

        exitCode.ShouldBe(3);
        error.ShouldContain("--since must be a duration from 1d to 1095d");
    }

    [Theory]
    [InlineData("report")]
    [InlineData("export", "--format", "csv")]
    public async Task Without_a_store_report_and_export_say_so_and_exit_with_4(params string[] args)
    {
        var config = await ConfigAsync();

        var (exitCode, _, error) = await RunAsync([.. args, "-c", config]);

        exitCode.ShouldBe(4);
        error.ShouldContain("No history yet");
        File.Exists(StorePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Export_writes_csv_to_standard_output()
    {
        var config = await ConfigAsync();
        await SeedAsync();

        var (exitCode, output, _) = await RunAsync("export", "-c", config, "--format", "csv");

        exitCode.ShouldBe(0);
        var lines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldStartWith("instance,database,schema,table,class,");
        lines.Length.ShouldBe(2);
        lines[1].ShouldStartWith("erp-prod,Sales,dbo,Orders,");
    }

    [Fact]
    public async Task Export_writes_json_to_a_file()
    {
        var config = await ConfigAsync();
        await SeedAsync();
        var path = Path.Combine(_directory, "indexes.json");

        var (exitCode, _, error) = await RunAsync("export", "-c", config, "--format", "json", "--level", "index", "--out", path);

        exitCode.ShouldBe(0);
        error.ShouldContain(path);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("level").GetString().ShouldBe("index");
        json.RootElement.GetProperty("rows")[0].GetProperty("index").GetString().ShouldBe("PK_Orders");
    }

    [Theory]
    [InlineData("--format", "xml")]
    [InlineData("--format", "csv", "--level", "column")]
    [InlineData("--level", "table")]
    public async Task Export_rejects_unknown_formats_and_levels(params string[] args)
    {
        var config = await ConfigAsync();

        var (exitCode, _, _) = await RunAsync(["export", "-c", config, .. args]);

        exitCode.ShouldBe(3);
    }
}
