using System.Security.Cryptography;
using Dormant.Collectors.SqlServer;
using Dormant.Core.Configuration;
using Dormant.Demo;
using Dormant.Store;
using Microsoft.Data.Sqlite;

namespace Dormant.Integration;

/// <summary>
/// The demo against a real SQL Server. After its setup (database, least-privilege login from the
/// shipped script, restart, 200 days of synthetic history) and a real workload, Dormant finds the planted cold and
/// dormant partitions, the dormant tables and the unused index, and nothing else. Its own container: setup restarts it.
/// </summary>
public sealed class DemoTests
{
    private sealed class ContainerServer : IDemoServer
    {
        private readonly SqlServerInstance _sql;
        private readonly string _password;

        public ContainerServer(SqlServerInstance sql, string password)
        {
            _sql = sql;
            _password = password;
        }

        // Read on every use: the container's port can change when it restarts.
        public string AdminConnectionString => _sql.AdminConnectionString;

        public string DormantConnectionString => DemoSetup.DormantConnectionString(_sql.AdminConnectionString, _password);

        public Task RestartAsync(CancellationToken ct) => _sql.RestartAsync(ct);
    }

    [Fact]
    public async Task The_demo_shows_what_was_planted_and_nothing_else()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sql = await SqlServerInstance.StartAsync(SqlServerFixture.Image, ct);
        var password = "Dz1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var server = new ContainerServer(sql, password);
        var directory = Directory.CreateTempSubdirectory("dormant-demo-it-").FullName;
        var storePath = Path.Combine(directory, "dormant.db");
        try
        {
            using var log = new StringWriter();
            await DemoSetup.RunAsync(server, storePath, password, TimeSpan.FromMinutes(45), log, ct);
            log.ToString().ShouldContain("SYNTHETIC history");

            // The real tail: the demo application runs, and Dormant collects as the least-privilege login.
            var random = new Random(7);
            for (var i = 0; i < 3; i++)
            {
                await DemoWorkload.RunOnceAsync(server.AdminConnectionString, random, ct);
            }

            var collector = new SqlServerCollector(server.DormantConnectionString, new DatabaseFilter([DemoPlan.Database], []));
            var config = ConfigLoader.Parse(
                $"version: 1\nstore: {{ path: dormant.db }}\ninstances:\n  - name: {DemoPlan.Instance}\n    connection: {{ env: X }}\n",
                Path.Combine(directory, "demo.yaml"));
            await using (var store = await DormantStore.OpenAsync(storePath, ct))
            {
                await store.IngestAsync(DemoPlan.Instance, await collector.CollectAsync(ct), new IngestOptions(TimeSpan.FromMinutes(45), []), ct);
                var estate = await store.ReadClassificationAsync(config, DateTimeOffset.UtcNow, ct);

                foreach (var table in estate.Tables)
                {
                    var (planted, writeOnly) = DemoPlan.Tables[table.Name];
                    table.Classification.Class.ShouldBe(planted, $"{table.Name}: {table.Classification.Explanation}");
                    table.Classification.WriteOnly.ShouldBe(writeOnly, table.Name);
                }

                var orders = estate.Tables.Single(t => t.Name == "Orders");
                orders.Partitions.Count.ShouldBe(26);
                foreach (var partition in orders.Partitions)
                {
                    var month = partition.PartitionNumber == 1 ? DemoPlan.BeforeOldest : partition.PartitionNumber - 25;
                    partition.Classification.Class.ShouldBe(DemoPlan.OrdersPartition(month), $"{partition.Label}: {partition.Classification.Explanation}");
                }

                estate.UnusedIndexes.Select(i => i.Index).ShouldBe([DemoPlan.UnusedIndex]);
                var status = (await store.ReadStatusAsync(ct)).Instances.ShouldHaveSingleItem();
                status.SyntheticCollections.ShouldBe(1600);
            }

            // Running setup again changes nothing.
            using var again = new StringWriter();
            await DemoSetup.RunAsync(server, storePath, password, TimeSpan.FromMinutes(45), again, ct);
            again.ToString().ShouldContain("already has the demo history");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
