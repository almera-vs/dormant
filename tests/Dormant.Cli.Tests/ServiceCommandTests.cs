using Dormant.Cli.Hosting;

namespace Dormant.Cli.Tests;

/// <summary>
/// What can be checked without installing anything. Installing, starting and removing the real service is done by the
/// CI workflow on a Windows runner (service-windows job).
/// </summary>
public sealed class ServiceCommandTests
{
    [Fact]
    public void The_service_runs_dormant_run_with_quoted_paths()
    {
        WindowsService.BinPath(@"C:\Program Files\Dormant\dormant.exe", @"C:\ProgramData\Dormant\dormant.yaml")
            .ShouldBe("\"C:\\Program Files\\Dormant\\dormant.exe\" run -c \"C:\\ProgramData\\Dormant\\dormant.yaml\"");
    }

    [Fact]
    public void It_is_created_for_delayed_automatic_start_under_the_virtual_account()
    {
        var arguments = WindowsService.CreateArguments(@"C:\Dormant\dormant.exe", @"C:\ProgramData\Dormant\dormant.yaml", WindowsService.VirtualAccount);

        arguments.ShouldBe(
        [
            "create", "Dormant",
            "binPath=", "\"C:\\Dormant\\dormant.exe\" run -c \"C:\\ProgramData\\Dormant\\dormant.yaml\"",
            "start=", "delayed-auto",
            "obj=", @"NT SERVICE\Dormant",
            "DisplayName=", "Dormant",
        ]);
    }

    [Fact]
    public void It_restarts_after_failures()
    {
        WindowsService.FailureArguments().ShouldBe(["failure", "Dormant", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000"]);
    }

    [Fact]
    public async Task Install_refuses_to_register_anything_but_the_installed_executable()
    {
        // Tests run under the dotnet host, never as dormant.exe, and usually without elevation: either way, nothing is installed.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await DormantCli.RunAsync(["service", "install", "-c", Path.Combine(Path.GetTempPath(), $"dormant-{Guid.NewGuid():N}", "dormant.yaml")], stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(3);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().ShouldMatch(OperatingSystem.IsWindows()
            ? "run this again as administrator|Run this from the installed dormant.exe"
            : "dormant service is for Windows");
    }
}
