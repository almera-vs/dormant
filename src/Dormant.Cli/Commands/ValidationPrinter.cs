using System.Globalization;
using Dormant.Core.Configuration;
using Dormant.Core.Validation;

namespace Dormant.Cli.Commands;

/// <summary>Plain-text output for <c>dormant validate</c>: aligned columns, failures followed by the statement that fixes them.</summary>
internal sealed class ValidationPrinter
{
    private readonly TextWriter _out;

    public ValidationPrinter(TextWriter output)
    {
        _out = output;
    }

    public async Task PrintConfigAsync(DormantConfig config)
    {
        var instances = config.Instances.Count == 1 ? "1 instance" : $"{config.Instances.Count} instances";
        await _out.WriteLineAsync($"Configuration {config.SourcePath} is valid: {instances}, snapshot every {Duration.Format(config.Collection.Interval)}.");
        await _out.WriteLineAsync($"Store: {config.Store.Path}");
    }

    public async Task PrintInstanceAsync(InstanceValidation result)
    {
        await _out.WriteLineAsync();
        await _out.WriteLineAsync(result.Info switch
        {
            null => result.InstanceName,
            { StartTimeUtc: { } started } => string.Create(CultureInfo.InvariantCulture, $"{result.InstanceName}  {result.Info.ServerName}, started {started:yyyy-MM-dd HH:mm} UTC"),
            _ => $"{result.InstanceName}  {result.Info.ServerName}",
        });

        var scopeWidth = result.Checks.Max(c => c.Scope.Length);
        var nameWidth = result.Checks.Max(c => c.Name.Length);
        var indent = new string(' ', 2 + 9 + scopeWidth + 2 + nameWidth + 2);
        foreach (var check in result.Checks)
        {
            await _out.WriteLineAsync($"  {Label(check.Status),-7}  {check.Scope.PadRight(scopeWidth)}  {check.Name.PadRight(nameWidth)}  {check.Detail}");
            if (check.Remedy is not null && check.Status != CheckStatus.Ok)
            {
                await _out.WriteLineAsync($"{indent}fix: {check.Remedy}");
            }
        }
    }

    public async Task PrintSummaryAsync(IReadOnlyList<InstanceValidation> results)
    {
        await _out.WriteLineAsync();
        var failing = results.Where(r => r.HasFailures).Select(r => r.InstanceName).ToList();
        var warnings = results.Sum(r => r.Checks.Count(c => c.Status == CheckStatus.Warning));
        if (failing.Count == 0)
        {
            await _out.WriteLineAsync(warnings == 0
                ? "Every instance is ready for collection."
                : $"Every instance is ready for collection. {Plural(warnings, "warning")} above limit what Dormant can see.");
            return;
        }

        await _out.WriteLineAsync(
            $"{failing.Count} of {Plural(results.Count, "instance")} {(failing.Count == 1 ? "needs" : "need")} attention: {string.Join(", ", failing)}. " +
            "Apply the fixes shown above, then run validate again.");
    }

    private static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "ok",
        CheckStatus.Warning => "warning",
        _ => "failed",
    };

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : string.Create(CultureInfo.InvariantCulture, $"{count} {noun}s");
}
