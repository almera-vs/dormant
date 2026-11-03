using Dormant.Core.Configuration;
using Dormant.Core.History;
using Dormant.Core.Usage;
using Dormant.Store;

namespace Dormant.Cli.Commands;

/// <summary>What report and export read from the store, limited to the selected instances.</summary>
internal static class StoreHistory
{
    /// <summary>Null, after saying so, when there is no store yet.</summary>
    /// <exception cref="StoreException">The store could not be read.</exception>
    public static async Task<(EstateClassification Estate, StoreStatus Status)?> ReadAsync(
        DormantConfig config, IReadOnlyList<InstanceOptions> instances, DateTimeOffset now, int historyDays,
        TextWriter stderr, CancellationToken ct)
    {
        await using var store = await DormantStore.OpenReadOnlyAsync(config.Store.Path, ct);
        if (store is null)
        {
            await stderr.WriteLineAsync($"No history yet: {config.Store.Path} does not exist.");
            await stderr.WriteLineAsync($"Take the first snapshot with: dormant collect -c {config.SourcePath}");
            return null;
        }

        var names = instances.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var status = await store.ReadStatusAsync(ct);
        var estate = await store.ReadClassificationAsync(config, now, historyDays, ct);
        return (
            estate with
            {
                Tables = estate.Tables.Where(t => names.Contains(t.Instance)).ToList(),
                UnusedIndexes = estate.UnusedIndexes.Where(i => names.Contains(i.Instance)).ToList(),
            },
            status with { Instances = status.Instances.Where(i => names.Contains(i.Name)).ToList() });
    }
}
