using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Dormant.Cli.Commands;

internal static class VersionCommand
{
    public static Command Create(TextWriter stdout)
    {
        var command = new Command("version", "Show the version of Dormant and the runtime it runs on.");
        command.SetAction(async (_, _) =>
        {
            await stdout.WriteLineAsync($"dormant {InformationalVersion}");
            await stdout.WriteLineAsync($"{RuntimeInformation.FrameworkDescription} on {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})");
            return ExitCodes.Ok;
        });
        return command;
    }

    public static string InformationalVersion =>
        typeof(VersionCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(VersionCommand).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
