using System.Diagnostics;
using Dormant.Collectors.SqlServer;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Dormant.Store;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Dormant.Integration;

/// <summary>
/// Runs alone, after every other test: a timing measured while other SQL Server containers share the machine measures
/// the machine, not Dormant.
/// </summary>
[CollectionDefinition(nameof(PerformanceTests), DisableParallelization = true)]
public sealed class RunsAlone;

/// <summary>
/// The performance budget: collecting 50 databases with 20,000 indexes finishes within 30 seconds and takes no locks on user
/// tables. Its own container, because it builds a large estate.
/// </summary>
[Collection(nameof(PerformanceTests))]
public sealed class PerformanceTests
{
    private const int Databases = 50;
    private const int TablesPerDatabase = 100;
    private const int PartitionedTables = 10;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>100 tables, each with a clustered primary key and three nonclustered indexes; ten of them on 25 monthly partitions.</summary>
    private static string Estate(int database) => $"""
        CREATE DATABASE [Perf{database:00}];
        GO
        USE [Perf{database:00}];
        CREATE PARTITION FUNCTION pf (date) AS RANGE RIGHT FOR VALUES (
            '2025-01-01', '2025-02-01', '2025-03-01', '2025-04-01', '2025-05-01', '2025-06-01', '2025-07-01', '2025-08-01',
            '2025-09-01', '2025-10-01', '2025-11-01', '2025-12-01', '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01',
            '2026-05-01', '2026-06-01', '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01');
        CREATE PARTITION SCHEME ps AS PARTITION pf ALL TO ([PRIMARY]);
        DECLARE @t int = 0, @sql nvarchar(max);
        WHILE @t < {TablesPerDatabase}
        BEGIN
            DECLARE @on nvarchar(40) = CASE WHEN @t < {PartitionedTables} THEN N' ON ps (D)' ELSE N'' END;
            SET @sql = CONCAT(
                N'CREATE TABLE dbo.T', @t, N' (Id int NOT NULL, D date NOT NULL, A int NOT NULL, B int NOT NULL, C int NOT NULL, ',
                N'CONSTRAINT PK_T', @t, N' PRIMARY KEY CLUSTERED (Id, D))', @on, N';',
                N'CREATE INDEX IX_T', @t, N'_A ON dbo.T', @t, N' (A)', @on, N';',
                N'CREATE INDEX IX_T', @t, N'_B ON dbo.T', @t, N' (B)', @on, N';',
                N'CREATE INDEX IX_T', @t, N'_C ON dbo.T', @t, N' (C)', @on, N';',
                N'INSERT dbo.T', @t, N' (Id, D, A, B, C) VALUES (1, ''2026-06-15'', 1, 2, 3);',
                N'SELECT COUNT(*) FROM dbo.T', @t, N' WITH (INDEX(IX_T', @t, N'_A)) WHERE A = 1;');
            EXEC sp_executesql @sql;
            SET @t += 1;
        END
        """;

    [Fact]
    public async Task Fifty_databases_with_20000_indexes_are_collected_within_30_seconds_without_locking_user_tables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sql = await SqlServerInstance.StartAsync(SqlServerFixture.Image, ct);
        for (var database = 0; database < Databases; database++)
        {
            await sql.ExecuteAsync(Estate(database), ct);
        }

        // Another application holds an exclusive lock on a user table for the whole collection. Dormant reads only
        // metadata and counters, so it must never wait for it.
        await using var holder = new SqlConnection(new SqlConnectionStringBuilder(sql.AdminConnectionString) { InitialCatalog = "Perf00" }.ConnectionString);
        await holder.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(ct);
        await using (var lockTable = new SqlCommand("SELECT COUNT(*) FROM dbo.T0 WITH (TABLOCKX, HOLDLOCK);", holder, transaction))
        {
            await lockTable.ExecuteScalarAsync(ct);
        }

        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sampler = SampleDormantLocksAsync(sql.AdminConnectionString, sampling.Token);

        var collector = new SqlServerCollector(sql.AdminConnectionString, new DatabaseFilter(["Perf*"], []));
        var collectTime = Stopwatch.StartNew();
        var snapshot = await collector.CollectAsync(ct);
        collectTime.Stop();
        await sampling.CancelAsync();
        var locks = await sampler;
        var statements = await StatementCostsAsync(sql.AdminConnectionString, ct);

        var directory = Directory.CreateTempSubdirectory("dormant-perf-").FullName;
        try
        {
            await using var store = await DormantStore.OpenAsync(Path.Combine(directory, "dormant.db"), ct);
            var ingestTime = Stopwatch.StartNew();
            await store.IngestAsync("perf", snapshot, new IngestOptions(TimeSpan.FromHours(1), []), ct);
            ingestTime.Stop();

            // The steady state: a second collection compared against the first.
            var second = Stopwatch.StartNew();
            var again = await collector.CollectAsync(ct);
            await store.IngestAsync("perf", again, new IngestOptions(TimeSpan.FromHours(1), []), ct);
            second.Stop();

            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Collected {snapshot.Databases.Count} databases, {snapshot.IndexCount} indexes, {snapshot.PartitionCount} partitions in {collectTime.Elapsed.TotalSeconds:0.0}s; " +
                $"first ingest {ingestTime.Elapsed.TotalSeconds:0.0}s; second collection and ingest {second.Elapsed.TotalSeconds:0.0}s; " +
                $"lock samples {locks.Samples}, schema-stability locks seen on user tables {locks.SchemaStability(snapshot)}." +
                Environment.NewLine + "First collection, per statement (executions, compile ms, elapsed ms, CPU ms):" + Environment.NewLine + statements);

            snapshot.Databases.Count.ShouldBe(Databases);
            snapshot.Databases.ShouldAllBe(d => d.Outcome == DatabaseOutcome.Collected);
            snapshot.IndexCount.ShouldBeGreaterThanOrEqualTo(20_000);
            locks.DataLocks(snapshot).ShouldBeEmpty();
            collectTime.Elapsed.ShouldBeLessThan(Budget);
            (collectTime.Elapsed + ingestTime.Elapsed).ShouldBeLessThan(Budget);
            second.Elapsed.ShouldBeLessThan(Budget);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Where the collection's time went: each of Dormant's statements, its compile time (from its cached plan) and run time.</summary>
    private static async Task<string> StatementCostsAsync(string adminConnectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            WITH XMLNAMESPACES (DEFAULT 'http://schemas.microsoft.com/sqlserver/2004/07/showplan'),
            statements AS (
                SELECT LEFT(REPLACE(REPLACE(SUBSTRING(t.text, qs.statement_start_offset / 2 + 1, 400), CHAR(10), ' '), CHAR(13), ' '), 90) AS statement,
                       qs.total_elapsed_time, qs.total_worker_time, qs.execution_count,
                       TRY_CAST(tp.query_plan AS xml).value('(//QueryPlan/@CompileTime)[1]', 'bigint') AS compile_ms
                FROM sys.dm_exec_query_stats AS qs
                CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS t
                CROSS APPLY sys.dm_exec_text_query_plan(qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset) AS tp
                WHERE t.text LIKE '%sys.dm_db_partition_stats%' AND t.text NOT LIKE '%dm_exec_query_stats%')
            SELECT statement, SUM(execution_count), SUM(compile_ms), SUM(total_elapsed_time) / 1000, SUM(total_worker_time) / 1000
            FROM statements GROUP BY statement ORDER BY SUM(compile_ms) DESC;
            """, connection) { CommandTimeout = 120 };
        await using var reader = await command.ExecuteReaderAsync(ct);
        var lines = new System.Text.StringBuilder();
        while (await reader.ReadAsync(ct))
        {
            lines.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"  {reader.GetInt64(1),4} {(reader.IsDBNull(2) ? -1 : reader.GetInt64(2)),8} {reader.GetInt64(3),8} {reader.GetInt64(4),8}  {reader.GetString(0)}");
        }

        return lines.ToString();
    }

    private sealed record LockRow(string Type, string Mode, string Database, long Entity);

    private sealed record LockSamples(int Samples, List<LockRow> Rows)
    {
        /// <summary>Locks on user tables' rows, pages or partitions, or object locks on user tables other than schema stability.</summary>
        public List<string> DataLocks(Snapshot snapshot)
        {
            var objects = snapshot.Databases.SelectMany(d => d.Indexes.Select(i => (d.Name, (long)i.Table.ObjectId))).ToHashSet();
            var partitions = snapshot.Databases.SelectMany(d => d.Indexes.SelectMany(i => i.Partitions.Select(p => (d.Name, p.PartitionId)))).ToHashSet();
            return Rows
                .Where(r => r.Type == "OBJECT"
                    ? objects.Contains((r.Database, r.Entity)) && r.Mode != "Sch-S"
                    : partitions.Contains((r.Database, r.Entity)))
                .Select(r => $"{r.Type} {r.Mode} in {r.Database} on {r.Entity}")
                .Distinct()
                .ToList();
        }

        public int SchemaStability(Snapshot snapshot)
        {
            var objects = snapshot.Databases.SelectMany(d => d.Indexes.Select(i => (d.Name, (long)i.Table.ObjectId))).ToHashSet();
            return Rows.Count(r => r.Type == "OBJECT" && r.Mode == "Sch-S" && objects.Contains((r.Database, r.Entity)));
        }
    }

    /// <summary>
    /// Polls sys.dm_tran_locks for locks held by Dormant's sessions (application name 'Dormant') in user databases. Each
    /// lock is judged later against the snapshot's own objects and partitions, in the lock's own database.
    /// </summary>
    private static async Task<LockSamples> SampleDormantLocksAsync(string adminConnectionString, CancellationToken ct)
    {
        var samples = 0;
        var rows = new List<LockRow>();
        await using var connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        while (!ct.IsCancellationRequested)
        {
            await using var command = new SqlCommand("""
                SELECT l.resource_type, l.request_mode, COALESCE(DB_NAME(l.resource_database_id), CONCAT('database ', l.resource_database_id)),
                       COALESCE(l.resource_associated_entity_id, 0)
                FROM sys.dm_tran_locks AS l
                JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
                WHERE s.program_name = 'Dormant' AND l.resource_database_id > 4
                  AND l.resource_type IN ('OBJECT', 'HOBT', 'PAGE', 'KEY', 'RID');
                """, connection);
            await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
            {
                while (await reader.ReadAsync(CancellationToken.None))
                {
                    rows.Add(new LockRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
                }
            }

            samples++;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new LockSamples(samples, rows);
    }
}
