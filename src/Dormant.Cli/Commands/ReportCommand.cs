using System.CommandLine;
using System.Globalization;
using System.Text;
using Dormant.Core;
using Dormant.Core.Configuration;
using Dormant.Report;
using Dormant.Store;

namespace Dormant.Cli.Commands;

internal static class ReportCommand
{
    public static Command Create(CommonOptions common, TextWriter stdout, TextWriter stderr)
    {
        var config = CommonOptions.Config();
        var instanceOption = CommonOptions.Instance();
        var since = new Option<string?>("--since")
        {
            Description = "How many days the heatmap shows, ending today, e.g. 90d or 1y. Default: classification.dormant_days. Classes are decided over the configured thresholds whatever this is.",
        };
        var output = new Option<FileInfo?>("--out")
        {
            Description = "Where to write the HTML file. Default: report.out_dir/dormant-report-<UTC time>.html.",
        };
        var command = new Command(
            "report",
            "Write the heatmap report: one self-contained HTML file. Reads only the local store.")
        {
            config,
            since,
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

            if (!TryParseDays(parseResult.GetValue(since), loaded, stderr, out var days))
            {
                return ExitCodes.ConfigOrUsageError;
            }

            var now = DateTimeOffset.UtcNow;
            var path = parseResult.GetValue(output)?.FullName
                ?? Path.Combine(loaded.Report.OutDir, string.Create(CultureInfo.InvariantCulture, $"dormant-report-{now:yyyyMMdd-HHmm}.html"));

            string html;
            try
            {
                var history = await StoreHistory.ReadAsync(loaded, instances, now, days, stderr, ct);
                if (history is null)
                {
                    return ExitCodes.StoreError;
                }

                html = HtmlReport.Render(new ReportContext(history.Value.Estate, history.Value.Status, loaded, VersionCommand.InformationalVersion));
            }
            catch (StoreException ex)
            {
                await stderr.WriteLineAsync(ex.Message);
                return ExitCodes.StoreError;
            }

            try
            {
                await OutputFile.WriteTextAsync(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            }
            catch (Exception ex) when (OutputFile.IsWriteError(ex))
            {
                await stderr.WriteLineAsync($"The report could not be written to {path}: {ex.Message}");
                return ExitCodes.ConfigOrUsageError;
            }

            await stdout.WriteLineAsync($"Report written to {path} ({Text.Bytes(Encoding.UTF8.GetByteCount(html))}, {days} days).");
            return ExitCodes.Ok;
        });
        return command;
    }

    /// <summary>A whole number of days, from one day up to the daily history kept (store.retention.daily).</summary>
    public static bool TryParseDays(string? text, DormantConfig config, TextWriter stderr, out int days)
    {
        days = config.Classification.DormantDays;
        if (text is null)
        {
            return true;
        }

        var limit = (int)config.Store.Retention.Daily.TotalDays;
        if (!Duration.TryParse(text, out var span) || span < TimeSpan.FromDays(1) || span.TotalDays > limit)
        {
            stderr.WriteLine($"--since must be a duration from 1d to {limit}d (the daily history kept), such as 90d or 1y; got '{text}'.");
            return false;
        }

        days = (int)Math.Ceiling(span.TotalDays);
        return true;
    }
}
