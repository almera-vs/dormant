using System.CommandLine;
using System.Text;
using Dormant.Core.Usage;
using Dormant.Report;
using Dormant.Store;

namespace Dormant.Cli.Commands;

internal static class ExportCommand
{
    public static Command Create(CommonOptions common, TextWriter stdout, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var instanceOption = CommonOptions.Instance();
        var format = new Option<string>("--format")
        {
            Description = "csv or json.",
            Required = true,
        };
        format.AcceptOnlyFromAmong("csv", "json");
        var level = new Option<string>("--level")
        {
            Description = "One row per table, per index, or per partition (each other table counts as one).",
            DefaultValueFactory = _ => "table",
        };
        level.AcceptOnlyFromAmong("table", "index", "partition");
        var output = new Option<FileInfo?>("--out")
        {
            Description = "Write to this file instead of standard output.",
        };
        var command = new Command(
            "export",
            "Export the classification as CSV or JSON, with coverage, last read and write, size and cost. Reads only the local store.")
        {
            config,
            format,
            level,
            instanceOption,
            output,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            using var loggerFactory = CliLogging.Create(parseResult.GetValue(common.LogLevel));
            var loaded = await ConfigFile.LoadAsync(parseResult.GetValue(config)!, stderr, ct);
            if (loaded is null)
            {
                return ExitCodes.ConfigOrUsageError;
            }

            if (!ConfigFile.TrySelectInstances(loaded, parseResult.GetValue(instanceOption), stderr, out var instances))
            {
                return ExitCodes.ConfigOrUsageError;
            }

            var exportLevel = parseResult.GetValue(level) switch
            {
                "index" => ExportLevel.Index,
                "partition" => ExportLevel.Partition,
                _ => ExportLevel.Table,
            };
            var json = parseResult.GetValue(format) == "json";
            var cost = new CostModel(loaded.Cost);

            EstateClassification estate;
            bool synthetic;
            try
            {
                var history = await StoreHistory.ReadAsync(loaded, instances, DateTimeOffset.UtcNow, historyDays: 0, stderr, ct);
                if (history is null)
                {
                    return ExitCodes.StoreError;
                }

                estate = history.Value.Estate;
                synthetic = history.Value.Status.Instances.Any(i => i.SyntheticCollections > 0);
            }
            catch (StoreException ex)
            {
                await stderr.WriteLineAsync(ex.Message);
                return ExitCodes.StoreError;
            }

            async Task WriteAsync(Stream stream)
            {
                if (json)
                {
                    await Export.WriteJsonAsync(stream, estate, exportLevel, cost, loaded.Classification, VersionCommand.InformationalVersion, ct, synthetic);
                    return;
                }

                await using var writer = new StreamWriter(stream, Export.CsvFileEncoding, leaveOpen: true);
                await Export.WriteCsvAsync(writer, estate, exportLevel, cost, ct);
            }

            var path = parseResult.GetValue(output)?.FullName;
            if (path is null)
            {
                // Standard output is text: render, then write it through the console's writer.
                using var buffer = new MemoryStream();
                if (json)
                {
                    await Export.WriteJsonAsync(buffer, estate, exportLevel, cost, loaded.Classification, VersionCommand.InformationalVersion, ct, synthetic);
                    await stdout.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray()));
                }
                else
                {
                    await Export.WriteCsvAsync(stdout, estate, exportLevel, cost, ct);
                }

                return ExitCodes.Ok;
            }

            try
            {
                await OutputFile.WriteAsync(path, WriteAsync, ct);
            }
            catch (Exception ex) when (OutputFile.IsWriteError(ex))
            {
                await stderr.WriteLineAsync($"The export could not be written to {path}: {ex.Message}");
                return ExitCodes.ConfigOrUsageError;
            }

            await stderr.WriteLineAsync($"Export written to {path}.");
            return ExitCodes.Ok;
        });
        return command;
    }
}
