using System.CommandLine;
using Dormant.Core.Usage;
using Dormant.Store;

namespace Dormant.Cli.Commands;

internal static class StatusCommand
{
    public static Command Create(CommonOptions common, TextWriter stdout, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var instanceOption = CommonOptions.Instance();
        var command = new Command(
            "status",
            "Show the last collection, how much of the history was observed, and the restarts, resets and gaps in it. Reads only the local store.")
        {
            config,
            instanceOption,
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

            try
            {
                await using var store = await DormantStore.OpenReadOnlyAsync(loaded.Store.Path, ct);
                if (store is null)
                {
                    await stdout.WriteLineAsync($"No history yet: {loaded.Store.Path} does not exist.");
                    await stdout.WriteLineAsync($"Take the first snapshot with: dormant collect -c {loaded.SourcePath}");
                    return ExitCodes.Ok;
                }

                var now = DateTimeOffset.UtcNow;
                var status = await store.ReadStatusAsync(ct);
                var estate = await store.ReadClassificationAsync(loaded, now, ct);
                var classification = new ClassificationPrinter(stdout, loaded.Classification, new CostModel(loaded.Cost));
                var printer = new StatusPrinter(stdout, now, loaded.Collection.MaxObservedSpan);
                await printer.PrintAsync(status, instances.Select(i => i.Name).ToList(), name => classification.PrintAsync(estate, name));
                return ExitCodes.Ok;
            }
            catch (StoreException ex)
            {
                await stderr.WriteLineAsync(ex.Message);
                return ExitCodes.StoreError;
            }
        });
        return command;
    }
}
