# Roadmap

## v1 milestones

| # | Milestone | Status |
|---|---|---|
| 1 | Skeleton: solution layout, CI, analyzers, configuration loading and validation, `init`, `validate`, `version` | done |
| 2 | Collector and store: usage statistics, SQLite store, delta engine, restart detection, `collect --once`, `status` | done |
| 3 | Size and partitions: partition statistics, boundary labels, operational statistics, identity across renames, recreations and SPLIT/MERGE | done |
| 4 | History: rollups, coverage, gaps, maintenance windows, long-running `run` | done |
| 5 | Classification: Hot, Warm, Cold, Dormant, Insufficient history; write-only; unused indexes; cost model | done |
| 6 | Attribution: Query Store reader, plan XML parser, last known reader | done |
| 7 | Report and exports: HTML heatmap with all six sections, CSV and JSON | done |
| 8 | Hosting and hardening: Windows Service, systemd, container, full fault suite, permission scripts, threat model, demo, 15-minute quickstart | done |

## After v1 (designed for, not built)

- Archiving or moving cold data, including tiering to Iceberg tables on S3-compatible storage.
- A PostgreSQL collector behind `ICollector`.
- A web UI or hosted dashboard.
- Alerting.
- Login-level attribution through Extended Events.
- Automatic index drops.

## Open items carried forward

- Azure SQL Database: identity by `create_date` is implemented (D-031) but not yet tested against a real Azure database.
- Optional collection of AUTO_CLOSE databases for operators who accept waking them (D-030).
- Detecting a database that went offline and back within one interval without leaving a usage-row trace (D-029).
- Fault scenario 8 is tested with simulated readings; forcing metadata-cache eviction in a container test is not yet done
  (docs/fault-scenarios.md).
- Testing the Windows Service under a group managed service account; CI uses the virtual account.
- Integration tests against Azure SQL Database, Managed Instance, Amazon RDS and Entra ID authentication.
- Whether `VIEW SECURITY DEFINITION` alone is enough on SQL Server 2022 and later.
- Reporting days in a configured time zone instead of UTC (D-043, D-054).
- Embedding IBM Plex in the report, if a licence-clean way to ship the font files is chosen (D-055).
- A heatmap for estates of tens of thousands of tables: paging or collapsing databases, if file size or browser speed
  becomes a problem (D-054).
- Correcting partition reads for automatic statistics updates, e.g. from `sys.dm_db_stats_properties.last_updated`
  per collection, so old partitions of busy tables can reach Cold and Dormant (D-051).
- Code-signing the Windows executable, so SmartScreen does not warn on first run.
