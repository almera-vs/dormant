# Architecture

Dormant is one executable, `dormant`, that runs either as a one-shot CLI or as a long-running service. It reads
counters and metadata from SQL Server, keeps its own history in a local SQLite file, and writes reports to disk. It
makes no other network calls.

```
            SQL Server instances (read-only)
                     │  DMVs, catalog views, Query Store
                     ▼
 ┌──────────────────────────────────────────┐
 │ Dormant.Collectors   ICollector           │  one connection per instance at a time
 │   SqlServer/          SqlServerCollector  │
 │                       SqlServerValidator  │
 └───────────────┬──────────────────────────┘
                 │ snapshots
                 ▼
 ┌──────────────────────────────────────────┐
 │ Dormant.Store        SQLite, deltas,      │  restart-proof history (milestones 2–4)
 │                      rollups, coverage    │
 └───────────────┬──────────────────────────┘
                 ▼
 ┌──────────────────────────────────────────┐
 │ Dormant.Core         config, domain model,│  engine-neutral
 │                      classification, cost │
 └───────────────┬──────────────────────────┘
                 ▼
 ┌──────────────────────────────────────────┐
 │ Dormant.Report       HTML / CSV / JSON    │  writes files only
 └──────────────────────────────────────────┘
   Dormant.Attribution  plan XML parser
   Dormant.Cli          commands, hosting, exit codes
```

## Projects

| Project | Holds | References |
|---|---|---|
| `Dormant.Core` | Configuration model and loader; engine-neutral contracts and snapshot model (`ICollector`, `Snapshot`, `UsageCounters`, `IInstanceValidator`, `IAttributionSource`); classification, coverage and cost (`Usage/`); the history summary (`History/StoreStatus`); shared text formatting. | YamlDotNet |
| `Dormant.Collectors` | `SqlServer/`: connections, platform detection, permission model, validation, collection, Query Store harvesting. | Core, Attribution, Microsoft.Data.SqlClient (+ Azure extension) |
| `Dormant.Store` | SQLite store: delta engines, ingestion, rollups, retention, status, classification and day-by-day usage (`Estate/`), attribution. | Core, Microsoft.Data.Sqlite |
| `Dormant.Attribution` | Showplan XML parser: the tables a plan reads and writes. | Core |
| `Dormant.Report` | The HTML report (`HtmlReport`, one class per section, `HeatmapCells`) and the CSV/JSON exports (`Export`). Renders what it is given; knows nothing of the store. | Core |
| `Dormant.Cli` | `dormant` executable: command tree, logging, exit codes, hosting (`run`, the Windows Service installer). | Core, Collectors, Store, Report |

Engine-specific code stays behind `ICollector` and `IInstanceValidator`, so a PostgreSQL collector can be added
without touching Core.

## Configuration

`ConfigLoader` reads YAML into a node tree, and `ConfigBinder` binds it to immutable records, collecting every error
with its line, column and field path. Secrets are references (`env:` or `file:`) resolved only when a command connects
(`SecretResolver`). See decisions D-003, D-008 to D-010.

## From counters to history

1. `SqlServerCollector.CollectAsync` reads, per selected database, the identity, server UTC time, engine incarnation and
   every index of every user table and indexed view with its `sys.dm_db_index_usage_stats` row, if any. Databases that
   are not ONLINE, not MULTI_USER, AUTO_CLOSE or not accessible are listed as skipped, never opened.
2. `DormantStore.IngestAsync` maps tables and indexes to stable keys (`CatalogWriter`), loads the database's previous
   reading (`BaselineRepository`) and asks `DeltaEngine.Compare` for the interval between the two: its kind, how much of
   it was observed, and the activity per index. It writes the interval, the deltas and the new baseline in one
   transaction.
3. In the same transaction, `RollupWriter` folds the new intervals into hourly and daily buckets: observed time split
   exactly at bucket boundaries, activity in the bucket where its interval ended, maintenance activity kept apart.
   Restarts, resets and gaps go to `history_event`.
4. `status` reads coverage from the daily rollups and events from `history_event`, so it survives raw retention.
5. `ClassificationReader` condenses the daily rollups into `UsageEvidence` per table, partition and index, and
   `Core.Usage.Classifier` turns that into Hot, Warm, Cold, Dormant or Insufficient history, with coverage and an
   explanation. `CostModel` prices sizes with the customer's own figures (D-048 to D-051).
6. When `query_store.enabled`, `InstanceCollector` asks the store what to read next from each database's Query Store,
   `SqlServerQueryStoreHarvester` reads it (plan XML only for new plans), `Dormant.Attribution.ShowplanParser` finds the
   tables each plan reads and writes, and the store keeps them (`qs_plan`, `qs_plan_object`, `qs_state`). The store
   implements `IAttributionSource`: who read a table since when, and the last known reader (D-052, D-053).

7. `report` and `export` read the store read-only. `DormantStore.ReadClassificationAsync(config, now, historyDays)` adds
   to each table and partition a `DailyUsage` series: user reads and the observed share per UTC day, from `db_day`,
   `index_day`, `part_day` and `part_unknown` (`DailyUsageReader`). `HtmlReport.Render` draws it with the status
   (restarts, resets, gaps); `Export` writes the same classification as CSV or JSON (D-054 to D-057).

`dormant run` hosts `DormantWorker`: one loop per instance (collect, then wait for the next aligned tick or maintenance
window boundary) and one loop that prunes history past its retention.

Partitions follow the same path: `PartitionWriter` maps each partition to a key by its boundary range and keeps its size,
and `PartitionDeltaEngine` turns operational counters into per-partition activity, exact only when provably
continuous, with everything else recorded as unknown stretches.

Store tables: `instance`, `collection` (every attempt, including failures), `db`, `db_observation`, `obj`, `idx`,
`part`, `part_size`, `usage_interval`, `index_delta`, `part_delta`, `part_unknown`, `db_baseline`, `index_baseline`,
`part_baseline`, `db_hour`, `db_day`, `index_hour`, `index_day`, `part_hour`, `part_day`, `history_event`. The rules are
in decisions D-024 to D-047.

## Deployment and demo

| Path | Holds |
|---|---|
| `deploy/windows` | `install.ps1` and `uninstall.ps1` around `dormant service install` and `uninstall` (`Cli/Hosting/WindowsService`, D-059). |
| `deploy/systemd` | The hardened unit and its install and uninstall scripts (D-060). |
| `deploy/docker` | The container image: the self-contained executable on a chiseled, non-root base; a `demo` target (D-060). |
| `deploy/sql` | Least-privilege permission scripts, run as shipped by the integration tests (D-061). |
| `deploy/demo` | The docker-compose demo and its configuration (D-062). |
| `demo/Dormant.Demo` | `dormant-demo`: builds the demo database, backfills synthetic history marked as such (D-058), runs the workload. |

CI runs the solution's tests on Linux (with the SQL Server containers) and Windows, publishes the three single-file
builds, installs and runs the Windows Service and the systemd unit, builds the image and runs the demo.

## Connections

`SqlServerConnections` applies Dormant's settings to every connection: application name `Dormant`, a 2-second
`LOCK_TIMEOUT`, `DEADLOCK_PRIORITY LOW`, 30-second command timeouts. On SQL Server, Managed Instance and RDS, work in
other databases runs over the instance's single connection through `EXEC [db].sys.sp_executesql`. On Azure SQL Database,
each database gets its own connection, opened one at a time. See D-016 and D-017.

## Output streams

Standard output carries command results only. Logs are structured JSON on standard error (D-011).

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Everything succeeded. |
| 1 | Partial failure: some instances unreachable or missing permissions; or an unexpected error (printed in full). |
| 3 | Configuration or command-line error. |
| 4 | Dormant's local store could not be used. |
