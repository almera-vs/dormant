using Dormant.Collectors.SqlServer;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Store;
using Microsoft.Data.SqlClient;

namespace Dormant.Demo;

/// <summary>
/// Prepares the demo once: the demo database, the least-privilege 'dormant' login (granted by the shipped script), a
/// restart so that building the database is not counted as use, then 200 days of synthetic history ending with one real
/// reading. Running it again changes nothing.
/// </summary>
public static class DemoSetup
{
    public static async Task RunAsync(IDemoServer server, string storePath, string dormantPassword, TimeSpan maxObservedSpan, TextWriter log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(log);
        await Sql.WaitUntilOnlineAsync(server.AdminConnectionString, TimeSpan.FromMinutes(5), ct);

        var exists = await Sql.ScalarAsync(server.AdminConnectionString, $"SELECT DB_ID(N'{DemoPlan.Database}');", ct) is not (null or DBNull);
        if (!exists)
        {
            var now = DateTimeOffset.UtcNow;
            await log.WriteLineAsync($"Creating {DemoPlan.Database}: 24 monthly partitions of orders, customers, products, an archive and an audit log.");
            await Sql.ExecuteAsync(server.AdminConnectionString, SqlScript.Batches(DemoSchema.Create(DemoPlan.BaseMonth(now))), ct);

            await log.WriteLineAsync($"Creating the login '{DemoPlan.Login}' with the grants in deploy/sql/sqlserver-2022-and-later.sql, and nothing else.");
            var login = $"IF SUSER_ID(N'{DemoPlan.Login}') IS NULL CREATE LOGIN [{DemoPlan.Login}] WITH PASSWORD = N'{dormantPassword.Replace("'", "''", StringComparison.Ordinal)}', CHECK_POLICY = OFF;";
            await Sql.ExecuteAsync(server.AdminConnectionString, [login, .. SqlScript.Batches(GrantScript(), new Dictionary<string, string> { ["DormantLogin"] = DemoPlan.Login })], ct);

            // Loading data and building indexes and statistics read every partition; a restart sets the counters to zero.
            await log.WriteLineAsync("Restarting SQL Server, so that building the demo is not counted as use.");
            await server.RestartAsync(ct);
        }

        await using var store = await DormantStore.OpenAsync(storePath, ct);
        var status = await store.ReadStatusAsync(ct);
        if (status.Instances.Any(i => i.Name == DemoPlan.Instance && i.Collections > 0))
        {
            await log.WriteLineAsync($"The store {storePath} already has the demo history; nothing to do.");
            return;
        }

        var template = await new SqlServerCollector(server.DormantConnectionString, new DatabaseFilter([DemoPlan.Database], [])).CollectAsync(ct);
        if (template.Databases.SingleOrDefault(d => d.Name == DemoPlan.Database) is not { Outcome: DatabaseOutcome.Collected })
        {
            throw new InvalidOperationException($"The '{DemoPlan.Login}' login could not read {DemoPlan.Database}: {(template.Databases.Count > 0 ? template.Databases[0].Detail : "not collected")}");
        }

        await log.WriteLineAsync($"Backfilling {DemoPlan.HistoryDays} days of SYNTHETIC history, one reading every {DemoBackfill.Step.TotalHours:0} hours, marked as demo data.");
        var synthetic = new IngestOptions(DemoBackfill.Step + TimeSpan.FromHours(1), [], Synthetic: true);
        var readings = 0;
        foreach (var snapshot in DemoBackfill.Snapshots(template, template.StartedUtc))
        {
            await store.IngestAsync(DemoPlan.Instance, snapshot, synthetic, ct);
            readings++;
        }

        // The real reading closes the made-up history; from here on, everything is collected.
        await store.IngestAsync(DemoPlan.Instance, template, new IngestOptions(maxObservedSpan, []), ct);
        await log.WriteLineAsync($"Wrote {readings} synthetic readings and 1 real one to {storePath}.");
    }

    private static string GrantScript()
    {
        using var stream = typeof(DemoSetup).Assembly.GetManifestResourceStream("Dormant.Demo.grants.sql")
            ?? throw new InvalidOperationException("The grant script is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Builds the connection string the demo's 'dormant' login uses, from the admin one.</summary>
    public static string DormantConnectionString(string adminConnectionString, string password) =>
        new SqlConnectionStringBuilder(adminConnectionString) { UserID = DemoPlan.Login, Password = password, InitialCatalog = "master" }.ConnectionString;
}
