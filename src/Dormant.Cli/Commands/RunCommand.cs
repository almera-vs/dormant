using System.CommandLine;
using Dormant.Cli.Running;
using Dormant.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Dormant.Cli.Commands;

internal static class RunCommand
{
    public static Command Create(CommonOptions common, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var command = new Command(
            "run",
            "Collect continuously on the configured schedule until stopped. This is what the Windows Service and systemd unit run.")
        {
            config,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var loaded = await ConfigFile.LoadAsync(parseResult.GetValue(config)!, stderr, ct);
            if (loaded is null)
            {
                return ExitCodes.ConfigOrUsageError;
            }

            // A long-running service logs its collections unless told otherwise.
            var level = parseResult.GetResult(common.LogLevel) is { Implicit: false }
                ? parseResult.GetValue(common.LogLevel)
                : LogLevel.Information;

            using var host = BuildHost(loaded, level, collectors: null);
            await host.RunAsync(ct);
            return host.Services.GetRequiredService<RunOutcome>().ExitCode;
        });
        return command;
    }

    /// <param name="collectors">Null for the SQL Server collector; tests pass their own.</param>
    /// <param name="time">Null for the system clock; tests pass a fake one.</param>
    public static IHost BuildHost(DormantConfig config, LogLevel level, ICollectorFactory? collectors, TimeProvider? time = null)
    {
        // An empty builder: no appsettings.json, environment or command-line configuration sources that could change
        // behaviour behind the operator's back. Everything comes from the Dormant configuration file.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { ApplicationName = "Dormant" });
        builder.Services.AddWindowsService(options => options.ServiceName = "Dormant");
        builder.Services.AddSystemd();
        builder.Logging.SetMinimumLevel(level);
        builder.Logging.AddJsonConsole(options =>
        {
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.IncludeScopes = true;
        });
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // A Windows Service has no console: its logs go to the Application event log, under the source the installer registers.
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            AddEventLog(builder.Logging);
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton(new SecretResolver());
        if (collectors is null)
        {
            builder.Services.AddSingleton<ICollectorFactory, SqlServerCollectorFactory>();
        }
        else
        {
            builder.Services.AddSingleton(collectors);
        }

        builder.Services.AddSingleton<InstanceCollector>();
        builder.Services.AddSingleton<RunOutcome>();
        builder.Services.AddHostedService<DormantWorker>();
        return builder.Build();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(settings =>
        {
            settings.SourceName = Hosting.WindowsService.EventSource;
            settings.LogName = "Application";
        });
}
