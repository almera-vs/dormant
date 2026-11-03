using System.CommandLine;
using Dormant.Cli.Running;
using Dormant.Core.Configuration;
using Dormant.Store;

namespace Dormant.Cli.Commands;

internal static class CollectCommand
{
    public static Command Create(CommonOptions common, TextWriter stdout, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var instanceOption = CommonOptions.Instance();
        var once = new Option<bool>("--once")
        {
            Description = "Take one snapshot of every instance and exit. This is what collect always does; 'dormant run' collects continuously.",
        };

        var command = new Command("collect", "Take one snapshot of usage counters from every instance and add it to the history.")
        {
            config,
            instanceOption,
            once,
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
                await using var store = await DormantStore.OpenAsync(loaded.Store.Path, ct);
                var collector = new InstanceCollector(new SqlServerCollectorFactory(loggerFactory), new SecretResolver());
                var printer = new CollectPrinter(stdout);
                var allCollected = true;
                foreach (var instance in instances)
                {
                    var result = await collector.CollectAsync(loaded, instance, store, ct);
                    await printer.PrintAsync(result);
                    allCollected &= result.IsComplete;
                }

                return allCollected ? ExitCodes.Ok : ExitCodes.PartialFailure;
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
