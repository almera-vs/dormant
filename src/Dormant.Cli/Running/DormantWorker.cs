using Dormant.Core.Configuration;
using Dormant.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dormant.Cli.Running;

/// <summary>The exit code <c>run</c> returns once the host stops.</summary>
public sealed class RunOutcome
{
    public int ExitCode { get; set; } = ExitCodes.Ok;
}

/// <summary>
/// The long-running collector behind <c>dormant run</c> and the services. Each instance has its own loop: collect,
/// then wait for the next scheduled time. A separate loop prunes old history. An unreachable instance is recorded and
/// retried at the next time; a store failure stops the service with exit code 4.
/// </summary>
public sealed partial class DormantWorker : BackgroundService
{
    /// <summary>How often old history is pruned.</summary>
    public static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private readonly DormantConfig _config;
    private readonly InstanceCollector _collector;
    private readonly TimeProvider _time;
    private readonly RunOutcome _outcome;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DormantWorker> _logger;

    public DormantWorker(
        DormantConfig config,
        InstanceCollector collector,
        TimeProvider time,
        RunOutcome outcome,
        IHostApplicationLifetime lifetime,
        ILogger<DormantWorker> logger)
    {
        _config = config;
        _collector = collector;
        _time = time;
        _outcome = outcome;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DormantStore store;
        try
        {
            store = await DormantStore.OpenAsync(_config.Store.Path, stoppingToken);
        }
        catch (StoreException ex)
        {
            StoreFailed(ex);
            return;
        }

        await using (store)
        {
            var interval = Duration.Format(_config.Collection.Interval);
            LogStarted(_logger, _config.Instances.Count, interval, store.Path);
            var loops = _config.Instances.Select(instance => CollectLoopAsync(instance, store, stoppingToken)).ToList();
            loops.Add(PruneLoopAsync(store, stoppingToken));
            try
            {
                await Task.WhenAll(loops);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                LogStopped(_logger);
            }
        }
    }

    private async Task CollectLoopAsync(InstanceOptions instance, DormantStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _collector.CollectAsync(_config, instance, store, ct);
                Log(result);
            }
            catch (StoreException ex)
            {
                StoreFailed(ex);
                return;
            }

            var now = _time.GetUtcNow();
            var next = CollectionSchedule.Next(now, _config.Collection.Interval, instance.MaintenanceWindows);
            await Task.Delay(next - now, _time, ct);
        }
    }

    private async Task PruneLoopAsync(DormantStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pruned = await store.PruneAsync(_time.GetUtcNow(), _config.Store.Retention, ct);
                LogPruned(_logger, pruned.RawIntervals, pruned.HourlyRows, pruned.DailyRows);
            }
            catch (StoreException ex)
            {
                StoreFailed(ex);
                return;
            }

            await Task.Delay(PruneEvery, _time, ct);
        }
    }

    private void StoreFailed(StoreException ex)
    {
        LogStoreFailed(_logger, ex);
        _outcome.ExitCode = ExitCodes.StoreError;
        _lifetime.StopApplication();
    }

    private void Log(CollectionOutcome result)
    {
        if (result.Error is not null)
        {
            LogUnreachable(_logger, result.InstanceName, result.Error);
            return;
        }

        foreach (var database in result.Ingest!.Databases.Where(d => d.Outcome != Core.Collection.DatabaseOutcome.Collected))
        {
            var outcome = database.Outcome.ToString();
            LogDatabaseNotRead(_logger, result.InstanceName, database.Name, outcome, database.Detail);
        }

        foreach (var database in result.Ingest.Databases.Where(d => d.Interval is { Kind: not Store.History.IntervalKind.Continuous }))
        {
            var kind = database.Interval!.Kind.ToString();
            LogHistoryEvent(_logger, result.InstanceName, database.Name, kind, database.Interval.Reason);
        }

        LogCollected(
            _logger,
            result.InstanceName,
            result.Ingest.Databases.Count,
            result.Snapshot!.IndexCount,
            result.Snapshot.PartitionCount,
            (long)result.Snapshot.Duration.TotalMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dormant is running: {Instances} instances, a snapshot every {Interval}, store {Store}")]
    private static partial void LogStarted(ILogger logger, int instances, string interval, string store);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dormant stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Collected {Instance}: {Databases} databases, {Indexes} indexes, {Partitions} partitions in {DurationMs} ms")]
    private static partial void LogCollected(ILogger logger, string instance, int databases, int indexes, int partitions, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not collect from {Instance}: {Error}. Trying again at the next scheduled time; the time in between will show as a gap.")]
    private static partial void LogUnreachable(ILogger logger, string instance, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Instance} database {Database} was not read ({Outcome}): {Detail}")]
    private static partial void LogDatabaseNotRead(ILogger logger, string instance, string database, string outcome, string? detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Instance} database {Database}: {Kind} ({Reason})")]
    private static partial void LogHistoryEvent(ILogger logger, string instance, string database, string kind, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned history: {Raw} raw intervals, {Hourly} hourly rows, {Daily} daily rows")]
    private static partial void LogPruned(ILogger logger, int raw, int hourly, int daily);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The store failed; Dormant is stopping")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);
}
