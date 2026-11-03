using Microsoft.Data.Sqlite;

namespace Dormant.Store;

/// <summary>
/// The store's schema, upgraded in place through numbered migrations recorded in PRAGMA user_version.
/// Times are UTC, stored as Unix milliseconds. The store holds metadata and counters only, never row data.
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 6;

    private static readonly string[] Migrations =
    [
        // 1: instances, collections, identities, intervals and deltas, baselines for the next comparison.
        """
        CREATE TABLE instance (
            instance_key    INTEGER PRIMARY KEY,
            name            TEXT    NOT NULL UNIQUE,
            created_ms      INTEGER NOT NULL
        );

        -- One row per collection attempt, successful or not.
        CREATE TABLE collection (
            collection_id   INTEGER PRIMARY KEY,
            instance_key    INTEGER NOT NULL REFERENCES instance,
            started_ms      INTEGER NOT NULL,
            duration_ms     INTEGER NOT NULL,
            outcome         TEXT    NOT NULL CHECK (outcome IN ('ok', 'partial', 'failed')),
            error           TEXT,
            server_name     TEXT,
            platform        TEXT,
            product_version TEXT,
            edition         TEXT,
            engine_start_ms INTEGER,
            database_count  INTEGER NOT NULL,
            index_count     INTEGER NOT NULL
        );
        CREATE INDEX ix_collection_instance ON collection (instance_key, started_ms);

        -- A database, by identity that survives renames and detach/attach (database_guid, or create_date on Azure).
        CREATE TABLE db (
            database_key    INTEGER PRIMARY KEY,
            instance_key    INTEGER NOT NULL REFERENCES instance,
            identity        TEXT    NOT NULL,
            name            TEXT    NOT NULL,
            first_seen_ms   INTEGER NOT NULL,
            last_seen_ms    INTEGER NOT NULL,
            UNIQUE (instance_key, identity)
        );

        -- What each collection did with each database.
        CREATE TABLE db_observation (
            collection_id   INTEGER NOT NULL REFERENCES collection,
            database_key    INTEGER NOT NULL REFERENCES db,
            outcome         TEXT    NOT NULL CHECK (outcome IN ('collected', 'skipped', 'failed')),
            detail          TEXT,
            PRIMARY KEY (collection_id, database_key)
        ) WITHOUT ROWID;

        -- A table or indexed view: (object_id, create_date). Dropped and recreated means a new row; renamed does not.
        CREATE TABLE obj (
            object_key      INTEGER PRIMARY KEY,
            database_key    INTEGER NOT NULL REFERENCES db,
            object_id       INTEGER NOT NULL,
            create_date     TEXT    NOT NULL,
            schema_name     TEXT    NOT NULL,
            name            TEXT    NOT NULL,
            type            TEXT    NOT NULL,
            first_seen_ms   INTEGER NOT NULL,
            last_seen_ms    INTEGER NOT NULL,
            UNIQUE (database_key, object_id, create_date)
        );

        -- An index (or heap) of an object: (index_id, name, type). A change of name or type starts a new history.
        CREATE TABLE idx (
            index_key            INTEGER PRIMARY KEY,
            object_key           INTEGER NOT NULL REFERENCES obj,
            index_id             INTEGER NOT NULL,
            name                 TEXT,
            type                 TEXT    NOT NULL,
            is_primary_key       INTEGER NOT NULL,
            is_unique            INTEGER NOT NULL,
            is_unique_constraint INTEGER NOT NULL,
            is_disabled          INTEGER NOT NULL,
            first_seen_ms        INTEGER NOT NULL,
            last_seen_ms         INTEGER NOT NULL
        );
        CREATE INDEX ix_idx_object ON idx (object_key, index_id);

        -- The time between two readings of a database, and how much of it was observed.
        CREATE TABLE usage_interval (
            interval_id      INTEGER PRIMARY KEY,
            database_key     INTEGER NOT NULL REFERENCES db,
            collection_id    INTEGER NOT NULL REFERENCES collection,
            from_ms          INTEGER NOT NULL,
            to_ms            INTEGER NOT NULL,
            observed_from_ms INTEGER,
            kind             TEXT    NOT NULL CHECK (kind IN ('continuous', 'gap', 'instance_restart', 'reset')),
            reset_at_ms      INTEGER,
            reason           TEXT
        );
        CREATE INDEX ix_usage_interval_db ON usage_interval (database_key, to_ms);

        -- Activity per index per interval. Only indexes with activity have a row.
        CREATE TABLE index_delta (
            interval_id         INTEGER NOT NULL REFERENCES usage_interval,
            index_key           INTEGER NOT NULL REFERENCES idx,
            user_seeks          INTEGER NOT NULL,
            user_scans          INTEGER NOT NULL,
            user_lookups        INTEGER NOT NULL,
            user_updates        INTEGER NOT NULL,
            system_seeks        INTEGER NOT NULL,
            system_scans        INTEGER NOT NULL,
            system_lookups      INTEGER NOT NULL,
            system_updates      INTEGER NOT NULL,
            lower_bound         INTEGER NOT NULL,
            last_user_read_ms   INTEGER,
            last_user_update_ms INTEGER,
            PRIMARY KEY (interval_id, index_key)
        ) WITHOUT ROWID;
        CREATE INDEX ix_index_delta_index ON index_delta (index_key);

        -- The last reading of each database, kept for the next comparison.
        CREATE TABLE db_baseline (
            database_key        INTEGER PRIMARY KEY REFERENCES db,
            observed_ms         INTEGER NOT NULL,
            database_id         INTEGER NOT NULL,
            engine_start_ticks  INTEGER NOT NULL,
            engine_ms_ticks     INTEGER NOT NULL,
            engine_start_ms     INTEGER NOT NULL,
            interrupted         INTEGER NOT NULL,
            interrupted_reason  TEXT
        );

        CREATE TABLE index_baseline (
            index_key           INTEGER PRIMARY KEY REFERENCES idx,
            database_key        INTEGER NOT NULL REFERENCES db,
            has_usage           INTEGER NOT NULL,
            user_seeks          INTEGER NOT NULL,
            user_scans          INTEGER NOT NULL,
            user_lookups        INTEGER NOT NULL,
            user_updates        INTEGER NOT NULL,
            system_seeks        INTEGER NOT NULL,
            system_scans        INTEGER NOT NULL,
            system_lookups      INTEGER NOT NULL,
            system_updates      INTEGER NOT NULL,
            last_user_read_ms   INTEGER,
            last_user_update_ms INTEGER
        );
        CREATE INDEX ix_index_baseline_db ON index_baseline (database_key);
        """,

        // 2: partitions, keyed by the range of values they hold; their size; their operational activity.
        """
        -- A partition of an index, identified by its range (decision D-039), not by its number.
        CREATE TABLE part (
            partition_key    INTEGER PRIMARY KEY,
            index_key        INTEGER NOT NULL REFERENCES idx,
            range_key        TEXT    NOT NULL,
            label            TEXT    NOT NULL,
            range_text       TEXT    NOT NULL,
            partition_number INTEGER NOT NULL,
            partition_id     INTEGER NOT NULL,
            row_count        INTEGER NOT NULL,
            reserved_pages   INTEGER NOT NULL,
            used_pages       INTEGER NOT NULL,
            compression      TEXT    NOT NULL,
            first_seen_ms    INTEGER NOT NULL,
            last_seen_ms     INTEGER NOT NULL,
            UNIQUE (index_key, range_key)
        );

        -- Size history: one row per partition per UTC day on which its size changed (the day's last reading).
        CREATE TABLE part_size (
            partition_key   INTEGER NOT NULL REFERENCES part,
            day             INTEGER NOT NULL,
            row_count       INTEGER NOT NULL,
            reserved_pages  INTEGER NOT NULL,
            used_pages      INTEGER NOT NULL,
            PRIMARY KEY (partition_key, day)
        ) WITHOUT ROWID;

        -- Operational activity per partition per interval. Only partitions with activity have a row.
        CREATE TABLE part_delta (
            interval_id       INTEGER NOT NULL REFERENCES usage_interval,
            partition_key     INTEGER NOT NULL REFERENCES part,
            range_scans       INTEGER NOT NULL,
            singleton_lookups INTEGER NOT NULL,
            leaf_inserts      INTEGER NOT NULL,
            leaf_updates      INTEGER NOT NULL,
            leaf_deletes      INTEGER NOT NULL,
            lower_bound       INTEGER NOT NULL,
            PRIMARY KEY (interval_id, partition_key)
        ) WITHOUT ROWID;
        CREATE INDEX ix_part_delta_partition ON part_delta (partition_key);

        -- Stretches of time in which a partition's operational activity is not known. Adjacent stretches are merged.
        CREATE TABLE part_unknown (
            partition_key   INTEGER NOT NULL REFERENCES part,
            from_ms         INTEGER NOT NULL,
            to_ms           INTEGER NOT NULL,
            PRIMARY KEY (partition_key, from_ms)
        ) WITHOUT ROWID;
        CREATE INDEX ix_part_unknown_to ON part_unknown (partition_key, to_ms);

        CREATE TABLE part_baseline (
            partition_key     INTEGER PRIMARY KEY REFERENCES part,
            database_key      INTEGER NOT NULL REFERENCES db,
            partition_id      INTEGER NOT NULL,
            has_stats         INTEGER NOT NULL,
            range_scans       INTEGER NOT NULL,
            singleton_lookups INTEGER NOT NULL,
            leaf_inserts      INTEGER NOT NULL,
            leaf_updates      INTEGER NOT NULL,
            leaf_deletes      INTEGER NOT NULL
        );
        CREATE INDEX ix_part_baseline_db ON part_baseline (database_key);
        """,

        // 3: maintenance flag, hourly and daily rollups, restart/reset/gap events that outlive the raw intervals.
        """
        -- 1 when the interval's activity lies entirely inside a configured maintenance window (decision D-044).
        ALTER TABLE usage_interval ADD COLUMN maintenance INTEGER NOT NULL DEFAULT 0;
        -- 0 until the interval has been folded into the hourly and daily rollups; existing intervals are folded on the
        -- next collection.
        ALTER TABLE usage_interval ADD COLUMN rolled_up INTEGER NOT NULL DEFAULT 0;
        CREATE INDEX ix_usage_interval_pending ON usage_interval (interval_id) WHERE rolled_up = 0;
        CREATE INDEX ix_usage_interval_to ON usage_interval (to_ms);

        -- Observed time per database per hour and per UTC day, split exactly at bucket boundaries.
        CREATE TABLE db_hour (
            database_key    INTEGER NOT NULL REFERENCES db,
            hour            INTEGER NOT NULL,
            observed_ms     INTEGER NOT NULL,
            maintenance_ms  INTEGER NOT NULL,
            PRIMARY KEY (database_key, hour)
        ) WITHOUT ROWID;
        CREATE TABLE db_day (
            database_key    INTEGER NOT NULL REFERENCES db,
            day             INTEGER NOT NULL,
            observed_ms     INTEGER NOT NULL,
            maintenance_ms  INTEGER NOT NULL,
            PRIMARY KEY (database_key, day)
        ) WITHOUT ROWID;

        -- Index activity per bucket, attributed to the bucket in which its interval ended. User activity inside a
        -- maintenance window is kept apart (maint_*). Only buckets with activity have a row.
        CREATE TABLE index_hour (
            index_key           INTEGER NOT NULL REFERENCES idx,
            hour                INTEGER NOT NULL,
            user_reads          INTEGER NOT NULL,
            user_updates        INTEGER NOT NULL,
            system_reads        INTEGER NOT NULL,
            system_updates      INTEGER NOT NULL,
            maint_user_reads    INTEGER NOT NULL,
            maint_user_updates  INTEGER NOT NULL,
            lower_bound         INTEGER NOT NULL,
            last_user_read_ms   INTEGER,
            PRIMARY KEY (index_key, hour)
        ) WITHOUT ROWID;
        CREATE TABLE index_day (
            index_key           INTEGER NOT NULL REFERENCES idx,
            day                 INTEGER NOT NULL,
            user_reads          INTEGER NOT NULL,
            user_updates        INTEGER NOT NULL,
            system_reads        INTEGER NOT NULL,
            system_updates      INTEGER NOT NULL,
            maint_user_reads    INTEGER NOT NULL,
            maint_user_updates  INTEGER NOT NULL,
            lower_bound         INTEGER NOT NULL,
            last_user_read_ms   INTEGER,
            PRIMARY KEY (index_key, day)
        ) WITHOUT ROWID;

        CREATE TABLE part_hour (
            partition_key   INTEGER NOT NULL REFERENCES part,
            hour            INTEGER NOT NULL,
            reads           INTEGER NOT NULL,
            writes          INTEGER NOT NULL,
            maint_reads     INTEGER NOT NULL,
            maint_writes    INTEGER NOT NULL,
            lower_bound     INTEGER NOT NULL,
            PRIMARY KEY (partition_key, hour)
        ) WITHOUT ROWID;
        CREATE TABLE part_day (
            partition_key   INTEGER NOT NULL REFERENCES part,
            day             INTEGER NOT NULL,
            reads           INTEGER NOT NULL,
            writes          INTEGER NOT NULL,
            maint_reads     INTEGER NOT NULL,
            maint_writes    INTEGER NOT NULL,
            lower_bound     INTEGER NOT NULL,
            PRIMARY KEY (partition_key, day)
        ) WITHOUT ROWID;

        -- Restarts, resets and gaps, kept as long as the daily rollups so that history can always explain itself.
        CREATE TABLE history_event (
            event_id        INTEGER PRIMARY KEY,
            database_key    INTEGER NOT NULL REFERENCES db,
            kind            TEXT    NOT NULL CHECK (kind IN ('instance_restart', 'reset', 'gap')),
            at_ms           INTEGER NOT NULL,
            from_ms         INTEGER NOT NULL,
            to_ms           INTEGER NOT NULL,
            reason          TEXT
        );
        CREATE INDEX ix_history_event_db ON history_event (database_key, at_ms);

        INSERT INTO history_event (database_key, kind, at_ms, from_ms, to_ms, reason)
        SELECT database_key, kind, COALESCE(reset_at_ms, to_ms), from_ms, to_ms, reason
        FROM usage_interval WHERE kind <> 'continuous';
        """,

        // 4: memory-optimized tables, whose index usage SQL Server does not report (decision D-049).
        """
        ALTER TABLE obj ADD COLUMN is_memory_optimized INTEGER NOT NULL DEFAULT 0;
        """,

        // 5: Query Store attribution: plans, the objects they read and write, and where each database's reading stopped.
        """
        -- A Query Store plan, by plan id and plan hash (a cleared Query Store can reuse plan ids).
        CREATE TABLE qs_plan (
            database_key        INTEGER NOT NULL REFERENCES db,
            plan_id             INTEGER NOT NULL,
            plan_hash           TEXT    NOT NULL,
            query_hash          TEXT    NOT NULL,
            procedure_name      TEXT,
            query_text          TEXT,
            last_execution_ms   INTEGER NOT NULL,
            parsed              INTEGER NOT NULL,
            PRIMARY KEY (database_key, plan_id, plan_hash)
        ) WITHOUT ROWID;
        CREATE INDEX ix_qs_plan_last ON qs_plan (last_execution_ms);

        -- The tables a plan reads or writes, mapped to Dormant's identities when the plan was first read.
        CREATE TABLE qs_plan_object (
            database_key    INTEGER NOT NULL,
            plan_id         INTEGER NOT NULL,
            plan_hash       TEXT    NOT NULL,
            object_key      INTEGER NOT NULL REFERENCES obj,
            index_name      TEXT    NOT NULL,
            access          TEXT    NOT NULL CHECK (access IN ('read', 'write')),
            PRIMARY KEY (database_key, plan_id, plan_hash, object_key, index_name, access),
            FOREIGN KEY (database_key, plan_id, plan_hash) REFERENCES qs_plan (database_key, plan_id, plan_hash)
        ) WITHOUT ROWID;
        CREATE INDEX ix_qs_plan_object_object ON qs_plan_object (object_key, access);

        CREATE TABLE qs_state (
            database_key    INTEGER PRIMARY KEY REFERENCES db,
            state           TEXT    NOT NULL,
            watermark_ms    INTEGER,
            read_ms         INTEGER NOT NULL,
            error           TEXT
        );
        """,

        // 6: readings made up rather than collected, such as the demo's backfilled history (decision D-058).
        """
        ALTER TABLE collection ADD COLUMN synthetic INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    public static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Applies every migration newer than the store's version, each in its own transaction.</summary>
    /// <param name="targetVersion">Stop at this version; tests use it to build stores as older releases wrote them.</param>
    public static async Task MigrateAsync(SqliteConnection connection, CancellationToken ct, int targetVersion = CurrentVersion)
    {
        var version = await ReadVersionAsync(connection, ct);
        if (version > CurrentVersion)
        {
            throw new StoreException($"The store was written by a newer version of Dormant (schema {version}; this build understands up to {CurrentVersion}). Upgrade Dormant.");
        }

        for (var next = version + 1; next <= targetVersion; next++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[next - 1] + $"\nPRAGMA user_version = {next};";
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }
}
