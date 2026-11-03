using System.Diagnostics;
using Dormant.Collectors.SqlServer;
using Dormant.Core.Attribution;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Store;
using Dormant.Store.Ingestion;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Dormant.Cli.Running;

/// <summary>Creates the collector for an instance; tests substitute their own.</summary>
public interface ICollectorFactory
{
    ICollector Create(string connectionString, InstanceOptions instance);

    IQueryStoreHarvester CreateHarvester(string connectionString);
}

internal sealed class SqlServerCollectorFactory : ICollectorFactory
{
    private readonly ILoggerFactory _loggers;

    public SqlServerCollectorFactory(ILoggerFactory loggers)
    {
        _loggers = loggers;
    }

    public ICollector Create(string connectionString, InstanceOptions instance) =>
        new SqlServerCollector(connectionString, instance.Databases, _loggers.CreateLogger<SqlServerCollector>());

    public IQueryStoreHarvester CreateHarvester(string connectionString) =>
        new SqlServerQueryStoreHarvester(connectionString, _loggers.CreateLogger<SqlServerQueryStoreHarvester>());
}

/// <summary>The outcome of one collection from one instance.</summary>
/// <param name="Snapshot">What was read; null when the instance could not be reached.</param>
/// <param name="Ingest">What it added to the history; null when the instance could not be reached.</param>
/// <param name="Error">Why the instance could not be reached.</param>
public sealed record CollectionOutcome(string InstanceName, Snapshot? Snapshot, IngestResult? Ingest, string? Error)
{
    /// <summary>What Query Store reading found per database; empty when it is not enabled for the instance.</summary>
    public IReadOnlyList<QueryStoreHarvest> QueryStore { get; init; } = [];

    /// <summary>Why Query Store could not be read at all this time; attribution is optional and never fails a collection.</summary>
    public string? QueryStoreError { get; init; }

    /// <summary>The instance and every selected database were read.</summary>
    public bool IsComplete => Error is null && Ingest!.Databases.All(d => d.Outcome != DatabaseOutcome.Failed);
}

/// <summary>
/// One collection from one instance into the store, as both <c>collect</c> and <c>run</c> do it. A failure to reach the
/// instance is recorded in the store and returned; a store failure is thrown (<see cref="StoreException"/>).
/// </summary>
public sealed class InstanceCollector
{
    private readonly ICollectorFactory _collectors;
    private readonly SecretResolver _secrets;

    public InstanceCollector(ICollectorFactory collectors, SecretResolver secrets)
    {
        _collectors = collectors;
        _secrets = secrets;
    }

    public async Task<CollectionOutcome> CollectAsync(DormantConfig config, InstanceOptions instance, DormantStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(store);

        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connectionString = await _secrets.ResolveAsync(instance.Connection, ct);
            var snapshot = await _collectors.Create(connectionString, instance).CollectAsync(ct);
            var ingest = await store.IngestAsync(instance.Name, snapshot, IngestOptions.For(config, instance), ct);
            var outcome = new CollectionOutcome(instance.Name, snapshot, ingest, null);
            return instance.QueryStore.Enabled ? await AttributeAsync(outcome, connectionString, instance, store, ct) : outcome;
        }
        catch (Exception ex) when (ex is SqlException or SecretUnavailableException or InvalidConnectionStringException or CollectionException)
        {
            var message = ex is SqlException sql ? SqlErrors.Describe(sql) : ex.Message;
            await store.RecordFailureAsync(instance.Name, startedUtc, stopwatch.Elapsed, message, ct);
            return new CollectionOutcome(instance.Name, null, null, message);
        }
    }

    /// <summary>Reads Query Store for the databases just collected (decision D-053).</summary>
    private async Task<CollectionOutcome> AttributeAsync(
        CollectionOutcome outcome, string connectionString, InstanceOptions instance, DormantStore store, CancellationToken ct)
    {
        var databases = outcome.Snapshot!.Databases
            .Where(d => d.Outcome == DatabaseOutcome.Collected && d.Identity is not null)
            .Select(d => (d.Identity!, d.Name))
            .ToList();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var requests = await store.QueryStoreRequestsAsync(instance.Name, databases, now, instance.QueryStore.Lookback, ct);
            var harvests = await _collectors.CreateHarvester(connectionString).HarvestAsync(requests, instance.QueryStore.StoreText, ct);
            await store.WriteQueryStoreAsync(instance.Name, harvests, now, ct);
            return outcome with { QueryStore = harvests };
        }
        catch (SqlException ex)
        {
            return outcome with { QueryStoreError = SqlErrors.Describe(ex) };
        }
    }
}
