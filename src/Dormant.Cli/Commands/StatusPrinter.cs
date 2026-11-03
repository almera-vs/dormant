using Dormant.Core;
using Dormant.Core.History;

namespace Dormant.Cli.Commands;

/// <summary>Plain-text output for <c>dormant status</c>.</summary>
internal sealed class StatusPrinter
{
    private const int MaxEventsShown = 5;

    private readonly TextWriter _out;
    private readonly DateTimeOffset _nowUtc;
    private readonly TimeSpan _maxObservedSpan;

    public StatusPrinter(TextWriter output, DateTimeOffset nowUtc, TimeSpan maxObservedSpan)
    {
        _out = output;
        _nowUtc = nowUtc;
        _maxObservedSpan = maxObservedSpan;
    }

    /// <param name="configured">Instances to show, in configuration order.</param>
    /// <param name="afterInstance">Writes more about an instance after its status, e.g. its classification.</param>
    public async Task PrintAsync(StoreStatus status, IReadOnlyList<string> configured, Func<string, Task>? afterInstance = null)
    {
        await _out.WriteLineAsync($"Store {status.Path}, {Text.Count(status.SizeBytes / 1024)} KB");
        foreach (var name in configured)
        {
            await _out.WriteLineAsync();
            var instance = status.Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (instance is null)
            {
                await _out.WriteLineAsync($"{name}  no collections yet");
                continue;
            }

            await PrintInstanceAsync(instance);
            if (afterInstance is not null)
            {
                await afterInstance(instance.Name);
            }
        }
    }

    private async Task PrintInstanceAsync(InstanceStatus instance)
    {
        var last = instance.LastCollection!;
        await _out.WriteLineAsync(
            $"{instance.Name}  last collection {Text.Time(last.StartedUtc)}: {last.Outcome}" +
            (last.Outcome == "failed"
                ? $": {last.Error}"
                : $", {Text.Plural(last.Databases, "database")}, {Text.Plural(last.Indexes, "index", "indexes")}, {Text.Span(last.Duration)}"));

        if (instance.LastSuccessfulCollection is not { } ok)
        {
            await _out.WriteLineAsync("  No collection has reached this instance yet.");
            return;
        }

        var sinceSuccess = _nowUtc - ok.StartedUtc;
        if (sinceSuccess > _maxObservedSpan)
        {
            await _out.WriteLineAsync($"  No successful collection for {Text.Span(sinceSuccess)}. Until the next one, this time counts as a gap, never as inactivity.");
        }

        if (instance.HistoryStartUtc is not { } start)
        {
            await _out.WriteLineAsync("  No database has been read yet.");
            return;
        }

        await _out.WriteLineAsync(
            $"  history since {Text.Time(start)}: {Text.Percent(instance.Coverage)} observed, " +
            $"{Text.Plural(instance.Restarts.Count, "restart")} stitched, " +
            $"{Text.Plural(instance.Gaps.Count, "gap")}{GapTotal(instance.Gaps)}, " +
            $"{Text.Plural(instance.Resets.Count, "counter reset")}");
        if (instance.LastSyntheticUtc is { } synthetic)
        {
            await _out.WriteLineAsync($"  SYNTHETIC: {Text.Plural(instance.SyntheticCollections, "reading")} up to {Text.Time(synthetic)} were made up (demo backfill), not collected.");
        }

        await PrintDatabasesAsync(instance.Databases);
        await PrintEventsAsync("restarts", instance.Restarts, e => $"{Text.Time(e.AtUtc)}  {e.Database}");
        await PrintEventsAsync("gaps", instance.Gaps, e => $"{Text.Time(e.AtUtc)}  {Text.Span(e.Duration)} without a reading  {e.Database}");
        await PrintEventsAsync("resets", instance.Resets, e => $"{Text.Time(e.AtUtc)}  {Text.Span(e.Duration)} not observed  {e.Database}: {e.Reason}");
    }

    private async Task PrintDatabasesAsync(IReadOnlyList<DatabaseStatus> databases)
    {
        var width = Math.Max("database".Length, databases.Max(d => d.Name.Length));
        await _out.WriteLineAsync();
        await _out.WriteLineAsync($"  {"database".PadRight(width)}  {"history since",-20}  {"last reading",-20}  {"observed",8}  last collection");
        foreach (var d in databases)
        {
            var observed = d.Coverage is null ? "n/a" : Text.Percent(d.Coverage);
            var last = d.LastOutcome switch
            {
                null => string.Empty,
                "collected" => "collected",
                _ => $"{d.LastOutcome}: {d.LastDetail}",
            };
            await _out.WriteLineAsync($"  {d.Name.PadRight(width)}  {Text.Time(d.FirstReadingUtc),-20}  {Text.Time(d.LastReadingUtc),-20}  {observed,8}  {last}");
        }
    }

    private async Task PrintEventsAsync(string title, IReadOnlyList<HistoryEvent> events, Func<HistoryEvent, string> describe)
    {
        if (events.Count == 0)
        {
            return;
        }

        await _out.WriteLineAsync();
        await _out.WriteLineAsync(events.Count > MaxEventsShown ? $"  {title} (latest {MaxEventsShown} of {events.Count})" : $"  {title}");
        foreach (var e in events.Take(MaxEventsShown))
        {
            await _out.WriteLineAsync($"    {describe(e)}");
        }
    }

    private static string GapTotal(IReadOnlyList<HistoryEvent> gaps) =>
        gaps.Count == 0 ? string.Empty : $" ({Text.Span(gaps.Aggregate(TimeSpan.Zero, (sum, g) => sum + g.Duration))} in total)";
}
