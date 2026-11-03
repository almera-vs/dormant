# Fault scenarios

Dormant is tested against eleven faults. Each must produce the correct history and never a false Cold or Dormant. This
page says where each is proven. Unit tests check the rule exactly; store tests check it end to end through ingestion,
rollups and classification; container tests check it against a real SQL Server 2022 (Linux CI runs them on every push
and must never skip them).

| # | Fault | Rule | Unit | Store | Real SQL Server |
|---|---|---|---|---|---|
| 1 | Instance restart mid-window | Counters after the restart count from zero, from the restart time; the restart is marked. | `DeltaEngineTests.Scenario_1_…` | `RollupTests` | `RestartTests.Scenario_1_restart_between_collections_is_stitched_and_marked` |
| 2 | Agent down for 6 hours | A gap: not observed, never inactivity; reads in it still count. | `DeltaEngineTests.Scenario_2_…` | `RollupTests.Scenario_2_six_hours_of_agent_downtime_…` | `CollectionTests.Scenario_2_time_without_a_reading_is_a_gap_never_inactivity` (time compressed: a 2-second maximum span) |
| 3 | One object's counters decrease | A reset: the interval is not observed, counts are lower bounds, never negative. | `DeltaEngineTests.Scenario_3_…` (one object) | | `CollectionTests.Scenario_3_counters_that_go_backwards_are_a_reset_never_negative_use` (the database taken offline and back) |
| 4 | Table dropped and recreated with the same name | A new identity `(object_id, create_date)`: separate histories. | | `DormantStoreTests.Scenario_4_…` | `CollectionTests.Scenario_4_…` |
| 5 | Table renamed | Same identity, one history under the new name. | | `DormantStoreTests.Scenario_5_…` | `CollectionTests.Scenario_5_…` |
| 6 | Partition SPLIT and MERGE | Partitions are keyed by boundary range, not number. | | `PartitionStoreTests.Scenario_6_…` | `CollectionTests.Scenario_6_…` |
| 7 | Database detached and reattached | Same database by `database_guid`; the new `database_id` marks a reset. | `DeltaEngineTests.Scenario_7_…` | `DormantStoreTests.Scenario_7_…` | `CollectionTests.Scenario_7_…` |
| 8 | Object missing from `index_operational_stats` | Unknown, never zero; unknown time is not observed time for that partition. | `PartitionDeltaEngineTests.Scenario_8_…` | `PartitionStoreTests.Scenario_8_…`, `DailyUsageTests` | Not forced: SQL Server offers no supported way to evict one object's metadata on demand (roadmap). |
| 9 | Reads only inside the maintenance window | Recorded apart, not counted as use; the explanation says so. | | `RollupTests.Scenario_9_…`, `ClassificationTests.Scenario_9_…` | `CollectionTests.Scenario_9_…` |
| 10 | 30 days of history, 180-day threshold | Insufficient history, never Dormant. | `ClassifierTests.Scenario_10_…` | `ClassificationTests.Scenario_10_…` | Needs 30 real days; the store test drives the same code with daily readings. |
| 11 | A unique index with zero reads | Never listed as unused. | | `ClassificationTests.Scenario_11_…` | `DemoTests` (UX_Customers_Email, written to and never read) |

Beyond the eleven:

- `DemoTests` and the CI job `container` check that the docker-compose demo finds what was planted and nothing else.
- `PerformanceTests` checks the 30-second budget on 50 databases and 20,000 indexes, and that no data lock is taken on a
  user table (D-063).
- `Memory_optimized_tables_are_never_judged` covers the one object type whose usage SQL Server never reports (D-049).
