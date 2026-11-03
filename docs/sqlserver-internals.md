# SQL Server internals Dormant relies on

Every source Dormant reads, what Microsoft Learn says about its columns, scope, permissions and reset behaviour, and
whether an integration test covers it yet. "Learn" means documented; "tested" means a test in
`tests/Dormant.Integration` checks it against a real SQL Server container; CI runs these on every push (Linux runners,
SQL Server 2019, 2022 and 2025 images). First passing run: 2026-11-03.

Checked against Microsoft Learn on 2026-11-02.

## Instance identity and restarts

### `sys.dm_os_sys_info`

- `sqlserver_start_time` (**datetime**): "the local system date and time SQL Server last started". Learn points to it as
  the way to know since when DMV counters have been accumulating.
- `sqlserver_start_time_ms_ticks` and `ms_ticks` (**bigint**): tick counts at start and now; used to derive the start
  time in UTC without depending on the server's time zone (decision D-019).
- Permissions: `VIEW SERVER STATE` on SQL Server 2019 and earlier and on Managed Instance; `VIEW SERVER PERFORMANCE
  STATE` on SQL Server 2022 and later. On Azure SQL Database, `VIEW DATABASE STATE`, except Basic, S0, S1 and elastic
  pools, which need the server admin, the Entra admin or membership in `##MS_ServerStateReader##`.
- Restart detection uses `sqlserver_start_time_ms_ticks` (fixed per process) and `ms_ticks` (goes backwards only when
  the host reboots), not the local `sqlserver_start_time` (decision D-025).
- Status: permissions **tested** for SQL Server 2019, 2022 and 2025 by `ValidateTests`; a restart between two
  collections is **tested** on 2022 by `RestartTests` (fault scenario 1).

### `SERVERPROPERTY`

- `EngineEdition`: 1–4 boxed editions, 5 Azure SQL Database, 8 Azure SQL Managed Instance (6, 9, 11 and others are
  Synapse, Edge and similar, which Dormant does not support).
- `ProductVersion`: Azure SQL Database and Managed Instance report a fixed `12.0.x`; the major version is meaningful
  only for boxed SQL Server and RDS.
- Readable by any login.

## Databases

### `sys.databases`

- Visible to every login while `VIEW ANY DATABASE` is granted to `public` (the default). Reading it does not open
  databases.
- `state_desc`, `user_access_desc`, `is_auto_close_on` decide whether Dormant may touch a database at all (D-017,
  D-030). Learn: "If a database isn't ONLINE, or AUTO_CLOSE is set to ON and the database is closed, the values of some
  columns can be NULL", and OFFLINE rows are visible only with `ALTER ANY DATABASE` or `CREATE DATABASE` in master.
- `create_date`: on SQL Server, changes on rename. On Azure SQL Database it "doesn't change when a rename operation is
  performed", and a restore or copy creates a database with a new one. Dormant uses it as the database identity on
  Azure SQL Database (D-031).
- `physical_database_name` (Azure SQL Database): changes when the service objective changes or the database is restored,
  so it is not a stable identity.
- `database_id` on Azure SQL Database is unique within the logical server but not joinable with other views; Dormant
  matches the current database by name there.

### `sys.database_recovery_status`

- Applies to SQL Server and Azure SQL Managed Instance only, **not Azure SQL Database**.
- "If the database is not opened, the Database Engine tries to start it": reading it wakes AUTO_CLOSE databases.
- `database_guid`: "Only one database should ever have this GUID, but duplicates can be created by copying and
  attaching databases. RESTORE always generates a new GUID when you restore a database that does not yet exist." NULL
  while the database is offline.
- Rows for databases other than `master` and `tempdb` are visible to the owner, or with `ALTER ANY DATABASE` or `VIEW
  ANY DATABASE`, or `CREATE DATABASE` in master.
- Use: detach and reattach keeps the GUID (the files carry it); restore-as-new and copy do not, which is right for
  history (a restored copy is a different database). Dormant reads the GUID only from inside the database it is
  already collecting (`WHERE database_id = DB_ID()`), and never for AUTO_CLOSE databases, which it skips. Detach and
  reattach keeping one identity is **tested** by `CollectionTests` (fault scenario 7).

## Usage

### `sys.dm_db_index_usage_stats`

- One row per index that has been used since the counters were last initialised. Columns: `database_id`, `object_id`,
  `index_id`; `user_seeks`, `user_scans`, `user_lookups`, `user_updates` and their `last_user_*` times; the same for
  `system_*`.
- "Every individual seek, scan, lookup, or update on the specified index by one query execution is counted as a use."
  `user_updates` counts statements, not rows ("if you delete 1000 rows in one statement, this count increments by 1").
- `system_*` covers internally generated queries, "such as scans for gathering statistics". Dormant never counts them
  as use.
- **Resets**: "The counters are initialized to empty whenever the database engine is started." "Whenever a database is
  detached or is shut down (for example, because AUTO_CLOSE is set to ON), all rows associated with the database are
  removed." "When an index is used, a row is added ... if a row does not already exist", with counters starting at
  zero.
- What Dormant concludes (D-026, D-028): no row, in a database read continuously since the counters started, means
  "not used since then". A row that vanishes, or a counter that goes backwards, for an index that still exists, means
  the database's counters restarted at an unknown time: the interval is a reset and nothing in it counts as observed.
- Index rebuilds: SQL Server 2012 and 2014 builds were widely reported to clear an index's row on rebuild; Learn does
  not say. `CollectionTests.Index_rebuild_does_not_reset_usage_counters` checks that SQL Server 2022 keeps the row. If a
  build does clear it, the rebuild shows as a database reset (safe, at the cost of one interval of coverage).
- Not covered: memory-optimized indexes and spatial indexes. Dormant reads `sys.tables.is_memory_optimized` and never
  classifies those tables (D-049); spatial indexes are never reported as unused (D-051).
- Azure SQL Database: rows for other tenants are filtered out; `database_id` is unique within a database or elastic
  pool, not within a logical server. Serverless databases also reset these counters on every resume (community
  reports; not yet tested).
- Permissions: as `sys.dm_os_sys_info` above. Status: readability **tested** for 2019, 2022 and 2025 by
  `ValidateTests`.

### `sys.dm_db_partition_stats`

- One row per partition in the current database: `partition_id`, `object_id`, `index_id`, `partition_number`,
  page counts (`in_row_*`, `lob_*`, `row_overflow_*`, `used_page_count`, `reserved_page_count`) and `row_count`
  ("approximate").
- Permissions: `VIEW DATABASE STATE` **and** `VIEW DEFINITION`; on SQL Server 2022 and later `VIEW DATABASE PERFORMANCE
  STATE` and `VIEW SECURITY DEFINITION`.
- Status: readability **tested** for 2019, 2022 and 2025 with `VIEW SERVER [PERFORMANCE] STATE` + `VIEW ANY DEFINITION`
  + `CONNECT ANY DATABASE`. Whether `VIEW SECURITY DEFINITION` alone suffices on 2022+ is untested.

### `sys.dm_db_index_operational_stats`

- A table-valued function; Dormant calls it as `(DB_ID(), NULL, NULL, NULL)` inside each database. Learn warns that a
  NULL `database_id` means every database; `DB_ID()` in a database context is never NULL.
- Columns used: `range_scan_count` ("range and table scans started"), `singleton_lookup_count` ("single row
  retrievals"), `leaf_insert_count`, `leaf_update_count`, `leaf_delete_count` and `leaf_ghost_count`. Learn: deletes
  that are ghosted first count in `leaf_ghost_count` instead of `leaf_delete_count`, so Dormant adds the two. All per
  partition (`partition_number`, 1-based). `hobt_id` here identifies columnstore internal rowsets, not the partition;
  Dormant sums by partition and does not use it.
- **Reset and removal** (Learn, "How the counters in the metadata cache are reset"): the counters "exist only as long
  as a metadata cache object that represents the heap or B-tree is available", "are set to zero whenever the metadata
  ... is brought into the metadata cache", and are removed when a table or index is dropped or a partition is
  truncated; "other DDL operations against the index might cause the value of the statistics to be reset to zero".
  An active object "commonly has its metadata in the cache"; a less active one may move in and out, "particularly ...
  under memory pressure".
- Learn: "you can't use these counters to conclusively determine whether an index was used or not". Dormant uses them
  only as supporting, per-partition evidence, with exact deltas only when continuity is provable (D-038).
- Not covered: indexes on memory-optimized tables.
- Permissions: `VIEW DATABASE STATE` (or `VIEW DATABASE PERFORMANCE STATE`) when `object_id` is not given; covered by the
  server-level grants in `permissions.md`.
- Performance: about 130 ms for 1,000 cached partitions on SQL Server 2025 Express.
- **Observed in testing (SQL Server 2022):** `range_scan_count` rises on a partition for activity that served no user
  rows from it:
  - an automatic statistics update after a load scans every partition once (system activity, which this DMV does not
    separate from user activity);
  - a query whose range ends exactly at a boundary (`OrderDate < '2021-04-01'` with RANGE RIGHT) also starts a range scan
    in the partition that holds that boundary value, which returns nothing from it.

  Both make partitions look more used, never less, so they can delay a Cold or Dormant label but never cause one. The
  first was found when the scenario 6 test failed in CI.

## Known limits of usage history

- A database taken offline and brought back online between two collections, where none of its indexes had a usage row
  before, leaves no trace: a read in that window before the outage is lost. The window is at most one interval wide
  (D-029).
- AUTO_CLOSE databases are not collected at all (D-030).
- Memory-optimized and spatial indexes have no rows in `sys.dm_db_index_usage_stats`; their tables' other indexes are
  still tracked.
- `last_user_*` timestamps are server-local and are converted with the current UTC offset (D-034).

## Partitions

### `sys.partitions`

- One row per partition of every table and "most types of indexes"; full-text, spatial and XML indexes are not
  included. Every table and index has at least one partition; unpartitioned ones have `partition_number` 1.
- `partition_id` is unique within a database and is what `sys.dm_db_partition_stats` joins on. Dormant compares it
  between readings: a different `partition_id` for the same boundary range means a new rowset (D-039).
- `data_compression_desc`: NONE, ROW, PAGE, COLUMNSTORE, COLUMNSTORE_ARCHIVE.
- Visible through metadata visibility (`VIEW DEFINITION`).

### `sys.partition_schemes`, `sys.partition_functions`, `sys.partition_range_values`

- An index is partitioned when `sys.indexes.data_space_id` is a partition scheme; the scheme names its function.
- `sys.partition_range_values.boundary_id` is "1-based ordinal ... with left-most boundary starting at an ID of 1".
  So partition *n* lies between boundary *n − 1* and boundary *n*; the first and last partitions are open-ended.
- `sys.partition_functions.boundary_value_on_right`: RANGE RIGHT puts a boundary value in the partition to its right
  (lower bound inclusive); RANGE LEFT in the one to its left (upper bound inclusive).
- `value` is **sql_variant**; its base type comes from `SQL_VARIANT_PROPERTY(value, 'BaseType')`. Dormant converts it to
  text with an explicit lossless style per type (D-039).
- The partitioning column is the index column with `sys.index_columns.partition_ordinal = 1`.
- SPLIT and MERGE keeping each range's history is **tested** by `CollectionTests` (fault scenario 6).

### `sys.dm_db_partition_stats`

- Joined to `sys.partitions` in the collector, never in SQL: inside a larger join SQL Server evaluated it once per row
  (26.6 s against 16 ms; D-040).

## Query Store

### `sys.database_query_store_options`

- `actual_state_desc`: `OFF`, `READ_ONLY`, `READ_WRITE`, `ERROR`. Query Store is on by default for new databases from
  SQL Server 2022.
- Requires `VIEW DATABASE STATE`.

### `sys.query_store_plan`

- `query_plan` is showplan XML as **nvarchar(max)**; `last_execution_time` is **datetimeoffset** and "refers to the last
  end time of the query/plan".
- `query_plan_hash` (**binary(8)**) is read with plan_id as the plan's identity (D-053).
- Permissions: `VIEW DATABASE STATE`; on SQL Server 2022 and later `VIEW DATABASE PERFORMANCE STATE`. Covered by the
  grants in `permissions.md`.

### `sys.query_store_query`, `sys.query_store_query_text`

- `query_hash` identifies the query without its text; `object_id` is the module (procedure, function, trigger) the query
  belongs to, 0 for ad-hoc queries; `query_sql_text` is read only with `store_text: true`, truncated to 4,000 characters.

### Showplan XML

- Namespace `http://schemas.microsoft.com/sqlserver/2004/07/showplan`. Seeks, scans and lookups are `RelOp` elements
  containing `IndexScan` (lookups with `Lookup="1"`) or `TableScan`, each with an `Object` naming database, schema,
  table and index in brackets. Insert, update and delete targets are `Object` elements inside `Update`,
  `SimpleUpdate` or `ScalarInsert`. `ColumnReference` elements also carry table names and are not accesses.
- Parsed with a streaming reader that refuses DTDs.
- **Observed in testing (SQL Server 2022):** Query Store captures the engine's internal statistics queries,
  `SELECT StatMan([SC0], ...) FROM (SELECT TOP 100 PERCENT ... FROM [dbo].[Orders] WITH (READUNCOMMITTED) ...) AS
  _MS_UPDSTATS_TBL`, with statement type SELECT. Their plans scan the table. Dormant recognises them by statement text
  and does not report them as readers.

### Query Store and Dormant's own queries

- Dormant's catalog and DMV queries run inside each monitored database, so where Query Store captures all queries (or
  AUTO decides they are worth keeping) they appear in that database's Query Store, as they do in its plan cache. They
  read only system views and are never attributed to user tables. This is the engine recording a read; Dormant writes
  nothing.
- **Tested** on SQL Server 2022 by `CollectionTests`: a stored procedure reading a table is named as its reader, and
  query text is kept only when asked.

## Sources

- [sys.dm_db_index_usage_stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-db-index-usage-stats-transact-sql)
- [sys.dm_os_sys_info](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-os-sys-info-transact-sql)
- [sys.dm_db_partition_stats](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-db-partition-stats-transact-sql)
- [sys.database_recovery_status](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-recovery-status-transact-sql)
- [sys.query_store_plan](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-plan-transact-sql)
