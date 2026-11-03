# Decisions

Design decisions, with the reason for each. Newest last. When a decision is revisited, the old entry stays and a new
one supersedes it.

## D-001 · Toolchain: .NET 10 SDK, pinned by `global.json`

`global.json` asks for SDK 10.0.100 with `rollForward: latestFeature`, so any 10.0.x feature band builds the same way.
Development used SDK 10.0.403. Central package management (`Directory.Packages.props`) keeps every version in one file.

## D-002 · CLI library: System.CommandLine 2.0

System.CommandLine has been stable since 2.0.0 (November 2025); 2.0.14 is current. It is Microsoft's own library and
needs no extra dependency, so Spectre.Console.Cli was not considered further.

System.CommandLine reports parse errors with exit code 1 by default. Dormant checks `ParseResult.Errors` before
invoking any command and returns **3** (usage error), as the exit-code contract requires.

## D-003 · Configuration: YAML read through YamlDotNet's representation model

The YAML document is loaded as a node tree and bound by hand (`ConfigBinder`, `YamlSection`) instead of deserialised
by reflection. This lets validation:

- report **every** problem at once, each with `file:line:column`, the field path (`instances[0].connection.env`) and a
  plain fix;
- reject unknown keys, with a "did you mean" suggestion for typos (a misspelt `min_coverage` silently falling back to
  its default would change classifications);
- stay free of reflection, which keeps the trimming/AOT door open (see D-004).

YamlDotNet reports some malformed input (an unclosed `{`) as a bare `InvalidOperationException` without a position;
the loader turns that into a configuration error rather than an unexpected crash.

## D-004 · Single-file self-contained publish, not Native AOT

`Microsoft.Data.SqlClient` 7.x is not trim- or AOT-clean, and Entra ID support pulls in `Azure.Identity` and the MSAL
broker, which are not either. Dormant therefore publishes as a single-file, self-contained executable with compression
for win-x64, linux-x64 and linux-arm64. Revisit when SqlClient declares AOT compatibility.

## D-005 · Assertions: Shouldly

FluentAssertions 8 and later is commercially licensed. Shouldly 4.3.0 (BSD) is used instead.

## D-006 · Tests run on Microsoft.Testing.Platform

xUnit v3 (4.x) on the .NET 10 SDK no longer supports the VSTest path through `dotnet test`. `global.json` opts in to
Microsoft.Testing.Platform; test projects need neither `Microsoft.NET.Test.Sdk` nor `xunit.runner.visualstudio`.

## D-007 · Entra ID through `Microsoft.Data.SqlClient.Extensions.Azure`

SqlClient 7.0 moved Entra ID authentication out of the core package. The SqlClient 7.0 release notes say referencing
`Microsoft.Data.SqlClient.Extensions.Azure` is enough for `Authentication=Active Directory ...` connection strings,
with no code changes. Dormant references it from `Dormant.Collectors`. An Azure-hosted integration test is still owed
(tracked for milestone 8).

## D-008 · Secrets are resolved when used, never when loading the configuration

`connection: { env: ... }` and `{ file: ... }` are checked for shape at load time, but read only when a command needs to
connect. `status`, `report` and `export` therefore work without access to credentials, and a missing variable is
reported per instance by `validate` (exit 1) rather than as a configuration error.

Messages about secrets name the source (`env:DORMANT_ERP_PROD`), never the value. A connection string the driver
cannot parse is reported without the driver's message or inner exception, because that message can quote fragments of
the string, including a password.

## D-009 · Relative paths resolve against the configuration file's directory

Not the working directory: a service started from `/` and an operator in their home directory must see the same
store.

## D-010 · Configuration defaults

| Field | Default | Why |
|---|---|---|
| `collection.interval` | `15m` | Fine enough for daily history, light on the server; minimum `5m`. |
| `instances[].databases` | include `*`, exclude `master`, `model`, `msdb`, `tempdb` | When `exclude` is omitted the system databases are still excluded; listing them is the common intent. |
| `instances[].query_store.enabled` | `false` | Reading plans is the heaviest thing Dormant can do; it is opt-in. The `init` example turns it on, to show the setting. |
| `query_store.lookback` | `30d` | The warm window: enough to name recent readers. |
| `query_store.store_text` | `false` | Query text can contain literal values; only the query hash and procedure name are kept unless asked. |
| maintenance window time zone | UTC | A server's local zone is ambiguous in containers. |
| `report.out_dir` | `reports` | Beside the configuration file. |
| `classification`, `cost` | hot 7, warm 30, dormant 180 days, coverage 0.9; no prices | `warm_days > hot_days` and `dormant_days > warm_days` are enforced. |

Maintenance windows accept `Mon`..`Sun` or `Daily`. An end before the start runs past midnight into the next day.
Times are wall-clock times in the window's zone, so a `01:00-05:00` window stays at those local hours across daylight
saving (and is one hour shorter in UTC on the spring-forward night).

## D-011 · Logs go to standard error as JSON; results go to standard output

Standard output carries only command results (validation output now; reports and exports later) and is safe to pipe.
Logs are JSON lines on standard error. Interactive commands default to `--log-level Warning`; validation findings are
logged at Information/Debug because they are already printed as the command's result.

## D-012 · Exit codes beyond the specified ones

- An unexpected internal error prints the full exception (it is a bug and should be reportable) and exits **1**.
- Cancellation (Ctrl+C) exits **1**: the run did not complete.

## D-013 · No `ConfigureAwait(false)`

Every host Dormant runs in (console, Windows Service, systemd, container) has no `SynchronizationContext`, so
`ConfigureAwait(false)` would be noise. CA2007 is off.

## D-014 · Transitive pin: `System.Security.Cryptography.Pkcs` 10.0.14

SqlClient 7.1.1 pulls in 9.0.18, which has known vulnerabilities (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7). Transitive
pinning is on and the patched 10.0.14 is pinned. NuGet audit warnings are build errors, so a new advisory fails CI.

## D-015 · How `validate` decides what is missing

For each permission it checks the login's *effective* permission with `HAS_PERMS_BY_NAME` (covering role membership,
`CONTROL SERVER` and the permission hierarchy) and then reads one row from each DMV collection will use, because the
probe is the ground truth. Each failure prints the `GRANT` that fixes it, naming the actual login.

The permission names per platform follow Microsoft Learn (see `docs/sqlserver-internals.md`): `VIEW SERVER STATE` on
SQL Server 2016–2019 and Managed Instance; `VIEW SERVER PERFORMANCE STATE` and `VIEW DATABASE PERFORMANCE STATE` on
SQL Server 2022 and later (including RDS); `VIEW DATABASE STATE` per database on Azure SQL Database. For metadata
visibility `validate` checks `VIEW DEFINITION`, which on SQL Server 2022 includes the narrower `VIEW SECURITY
DEFINITION` that Learn lists for `sys.dm_db_partition_stats`. Whether the narrower permission alone is enough will be
tested before `docs/permissions.md` is finalised in milestone 8.

## D-016 · Session settings for every connection

`Application Name=Dormant` (overriding the user's), `SET LOCK_TIMEOUT 2000`, `SET DEADLOCK_PRIORITY LOW` (if Dormant
is ever in a deadlock, it is the victim) and a 30-second command timeout. Settings are re-applied on every open
because pooled connections are not guaranteed to keep them. Dormant holds at most one connection per instance: on
Azure SQL Database the server connection is closed before per-database connections are opened, one at a time.

## D-017 · Databases that are not ONLINE or not MULTI_USER are skipped

A DBA who sets a database to `SINGLE_USER` for maintenance must never find Dormant holding the only connection.
`RESTRICTED_USER`, `OFFLINE`, `RESTORING` and similar states are reported as warnings and not touched.

## D-018 · `sys.database_recovery_status` is not read by `validate`

Learn notes that querying it starts databases that are closed (AUTO_CLOSE). `validate` warns about AUTO_CLOSE databases
instead. How collection obtains `database_guid` without waking closed databases is decided in milestone 2.

## D-019 · Instance start time: UTC derived from tick counters

`sqlserver_start_time` is the server's local time. Dormant derives UTC as `SYSUTCDATETIME() - (ms_ticks -
sqlserver_start_time_ms_ticks)`, which does not depend on the server's time zone or daylight-saving rules, and keeps
the reported value too. The reported value is stable across reads and is the one restart detection will compare
(milestone 2); the derived UTC value can move by a few milliseconds between reads and is for display and ordering.

## D-020 · Platform detection

`SERVERPROPERTY('EngineEdition')`: 1–4 boxed SQL Server, 5 Azure SQL Database, 8 Azure SQL Managed Instance; others are
unsupported. Amazon RDS is boxed SQL Server with an `rdsadmin` database. Azure SQL reports a fixed 12.x version, so the
SQL Server 2016 minimum applies only to boxed SQL Server and RDS.

## D-021 · All source projects exist from milestone 1

`Dormant.Store`, `Dormant.Attribution` and `Dormant.Report` exist as empty projects so that the layout and project
references are fixed from the start. Each gains code in its milestone.

## D-022 · Licence: not chosen

Dormant is planned as open-core. The licence is the owner's decision and is not in the repository yet.
*Superseded by D-065: Dormant is MIT-licensed.*

## D-023 · Landscape check (2026-11-02)

Rechecked before writing positioning text:

- **sp_BlitzIndex** (First Responder Kit, now released annually from July 2026) added LLM-assisted advice at table
  level in 2026. It remains a point-in-time diagnostic with no usage history.
- **Database Health Monitor v3** now stores index usage in its own history database so that reports span restarts
  (vendor claim, not verified). This is the closest overlap found: it covers index-level history; no evidence of
  partition-level history, coverage tracking or confidence-qualified classification.
- **Redgate Monitor, SolarWinds DPA, Quest Spotlight**: still performance-focused; no evidence found of long-term
  object-usage reporting.
- Community scripts that `MERGE` snapshots of `sys.dm_db_index_usage_stats` into a table remain the common DIY route.
- Azure SQL Database serverless resets usage statistics on every resume, as well as on host restarts: one more reason
  restart-proof history matters, and a case for the restart-detection tests.

Dormant's differentiation (partition granularity by boundary value, explicit coverage, "unknown is not zero",
read-only and local) stands.

## D-024 · History is stored as intervals between readings, not as raw snapshots

Each collection reads the cumulative counters of every index and compares them with the previous reading of the same
database (its *baseline*). The result is one `usage_interval` per database per collection, plus an `index_delta` row
for each index that had activity in it. Only the latest reading is kept as the baseline. Twenty thousand indexes read
every 15 minutes would otherwise be close to two million raw rows a day. The rules that turn two readings into an
interval live in one pure function, `DeltaEngine.Compare`, which is unit-tested on its own.

Every interval states how much of it was observed (`observed_from`); "no reads" may only ever be concluded for observed
time. Interval kinds:

| Kind | Meaning | Observed |
|---|---|---|
| `continuous` | Counters compared cleanly. | All of it, unless longer than the limit (D-027). |
| `gap` | Counters compared cleanly, but too long to attribute (agent downtime), or the server clock went backwards. | None. Reads are still recorded exactly. |
| `instance_restart` | The engine restarted at a known time inside the interval. | From the restart. |
| `reset` | Counters restarted at an unknown time inside the interval. | None. Reads are recorded as lower bounds. |

## D-025 · Restarts are detected by the engine's tick counters, not by its local start time

`sqlserver_start_time` is a local time, and how it behaves across a daylight-saving change is not documented. Comparing
it could invent a restart, and a false restart turns cumulative counters into a large false burst of reads.
`sqlserver_start_time_ms_ticks` is fixed for the life of the process. Dormant treats the engine as restarted when that
value changes, or when `ms_ticks` goes backwards (the host rebooted). The restart time is the tick-derived UTC start
(D-019).

If the "new" engine started before the previous reading, the database moved to an engine that was already running: an
Azure SQL Database reconfiguration or failover. Its counters restarted at an unknown time, so the interval is a `reset`.

## D-026 · No usage row means "no use since the counters started"; a row that disappears means "unknown"

Learn: "When an index is used, a row is added to `sys.dm_db_index_usage_stats` if a row does not already exist", and
rows are removed only when the engine starts or the database is detached or shut down. So an index with no row, in a
database that has been continuously readable, has not been used since its counters started. That is an observation,
and without it a table nobody touches could never be called dormant.

The rule "missing means unknown, never zero" applies to everything else, in particular:

- a row that existed in the previous reading and is gone now (D-028);
- `sys.dm_db_index_operational_stats`, whose rows can be evicted from cache (milestone 3);
- any database that was not readable, or is absent from a collection (D-029).

## D-027 · An interval counts as observed only if it is at most three collection intervals long

With the default 15-minute interval, an interval of up to 45 minutes counts as observed: one missed or slow collection
is not a gap. Anything longer is a `gap` with no observed time, so agent downtime is never shown as inactivity.
Activity inside a gap is still recorded exactly, because the counters are cumulative. It just cannot be placed in time
more precisely than the gap. The limit follows the configured interval (`CollectionOptions.MaxObservedSpan`).

## D-028 · A usage row that vanishes or goes backwards resets the whole database

On SQL Server 2016 and later, Learn documents only database-wide removal of usage rows. So if any row of an index that
still exists disappears, or any of its counters is lower than before, Dormant treats the interval as a database-wide
`reset` at an unknown time. Every index's current counters are recorded as lower-bound activity, and no part of the
interval counts as observed. This handles a per-object reset (fault scenario 3) without negative deltas. It also makes
sure a database restart that left one visible trace does not leave the other indexes' "no row" looking like "no use".
The cost is one interval of coverage per event.

## D-029 · Database interruptions break the comparison, and outrank a known restart time

A database found not ONLINE, set to AUTO_CLOSE, unreadable by the login, or absent from a collection has its baseline
marked *interrupted*. The next successful reading is a `reset`, not a comparison. A database whose `database_id`
changed (detach and attach, restore over) is a `reset` too. Both are checked before the engine-restart rule: a database
can go offline after a restart within the same interval, and then its counters date from its return, not from the
restart.

A reading that fails for a transient reason (a lock timeout, a dropped query) does not interrupt: the baseline is kept,
and the next reading compares cleanly across the longer interval, subject to D-027.

Residual risk, recorded in `sqlserver-internals.md`: a database that is taken offline and brought back between two
collections leaves no trace if none of its indexes had a usage row before. A read in that window before the outage
would be lost. The window is at most one interval wide.

## D-030 · AUTO_CLOSE databases are skipped

The engine discards a database's usage counters each time it auto-closes, which can happen many times between two
collections without leaving a trace. Reading such a database every 15 minutes would also keep waking it, which changes
the server's behaviour. Dormant skips these databases, and `validate` says so and does not open them either. Switching
AUTO_CLOSE off makes them collectable.

## D-031 · Identity of databases, tables and indexes

- Database: `guid:<database_guid>` on SQL Server and Managed Instance; `azure:<create_date>` on Azure SQL Database,
  where `sys.database_recovery_status` does not exist. Learn: Azure's `create_date` does not change on rename and
  changes on restore or copy. `physical_database_name` was rejected because it changes with the service objective.
- Table: `(object_id, create_date)`. Renames keep the history; a drop and recreate starts a new one.
- Index: `(index_id, name, type)` within its table. A renamed or retyped index starts a new history. That makes it look
  younger, never older, so it can only delay an "unused" flag. An index dropped and recreated with the same name and
  type keeps its history, but its usage row vanished in between, so that interval is a `reset` (D-028).

## D-032 · The server's clock is the time base

Interval boundaries use `SYSUTCDATETIME()` read with the counters, not the agent's clock, so they share a clock with
the engine's start time and last-use timestamps. A server clock that moves backwards produces a `gap` (D-024).

## D-033 · Collection sessions use READ UNCOMMITTED

Dormant reads only catalog views and DMVs. READ UNCOMMITTED, with the 2-second `LOCK_TIMEOUT` of D-016, avoids waiting
on or holding shared locks while DDL runs. A read that still times out fails that database for this collection only
(D-029).

## D-034 · Last-use timestamps are converted with the server's current UTC offset

`last_user_seek` and the related columns are server-local **datetime**. They are converted to UTC with the offset at
collection time. For timestamps from the other side of a daylight-saving change this can be one hour off. They are
supporting evidence ("last read at"); the counters are the measurement.

## D-035 · Store design

- SQLite in WAL mode, `synchronous=NORMAL`, foreign keys on, no connection pooling (the file is released on close).
- Times are UTC Unix milliseconds in INTEGER columns.
- One transaction per collection: a crash never leaves half a snapshot.
- Schema upgrades are numbered migrations recorded in `PRAGMA user_version`. A store written by a newer Dormant is
  refused rather than misread. `status` opens the store read-only and never creates or upgrades it.
- On Linux the store's directory is created 0700 and the file 0600; SQLite gives its `-wal` and `-shm` files the main
  file's mode.

## D-036 · `collect` always takes one snapshot

`dormant collect` takes one snapshot of every configured instance and exits; `--once` is accepted, as
scripts may write it. Continuous collection is `dormant run` (milestone 4). An unreachable instance is recorded in the store as a
failed collection, so `status` shows it, and the command exits 1.

## D-037 · Coverage in `status`

Per database: observed time divided by the time from its first reading to its latest. Per instance: the sums of those.
Time since the latest reading is not in the window. When it exceeds the observed-span limit, `status` says it will
become a gap.

## D-038 · Operational statistics are supporting evidence, exact only when provably continuous

Learn on `sys.dm_db_index_operational_stats`: the counters "are set to zero whenever the metadata for the heap or B-tree
is brought into the metadata cache", vanish when it leaves the cache, can be reset by DDL, and "you can't use these
counters to conclusively determine whether an index was used or not". They also include maintenance. They are still
the only per-partition read signal SQL Server has.

`PartitionDeltaEngine` therefore records, per partition per interval:

- **exact** activity only when the database interval was comparable (`continuous` or `gap`), the partition had a row at
  both readings, its `partition_id` is unchanged, and no counter went backwards;
- otherwise the current counts as a **lower bound** (if there is a row) and an **unknown** stretch for the interval.
  This covers fault scenario 8: a partition missing from the DMV is unknown, never zero.

Unknown stretches are stored as merged time ranges (`part_unknown`), so a partition that stays uncached for months is
one row. Index-level "no use" still comes only from `sys.dm_db_index_usage_stats` (D-026). How the two signals combine
into partition classes is decided in milestone 5.

Residual risk: if a partition's metadata is evicted and reloaded between two readings and it then sees at least as
many operations as before, the counters do not go backwards and the delta undercounts. Learn calls eviction of active
objects uncommon; for a partition with no reads at either reading, an undercount cannot turn activity into zero.

Known overcounts, seen on SQL Server 2022 (`sqlserver-internals.md`): automatic statistics updates scan every partition,
and a query whose range ends at a boundary starts a scan in the partition that holds the boundary value. Both err
towards "used", which is the safe direction.

## D-039 · A partition is identified by the range of values it holds

Partition key: (index, `R|L`, typed lower boundary, typed upper boundary), with `-` and `+` for open ends and
`all` for an unpartitioned index. Partition numbers change on SPLIT and MERGE; boundary values do not. So a MERGE
that renumbers every later partition leaves their histories intact (fault scenario 6), and the partitions it creates
or removes start or end their own histories. The boundary's SQL base type is part of the key, so a function rebuilt on
another type does not merge histories by accident. The partitioning column is not part of the key; it is shown in
labels. Boundary values are converted to text in SQL with an explicit lossless style per type
(`SqlServerUsageReader.CanonicalValue`).

`partition_id` is recorded per partition and compared between readings: a new rowset for the same range means its
operational counters started again (D-038).

## D-040 · DMVs are read in statements of their own and joined in the agent

Joining `sys.dm_db_partition_stats` to the catalog views in one statement made SQL Server evaluate the DMV once per row:
26.6 seconds instead of 16 ms for 1,600 partitions on SQL Server 2025 Express. Every DMV (`index_usage_stats`,
`partition_stats`, `index_operational_stats`) is now a separate statement in the same batch, joined in memory by the
collector. Temporary tables were rejected because they would be writes to tempdb on a monitored instance.

## D-041 · Size history is a daily change log

The partition row holds the latest row count, reserved and used pages, and compression. `part_size` gets a row only
when a partition's size changed, at most one per UTC day (the day's last reading). Dormant partitions rarely change, so
they cost almost nothing, while growing ones keep a daily trend. Retention is decided with the other rollups in
milestone 4.

## D-042 · Partition labels

RANGE RIGHT functions on `date`, `datetime`, `datetime2` or `smalldatetime` get calendar labels when a range is exactly
a day (`2021-03-15`), month (`2021-03`), quarter (`2021-Q1`) or year (`2021`), and `before …` / `from …` for the open
ends. Everything else is labelled with its exact range (`2021-01-01 ≤ OrderDate < 2021-03-01`; RANGE LEFT as
`… < x ≤ …`). Labels are for people; the key (D-039) is what identifies.

## D-043 · Rollups are incremental and fold each raw interval exactly once

Every collection writes its raw intervals, then folds every interval not yet rolled up into hourly and daily buckets in
the same transaction (`RollupWriter`), and marks it rolled up. So the rollups are always current, a crash never folds an
interval twice or not at all, and a store upgraded from an older schema is backfilled by its next collection.

- **Observed time** is split exactly at hour and day boundaries, so coverage is identical at every level (tested).
- **Activity** cannot be split, because the counters only say how much happened between two readings. It goes to the
  bucket in which its interval ended. That is the later bucket, so reads look more recent, never less, which can only
  delay a Cold or Dormant label. A gap's activity lands at the end of the gap; the gap itself has no observed time.
- **Flags survive**: a bucket is lower-bound if any interval in it was; `last_user_read_ms` is the maximum; restarts,
  resets and gaps are kept in `history_event`, which outlives the raw intervals (D-045).
- Buckets are UTC hours and UTC days. Reporting in another time zone is on the roadmap.

## D-044 · Maintenance windows: an interval is maintenance only if it lies entirely inside a window

An interval whose activity happened entirely inside a configured window (checked minute by minute, in the window's
time zone) is flagged `maintenance`. Its user reads and updates go to `maint_*` columns in the rollups and do not set
the last-read time. Its time still counts as observed. An interval that only overlaps a window counts as normal use: a
real read near the edge of a window is never discarded (fault scenario 9 is the reverse case and is tested).

The flag is decided when the interval is written, using the configuration at that time; changing windows later applies
from then on. A window must last at least twice the collection interval (configuration error otherwise), so whole
intervals fit inside it. `run` also collects exactly when a window opens and closes (D-046).

This applies to index-level user activity as well as partition-level activity: fault scenario 9 ("reads
only inside the maintenance window: not counted as use") covers both. System activity never counts as use in any case.

## D-045 · Retention

Defaults: raw intervals and deltas 14 days, hourly rollups 90 days, daily rollups 3 years, all set in
`store.retention`; a year is 365 days. Daily retention also governs `history_event`, `part_unknown`, `part_size` and
`collection` rows. Raw intervals are removed only once rolled up. When daily history is pruned, a database's history
start moves to the first retained day, so coverage is always measured over what is kept. `run` prunes at start and every
6 hours; `collect` does not prune.

## D-046 · `run` collects on aligned ticks and at maintenance window boundaries

Each instance has its own loop. It collects at start, then at the next multiple of the interval on the UTC clock (with
15 minutes: :00, :15, :30, :45), so intervals line up across restarts of the service. If a maintenance window opens or
closes sooner, that moment comes first, so maintenance activity falls into whole intervals. A collection that overruns
its tick simply continues at the next one. An unreachable instance is recorded and retried at the next tick. A store
failure stops the service with exit code 4.

## D-048 · Classification rules

Evidence is condensed per UTC day. Only user reads outside maintenance windows count; system activity never does. A
day with exact or lower-bound reads is a read day. The last read also takes SQL Server's own `last_user_*` time from
the latest reading, which can predate Dormant's history, unless that time lies inside a maintenance window. Windows
end now: the last *N* days are today so far plus the *N − 1* days before it.

| Class | Rule (defaults) |
|---|---|
| Hot | read on more than half of the last `hot_days` (7) days: 4 of 7 |
| Warm | a read within the last `warm_days` (30) days |
| Cold | no read in 30 days, a read within `dormant_days` (180), and the 30-day window at least `min_coverage` (90%) observed |
| Dormant | no read known in 180 days, and the 180-day window at least 90% observed |
| Insufficient history | would be Cold or Dormant, but the window lacks the coverage; the explanation says how much was observed |

Coverage counts only time observed while the object existed. Only whole days after it was first seen count, so a
table created (or first seen) recently cannot be Dormant however long its database has been watched (fault scenario
10). Percentages are floored, so 89.96% never reads as 90.0%. *Write-only*: user updates but no reads in the last 30
days, with the 30-day coverage met.

## D-049 · Memory-optimized tables are never judged

Learn: `sys.dm_db_index_usage_stats` does not return memory-optimized indexes. Such a table would have no usage rows
ever and would drift into Dormant. Dormant collects `sys.tables.is_memory_optimized` and always classifies these tables
as Insufficient history, explaining why; their indexes are never reported as unused.

## D-050 · Cost

Monthly estimate = GB × `data_storage_per_gb_month` + GB × `backup_copies` × `backup_storage_per_gb_month`, with
GB = 1024³ bytes as SQL Server reports sizes. While both prices are 0 there is no money figure anywhere, only "set your
price". Money is always labelled an estimate and shown after the size. Sizes are reserved pages (including LOB and
row-overflow).

## D-051 · Units, partitions and unused indexes

- **Units** for sizes and money: each partition of a partitioned table (its range across the clustered index or heap
  and every aligned index), each other table as a whole. Indexes not aligned to the table's partition scheme count
  towards the table, not a partition.
- **Partitions** are classified from operational activity by range (D-038). On days the whole table had no user reads,
  each partition certainly had none. On days the table was read but a partition's counters were unknown, that day does
  not count as observed for the partition. Known overcounts (statistics updates, boundary scans) make partitions look
  more used, never less. In particular, automatic statistics updates on a busy partitioned table register as reads on
  old partitions. Correcting for them is on the roadmap.
- **Unused indexes**: nonclustered (rowstore or columnstore) indexes that are not primary keys, unique indexes or
  unique constraints, not disabled, not XML or spatial, and not on memory-optimized tables. They also need no user read
  in the last `warm_days` days, user updates in that window, and its coverage met (fault scenario 11). The
  `DROP INDEX` statement is produced for review only.

## D-052 · Query Store attribution is supporting information, never a verdict

The "last known reader" names the most recent query or procedure whose plan reads a table. Query Store's default
capture mode (AUTO) skips infrequent and cheap queries, a plan that references a table may not touch it on every run,
and Query Store may be off, read-only or purged. So attribution never changes a classification, and the absence of a
reader is never evidence of disuse. Reads are `Object` elements inside `IndexScan` or `TableScan` (seeks, scans and
lookups), the same operations `sys.dm_db_index_usage_stats` counts as user reads; writes are the targets of insert,
update and delete operators. Temporary tables, table variables and tempdb are ignored. So are index builds, statistics
statements and the engine's internal statistics queries (`SELECT StatMan(...) FROM ... _MS_UPDSTATS_TBL`). Query Store
captures those as ordinary SELECTs; the Query Store container test found one on its first CI run. Plans referencing another
database on the same instance are mapped to it; names are matched case-insensitively against the tables Dormant tracks.

Query text is stored only with `query_store.store_text: true`; otherwise only the query hash and the procedure name.
Query Store is read only when `query_store.enabled` is true (default false).

## D-053 · Query Store is read incrementally

After each collection, for each database collected and with Query Store in READ_WRITE or READ_ONLY:

1. plan metadata (plan id and hash, query hash, procedure, last execution time) for plans that ran since the newest plan
   seen last time, minus one hour of overlap; on the first read, since `query_store.lookback` (default 30 days);
2. plan XML only for plans not seen before, oldest first, at most 1,000 per database per run, so the first read of a
   large Query Store spreads over several collections; known plans only have their last execution time refreshed;
3. query text for new plans, only when asked.

A plan's identity is (plan id, plan hash), because clearing Query Store can reuse plan ids. References are mapped to
Dormant's table identities when a plan is first read. Plans not run within daily retention are pruned. A Query Store
that cannot be read is reported and never fails the collection. On SQL Server 2025 Express with about 2,000 plans, the
first two reads parsed 1,006 and 1,000 plans.

## D-047 · Hosting

`run` uses the .NET generic host with an empty builder: no `appsettings.json`, environment or command-line configuration
sources, so nothing outside the Dormant configuration file changes behaviour. The Windows Service and systemd
integrations are registered and do nothing when run interactively; installation scripts come in milestone 8. `run` logs
at Information unless `--log-level` is given. The store serialises its operations with a lock, because all instance
loops share its single SQLite connection.

## D-054 · The heatmap: UTC days, user reads, and no data drawn as no data

The heatmap has one column per UTC day, ending today (`--since`, default `classification.dormant_days`), and one row per
table, grouped by instance and database and sorted by size. A partitioned table also gets one row per partition, in range
order. Each cell is user reads outside maintenance windows, from the daily rollups. For a table, that is the sum over its
indexes. For a partition, it is the range's operational reads across the table's aligned indexes.

Each day falls into exactly one state:

| State | Rule | Drawn as |
|---|---|---|
| not yet seen | the object had not been seen by that day | empty |
| reads | at least one user read, however little of the day was observed | 4 shades: 1–9, 10–99, 100–999, 1,000+ |
| no reads | no reads, and at least `min_coverage` of the day observed | near-white, outlined |
| no data | no reads, and less than `min_coverage` of the day observed | hatched |

The threshold is the same `min_coverage` that classification uses, so the picture never claims more than the class does.
A day with an hour of gap is hatched even though classification counts its other 23 hours. Today is measured against
the part of the day that has elapsed. A partition's day loses the time its counters were unknown, when its table was read
that day (D-051), exactly as in classification. Every run of same-state days has a tooltip with its dates, reads (with
"at least" when a counter reset fell inside), the least-observed day and any reads inside maintenance windows. The time
axis marks engine restarts (filled triangles) and the database's counter resets (open circles).

*The drawing described in this paragraph was replaced by the cell matrix of D-064; the states above still hold, day by day.*
Pixels per day are 8, 4, 2 or 1 for windows up to 92, 184, 370 and 1,095 days, so 180 days take 720 px and fit an A4
landscape page with the name, size and class columns. Each row is one small inline SVG whose same-state days are merged
into one rectangle, which keeps a large estate's file small.

## D-055 · The report is one self-contained HTML file

`dormant report` writes a single HTML file with inline CSS and about 40 lines of inline script for sorting and filtering.
The page reads and prints fully without the script. A Content-Security-Policy meta tag (`default-src 'none'`) forbids
loading anything from elsewhere, so the file makes no requests even when opened on a connected machine. Every name and
text from the store is HTML-encoded.

Fonts: IBM Plex is named first in the font stack but not embedded. Embedding it would add about 300 KB of base64 to
every report and put third-party font files and their licence into the repository. The system font stack (Segoe UI,
system-ui, Helvetica, Arial) is used otherwise.

*Superseded by D-064, which replaces these colours with the Control room theme; the rest of this decision stands.*
Colours: ground `#F4F3EE`, ink `#17191B`, grey text `#585C60`, and the accent `#E3CF3F` only as a fill
behind dark text, for Dormant. The class colours run with the heat ramp: Hot is ink, Warm dark grey, Cold light grey,
Dormant accent, and Insufficient history hatched like "no data". Printing uses `@page { size: A4 landscape }`, starts
each section on a new page and keeps background colours (`print-color-adjust: exact`).

The report has six sections: summary, heatmap, Cold & Dormant by size, unused indexes, partitioned tables, and method &
caveats. Two choices within them:

- **Cold & Dormant** lists units (each partition of a partitioned table, each other table). The last known reader is the
  table's, labelled "(reads the table)" for a partition, because Query Store plans name tables, not ranges. Insufficient
  history is counted under the list, not listed, so that list holds only conclusions.
- **Unused indexes** show `USE [database];` before the `DROP INDEX` statement, so a pasted statement cannot run in the
  wrong database. A warning above them says what to check first.

The default file is `report.out_dir/dormant-report-<yyyyMMdd-HHmm UTC>.html`, so earlier reports are kept. The file is
written to a temporary name and moved into place, so a reader never sees half a report. On Linux, report and export
files are created 0600 (and a new directory 0700), like the store, since they hold the same names.

## D-056 · Exports: one schema for CSV and JSON

`dormant export --format csv|json --level table|index|partition` writes the classification with the same columns in both
formats, to standard output or `--out`. The levels:

- **table**: one row per table, with its class, coverage, last read and write, rows, size, cost, partition count and
  last known reader.
- **index**: one row per index or heap, each classified on its own reads, with its update count, its unused flag, and a
  DROP statement for unused ones only.
- **partition**: the units sizes add up over, as in the report: each partition of a partitioned table, each other table
  as one row (`all`).

Values:

- Dates are UTC days (`yyyy-MM-dd`); timestamps are ISO 8601 UTC.
- Sizes are bytes. Coverage is a fraction from 0 to 1, to 4 decimals.
- The estimated monthly cost is set only when prices are configured.
- Class values are `hot`, `warm`, `cold`, `dormant` and `insufficient_history`.
- Anything not known (no read seen, no reader, no price) is empty in CSV and `null` in JSON, never 0.

JSON wraps the rows with the format version (`dormant_export: 1`), the Dormant version, the as-of time, the level, the
thresholds and the currency. Text is written unescaped where JSON allows (UTF-8, `+`, `<`), since the file is data, not
HTML.

CSV follows RFC 4180: a header row, CRLF line ends, fields quoted when they contain commas, quotes or line breaks. Text
starting with `=`, `+`, `-`, `@`, tab or carriage return is prefixed with `'`, so a spreadsheet opening the file never
evaluates an object name as a formula (OWASP's CSV injection guidance). Numbers are never prefixed. CSV files written
with `--out` start with a UTF-8 byte order mark, so spreadsheets read non-ASCII names correctly. Standard output gets
no mark.

## D-057 · Report and export exit codes

`report` and `export` read only the local store and never connect to an instance. Without a store they say so on
standard error and exit with 4: there is no history to show, and a script expecting a file must not mistake that for
success. `status` keeps exiting 0 there, because saying "no history yet" is its whole answer. If the output file cannot
be written (bad path, no permission), they exit with 3: the problem is the path given, in `--out` or `report.out_dir`.
`--since` accepts 1 day up to `store.retention.daily`, since no daily history is kept beyond that.

## D-058 · Synthetic history is marked wherever it shows

The demo needs 200 days of history in minutes, so it backfills made-up readings. They must be clearly marked as demo
data, so made-up history can never pass for real:

- Store schema 6 adds `collection.synthetic`. A snapshot ingested with `IngestOptions.Synthetic` is recorded as such;
  everything else about it (deltas, rollups, coverage) is ordinary, so the demo exercises the real engine.
- `status` prints a `SYNTHETIC:` line with the number of made-up readings and the last one.
- The report opens with a "Contains synthetic demo data" notice naming the instance, count and end time.
- JSON exports carry `contains_synthetic_data`.

Only the demo tool writes synthetic readings. `dormant` itself has no option to.

## D-059 · The Windows Service

`dormant service install` registers the service with sc.exe. Sc.exe is part of Windows and its behaviour is
documented; P/Invoke into the service control manager would add code without adding safety.

- **Command line**: `"<dormant.exe>" run -c "<config>"`, with both paths quoted. The command refuses to register any
  executable other than an installed `dormant.exe` (not the `dotnet` host), and refuses to run without elevation.
- **Account**: the virtual account `NT SERVICE\Dormant` by default. It has no password and no rights beyond what is
  granted to it. It authenticates to remote SQL Servers as the computer account, so Windows authentication to other
  machines needs either that account's login or a group managed service account (`--account CORP\dormant$`).
  Passwords are never accepted.
- **Start**: automatic (delayed), so it does not compete with boot. It restarts after a failure: after 1 minute,
  again after 1 minute, then every 5 minutes, and the count resets after a day.
- **Files**: the configuration defaults to `%ProgramData%\Dormant\dormant.yaml`, written from the example if missing.
  - A directory the installer creates gets a protected ACL: Administrators and SYSTEM full control, the service account
    read (configuration) or modify (store, reports), nobody else.
  - An existing directory keeps its ACL and only gains the service account's entry.
- **Logs**: when running as a service, Dormant also logs to the Application event log, under the source `Dormant`,
  which the installer registers.
- **Uninstall**: stops and removes the service and the event source. It keeps the configuration and history.

The CI job `service-windows` installs the service with `deploy/windows/install.ps1` and starts it. It checks that the
service is running, has written its store and the event log, and that its directory's ACL admits no ordinary users.
Then it removes the service.

## D-060 · systemd and the container image

**systemd** (`deploy/systemd`):

- `Type=notify`, through `Microsoft.Extensions.Hosting.Systemd`.
- A system user `dormant` with no login shell and no home directory.
- Configuration in `/etc/dormant`: `root:dormant`, 0750 for the directory and 0640 for the file.
- Connection strings in `/etc/dormant/dormant.env`, 0600 root, read by systemd before the service starts, for `env:`
  references.
- State in `StateDirectory=dormant` (`/var/lib/dormant`, 0700), with `UMask=0077`.
- `ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`, no capabilities, `NoNewPrivileges`,
  `RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX`, `SystemCallFilter=@system-service`.
- `MemoryDenyWriteExecute` is not set, because the .NET JIT needs executable memory it has written.
- The single-file executable unpacks its native libraries under the state directory
  (`DOTNET_BUNDLE_EXTRACT_BASE_DIR`), since the service has no home.

The CI job `service-linux` installs the unit, collects from a real SQL Server as a login granted only by the shipped
script, and checks the modes and the collection.

**Container** (`deploy/docker/Dockerfile`):

- The self-contained executable on `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra`: no shell, no
  package manager, non-root user 1654. The `-extra` variant includes ICU, which globalization needs.
- The base tag floats within 10.0, so a rebuild picks up security patches; the SDK is pinned to the version in
  `global.json`.
- State is the volume `/var/lib/dormant`; configuration is mounted at `/etc/dormant`.
- The `demo` target adds `dormant-demo`.

## D-061 · Permission scripts are the tested artefact

`deploy/sql` holds the least-privilege scripts for SQL Server 2022 and later, SQL Server 2016 to 2019 (also Managed
Instance and RDS for those versions), and Azure SQL Database. They use sqlcmd variables (`:setvar DormantLogin`), so
they run unchanged in sqlcmd and in SSMS's SQLCMD mode. They grant, and never create a login: the commented lines show
the choices. `ValidateTests` runs the scripts themselves on SQL Server 2019, 2022 and 2025, not a copy of their grants,
and `validate` must then pass every check. The demo grants its login with the same script. A unit test checks that no
script grants anything that writes or controls.

## D-062 · The demo

`deploy/demo/docker-compose.yml` runs four services:

- **SQL Server 2022.**
- **setup** (`dormant-demo setup`):
  - creates `DormantDemo`: orders on 26 monthly partitions over two years, customers, products, an archive, an audit
    log, an index that nothing reads;
  - creates the `dormant` login with the shipped grant script;
  - restarts SQL Server, so that loading data and building indexes and statistics, which read every partition, is not
    counted as use;
  - backfills 200 days of synthetic readings every 3 hours (D-058), built from a real snapshot so every identity, size
    and range is real;
  - ends with that real reading.
- **workload** (`dormant-demo workload`): the shop application, reading the last three months and placing orders
  through stored procedures, so Query Store can name readers.
- **dormant** (`dormant run`): collects every 5 minutes as the `dormant` login.

What is planted, and must be found exactly (`DemoPlan`):

| Object | Planted as |
|---|---|
| Orders partitions before month -8 | Dormant (17, including the empty one before the oldest boundary) |
| Orders months -7 and -6 | Cold |
| Orders months -5 to -3, Products | Warm |
| Orders months -2 to +1, Customers, Orders as a whole | Hot |
| OrderArchive | Dormant |
| AuditLog | Dormant, write-only |
| IX_Orders_Status | the only unused index (UX_Customers_Email is unique and never read, and must not be listed) |

The demo database turns off automatic statistics after building them. SQL Server's automatic updates scan whole indexes,
and partition-level counters cannot tell those scans from a user's, so old partitions would look read. That is a known
limitation (roadmap) and is stated in the report. Hiding it in the demo would be wrong, but so would letting it hide what
the demo is for. The demo database is the only place this setting is changed; Dormant never changes anything.

The outcome is checked three times:

1. `DemoBackfillTests`: through the store and classifier, without SQL Server.
2. `DemoTests`: against a real SQL Server container.
3. The CI job `container`, which runs the compose file and checks the exported classes with jq.

## D-063 · Performance and locking

`PerformanceTests` builds 50 databases with 100 tables each: a clustered primary key and three nonclustered indexes per
table, so 20,000 indexes in all, and ten tables per database on 25 monthly partitions. Another session holds
`TABLOCKX, HOLDLOCK` on a user table throughout the collection, and `sys.dm_tran_locks` is sampled every 50 ms for
locks held by sessions named `Dormant`. The test fails if:

- any database is not collected, or there are fewer than 20,000 indexes;
- any lock on a user table's rows, pages or partitions, or any object lock on a user table other than schema stability,
  is seen;
- collection, collection plus the first ingest, or a second collection plus ingest takes 30 seconds or more.

Schema-stability locks are expected: reading metadata takes them briefly, and they conflict only with schema changes.
The measured times, and each statement's compile and run time, are written to the test log.

Measured on a GitHub Actions Linux runner (2 vCPUs), SQL Server 2022 in a container, the test running alone:

| | Time |
|---|---|
| First collection: 50 databases, 20,000 indexes, 68,000 partitions | 9.7 s and 16.9 s in two runs |
| First ingest into the store | 2.1 s and 4.1 s |
| Second collection and ingest (steady state) | 3.0 s and 5.5 s |

Shared runners vary by run; the budget is checked on every push. No data locks were seen (161 and 285 samples). A first collection is dominated by compiling: each database compiles its own plan
for each statement, and catalog views are expensive to compile (in that run 6.7 s of compile time, against 2.6 s of
execution). Plans stay cached between collections, which is why the steady state is three times faster. Two findings
shaped the test:

- Splitting the partitions statement into smaller ones raised the total compile time from 13.7 s to 23.2 s, because
  every catalog view costs about 70 ms to compile per database. Fewer, larger statements are cheaper, so the collector
  keeps them (D-040).
- Run in parallel with the other container suites, the same collection took 19.6 to 31.8 s: that measured the shared
  runner, not Dormant. The test therefore runs alone, after the others (xunit `DisableParallelization`).

## D-064 · The report's look: the Control room theme, and the heatmap as a matrix

The owner chose a look of Dormant's own. This replaces the colours of D-055 and the drawing of D-054. The rules for what a day shows are unchanged.

**Theme.**

- **Screen:** dark. Page `#0B111B`, panels `#111A27`, ink `#D9E2EC`, grey `#8FA1B5`.
- **Heat ramp:** a single teal hue rising from the panel's own tone to near-white: `#1E2A3B` (no reads), `#1F4E5F`,
  `#2A8C8C`, `#5FD3B8`, `#C8FFE9`. "No reads" is a visible dark cell, not the background.
- **Classes:** Hot, Warm and Cold follow the ramp. Amber `#FFB547` marks Dormant and nothing else, the one class someone
  might act on. Insufficient history is hatched like "no data".
- **Type:** numbers, sizes and dates are set in a monospaced, tabular face, so they line up.
- **Print:** every colour is a CSS variable, and `@media print` redefines them as a light version of the same palette
  (white page, the ramp running light to dark teal). A dark page would waste ink and print poorly.

**The matrix.** The heatmap is a matrix of discrete cells with gaps between them, like a classic heatmap:

- **Cells:** one cell per UTC day for windows up to 90 days, one per ISO week (Monday to Sunday) beyond, so the default
  180 days is 26 or 27 columns. The first and last week can be partial; their tooltips say how many days they hold.
- **A cell's state**, from its days, in this order:
  - not yet seen, if the object was present on none of them;
  - shaded, if any reads were seen, by the busiest day's reads (1–9, 10–99, 100–999, 1,000+), so one legend serves both
    cell sizes;
  - "no reads" only if every present day was observed at least `min_coverage`;
  - otherwise no data, hatched.

  A week with one poorly observed day and no reads is therefore unknown, never idle.
- **Size:** columns take at most 720 px, at 3 to 24 px per column with a 1 or 2 px gap, so any window fits an A4
  landscape page.
- **File size:** each row is a small SVG. A cell with reads is its own path with its own tooltip (the dates, total reads,
  the busiest day). Consecutive cells without reads, without data or not yet seen share one path and one tooltip, which
  keeps a large estate's file small.
- **Axis:** months are labelled where they begin, and engine restarts and counter resets are marked in the column that
  holds them.

`HeatmapCellsTests` covers both groupings, partial weeks, the busiest-day shading, a week that is "no reads" only when
fully observed, and that every window fits the page.

## D-065 · Licence: MIT

Dormant is released under the MIT licence (`LICENSE`), copyright "Dormant contributors". This supersedes D-022.

## D-066 · Releases

A version tag (`v1.2.3`) runs `.github/workflows/release.yml`:

1. **Tests:** the full suite, with the SQL Server container suites required.
2. **Binaries:** the three single-file builds, stamped with the tag's version (`-p:Version`), packaged with the licence,
   the README, the permission scripts and the service files for their platform: `dormant-<version>-win-x64.zip`,
   `dormant-<version>-linux-x64.tar.gz` and `dormant-<version>-linux-arm64.tar.gz`.
3. **Release:** a GitHub release with those packages, a `SHA256SUMS` file and generated release notes.
4. **Image:** the container image for `linux/amd64` and `linux/arm64`, built from `deploy/docker/Dockerfile` and pushed
   to `ghcr.io/<owner>/dormant` tagged with the version and `major.minor`.

A tag with a suffix (`v1.2.3-rc.1`) is a pre-release: it moves neither `latest` nor `major.minor`. The image builds both
architectures without emulation: the SDK stage runs on the build machine and publishes for the target, and the final
stage only copies files. Windows executables are not code-signed yet (roadmap).
