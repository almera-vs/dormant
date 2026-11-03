using System.CommandLine;
using Dormant.Cli.Commands;
using Microsoft.Extensions.Logging;

namespace Dormant.Cli;

/// <summary>Builds the command tree and maps outcomes to Dormant's exit codes.</summary>
public static class DormantCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var options = new CommonOptions();
        var root = new RootCommand("Dormant: read-only, restart-proof usage history for SQL Server. Pay for the data you use.");
        root.Options.Add(options.LogLevel);
        root.Subcommands.Add(InitCommand.Create(stdout, stderr));
        root.Subcommands.Add(ValidateCommand.Create(options, stdout, stderr));
        root.Subcommands.Add(CollectCommand.Create(options, stdout, stderr));
        root.Subcommands.Add(StatusCommand.Create(options, stdout, stderr));
        root.Subcommands.Add(ReportCommand.Create(options, stdout, stderr));
        root.Subcommands.Add(ExportCommand.Create(options, stdout, stderr));
        root.Subcommands.Add(RunCommand.Create(options, stderr));
        root.Subcommands.Add(ServiceCommand.Create(stdout, stderr));
        root.Subcommands.Add(VersionCommand.Create(stdout));

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await stderr.WriteLineAsync($"dormant: {error.Message}");
            }

            await stderr.WriteLineAsync("Run 'dormant --help' for usage.");
            return ExitCodes.ConfigOrUsageError;
        }

        var invocation = new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
            EnableDefaultExceptionHandler = false,
        };
        try
        {
            return await parseResult.InvokeAsync(invocation, ct);
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("dormant: stopped before finishing.");
            return ExitCodes.PartialFailure;
        }
        catch (Exception ex)
        {
            // Last-resort handler: an unexpected error is a bug. Print everything needed to report it.
            await stderr.WriteLineAsync($"dormant: unexpected error. Please report it with the details below.{Environment.NewLine}{ex}");
            return ExitCodes.PartialFailure;
        }
    }
}

/// <summary>Options shared by every command.</summary>
internal sealed class CommonOptions
{
    public Option<LogLevel> LogLevel { get; } = new("--log-level")
    {
        Description = "Minimum level of the JSON logs written to standard error: Trace, Debug, Information, Warning, Error, None.",
        DefaultValueFactory = _ => Microsoft.Extensions.Logging.LogLevel.Warning,
        Recursive = true,
    };

    public static Option<FileInfo> Config() => new("--config", "-c")
    {
        Description = "Path to the configuration file.",
        Required = true,
    };

    public static Option<string?> Instance() => new("--instance")
    {
        Description = "Only this instance, by its name in the configuration.",
    };
}
