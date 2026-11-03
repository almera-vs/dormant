using Dormant.Collectors.SqlServer;
using Dormant.Core.Attribution;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Store;
using Dormant.Store.Ingestion;
using Dormant.Store.Tests;
using Microsoft.Data.Sqlite;

namespace Dormant.Integration;

/// <summary>A store in a temporary directory, fed by real collections from a SQL Server container.</summary>
internal sealed class Collection : IAsyncDisposable
{
    private readonly string _directory;
    private readonly SqlServerInstance _sql;
    private readonly DatabaseFilter _databases;
    private readonly IngestOptions _options;

    private Collection(string directory, SqlServerInstance sql, DatabaseFilter databases, IngestOptions options, DormantStore store)
    {
        _directory = directory;
        _sql = sql;
        _databases = databases;
        _options = options;
        Store = store;
        Inspect = new StoreInspector(Path.Combine(directory, "dormant.db"));
    }

    public DormantStore Store { get; }

    public StoreInspector Inspect { get; }

    public static Task<Collection> StartAsync(SqlServerInstance sql, params string[] databases) =>
        StartAsync(sql, [], databases);

    public static Task<Collection> StartAsync(SqlServerInstance sql, IReadOnlyList<MaintenanceWindow> windows, params string[] databases) =>
        StartAsync(sql, new IngestOptions(TimeSpan.FromHours(1), windows), databases);

    public static async Task<Collection> StartAsync(SqlServerInstance sql, IngestOptions options, params string[] databases)
    {
        var directory = Directory.CreateTempSubdirectory("dormant-it-").FullName;
        var store = await DormantStore.OpenAsync(Path.Combine(directory, "dormant.db"), TestContext.Current.CancellationToken);
        return new Collection(directory, sql, new DatabaseFilter(databases, []), options, store);
    }

    /// <summary>One collection, exactly as <c>dormant collect</c> does it.</summary>
    public async Task<IngestResult> CollectAsync()
    {
        var collector = new SqlServerCollector(_sql.AdminConnectionString, _databases);
        var snapshot = await collector.CollectAsync(TestContext.Current.CancellationToken);
        _lastSnapshot = snapshot;
        return await Store.IngestAsync("test", snapshot, _options, TestContext.Current.CancellationToken);
    }

    /// <summary>One Query Store read for the databases of the last collection, as <c>collect</c> does it after ingesting.</summary>
    public async Task<IReadOnlyList<QueryStoreHarvest>> HarvestAsync(bool storeText)
    {
        var ct = TestContext.Current.CancellationToken;
        var databases = _lastSnapshot!.Databases.Where(d => d.Identity is not null).Select(d => (d.Identity!, d.Name)).ToList();
        var now = DateTimeOffset.UtcNow;
        var requests = await Store.QueryStoreRequestsAsync("test", databases, now, TimeSpan.FromDays(30), ct);
        var harvests = await new SqlServerQueryStoreHarvester(_sql.AdminConnectionString).HarvestAsync(requests, storeText, ct);
        await Store.WriteQueryStoreAsync("test", harvests, now, ct);
        return harvests;
    }

    private Snapshot? _lastSnapshot;

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
