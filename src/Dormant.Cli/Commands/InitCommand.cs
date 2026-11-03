using System.CommandLine;
using System.Text;
using Dormant.Core.Configuration;

namespace Dormant.Cli.Commands;

internal static class InitCommand
{
    public static Command Create(TextWriter stdout, TextWriter stderr)
    {
        var output = new Option<FileInfo>("--output", "-o")
        {
            Description = "Where to write the example configuration.",
            DefaultValueFactory = _ => new FileInfo("dormant.yaml"),
        };
        var force = new Option<bool>("--force")
        {
            Description = "Overwrite the file if it already exists.",
        };

        var command = new Command("init", "Write a commented example configuration.") { output, force };
        command.SetAction(async (parseResult, ct) =>
        {
            var file = parseResult.GetValue(output)!;
            if (file.Exists && !parseResult.GetValue(force))
            {
                await stderr.WriteLineAsync($"{file.FullName} already exists; nothing was written. Use --force to overwrite it.");
                return ExitCodes.ConfigOrUsageError;
            }

            file.Directory?.Create();
            await File.WriteAllTextAsync(file.FullName, ExampleConfig.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            await stdout.WriteLineAsync($"Wrote an example configuration to {file.FullName}.");
            await stdout.WriteLineAsync("Set the connection details for each instance, then check them with:");
            await stdout.WriteLineAsync($"  dormant validate -c {file.Name}");
            return ExitCodes.Ok;
        });
        return command;
    }
}
