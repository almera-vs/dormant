using Dormant.Core;
using Dormant.Cli.Running;
using Dormant.Core.Collection;
using Dormant.Store.History;
using Dormant.Store.Ingestion;

namespace Dormant.Cli.Commands;

/// <summary>Plain-text output for <c>dormant collect</c>: one line per instance, one per database.</summary>
internal sealed class CollectPrinter
{
    private readonly TextWriter _out;

    public CollectPrinter(TextWriter output)
    {
        _out = output;
    }

    public async Task PrintAsync(CollectionOutcome collection)
    {
        if (collection.Error is not null)
        {
            await _out.WriteLineAsync($"{collection.InstanceName}  not collected: {collection.Error}");
            return;
        }

        await PrintAsync(collection.InstanceName, collection.Snapshot!, collection.Ingest!);
        await PrintQueryStoreAsync(collection);
    }

    private async Task PrintQueryStoreAsync(CollectionOutcome collection)
    {
        if (collection.QueryStoreError is { } error)
        {
            await _out.WriteLineAsync($"  Query Store not read: {error}");
            return;
        }

        if (collection.QueryStore.Count == 0)
        {
            return;
        }

        var read = collection.QueryStore.Where(h => h.Error is null && h.State is "READ_WRITE" or "READ_ONLY").ToList();
        var parsed = read.Sum(h => h.Plans.Count(p => p.References is not null));
        var seen = read.Sum(h => h.Plans.Count);
        var line = $"  Query Store: {Text.Plural(read.Count, "database")} read, {Text.Plural(parsed, "new plan")} parsed, {Text.Plural(seen, "plan")} seen running";
        var off = collection.QueryStore.Where(h => h.Error is null && h.State is not ("READ_WRITE" or "READ_ONLY")).Select(h => h.DatabaseName).ToList();
        if (off.Count > 0)
        {
            line += $"; off in {string.Join(", ", off)}";
        }

        await _out.WriteLineAsync(line);
        foreach (var failed in collection.QueryStore.Where(h => h.Error is not null))
        {
            await _out.WriteLineAsync($"  Query Store of {failed.DatabaseName} not read: {failed.Error}");
        }
    }

    private async Task PrintAsync(string instanceName, Snapshot snapshot, IngestResult result)
    {
        var read = result.Databases.Count(d => d.Outcome == DatabaseOutcome.Collected);
        var notRead = result.Databases.Count - read;
        await _out.WriteLineAsync(
            $"{instanceName}  {snapshot.Instance.ServerName}: {Text.Plural(read, "database")} read" +
            (notRead > 0 ? $", {notRead} not read" : string.Empty) +
            $", {Text.Plural(snapshot.IndexCount, "index", "indexes")}, {Text.Plural(snapshot.PartitionCount, "partition")}, " +
            $"{Text.Bytes(snapshot.ReservedBytes)} in {Text.Span(snapshot.Duration)}");

        if (result.Databases.Count == 0)
        {
            await _out.WriteLineAsync("  No database matched the configuration's include and exclude patterns.");
            return;
        }

        var width = result.Databases.Max(d => d.Name.Length);
        foreach (var database in result.Databases)
        {
            await _out.WriteLineAsync($"  {database.Name.PadRight(width)}  {Describe(database)}");
        }
    }

    private static string Describe(DatabaseIngest database) => database.Outcome switch
    {
        DatabaseOutcome.Skipped => $"skipped: {database.Detail}",
        DatabaseOutcome.Failed => $"failed: {database.Detail}",
        _ when database.Interval is null => "first reading; its history starts here",
        _ => Describe(database.Interval),
    };

    private static string Describe(IntervalResult interval)
    {
        var read = interval.Deltas.Count(d => d.UserReads > 0);
        var activity = read == 0 ? "no reads" : $"{Text.Plural(read, "index", "indexes")} read";
        return interval.Kind switch
        {
            IntervalKind.Continuous => $"{Text.Span(interval.ToUtc - interval.FromUtc)} observed, {activity}",
            IntervalKind.InstanceRestart when interval.ObservedFromUtc is { } since =>
                $"engine restarted at {Text.Time(interval.ResetAtUtc!.Value)}; history continues, {Text.Span(interval.ToUtc - since)} observed since, {activity}",
            IntervalKind.InstanceRestart =>
                $"engine restarted at {Text.Time(interval.ResetAtUtc!.Value)}, too long ago to count as observed; {activity}",
            IntervalKind.Reset => $"counters were reset at an unknown time ({interval.Reason}); the last {Text.Span(interval.ToUtc - interval.FromUtc)} are not counted as observed; {activity}",
            _ => $"gap: {interval.Reason}; not counted as observed; {activity}",
        };
    }
}
