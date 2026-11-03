using System.CommandLine;
using Dormant.Collectors.SqlServer;
using Dormant.Core.Configuration;
using Dormant.Core.Validation;
using Microsoft.Extensions.Logging;

namespace Dormant.Cli.Commands;

internal static class ValidateCommand
{
    public static Command Create(CommonOptions common, TextWriter stdout, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var command = new Command(
            "validate",
            "Check the configuration, then connectivity and permissions for every instance and database. Collects nothing.")
        {
            config,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            using var loggerFactory = CliLogging.Create(parseResult.GetValue(common.LogLevel));
            var loaded = await ConfigFile.LoadAsync(parseResult.GetValue(config)!, stderr, ct);
            if (loaded is null)
            {
                return ExitCodes.ConfigOrUsageError;
            }

            var printer = new ValidationPrinter(stdout);
            await printer.PrintConfigAsync(loaded);

            var validator = new SqlServerValidator(new SecretResolver(), loggerFactory.CreateLogger<SqlServerValidator>());
            var results = new List<InstanceValidation>();
            foreach (var instance in loaded.Instances)
            {
                var result = await validator.ValidateAsync(instance, ct);
                results.Add(result);
                await printer.PrintInstanceAsync(result);
            }

            await printer.PrintSummaryAsync(results);
            return results.Any(r => r.HasFailures) ? ExitCodes.PartialFailure : ExitCodes.Ok;
        });
        return command;
    }
}
