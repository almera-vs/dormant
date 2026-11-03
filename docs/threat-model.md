# Threat model

Dormant is a read-only agent: it connects to SQL Server instances, reads usage counters and metadata, and writes a
local history and reports. This page lists what it holds, who could misuse it, and what stops them.

## What Dormant holds

| Asset | Where | Sensitivity |
|---|---|---|
| Connection strings | Environment variables or files named in the configuration; never in the configuration itself, never logged, never stored. | High: they can carry passwords. |
| The configuration | `dormant.yaml` | Low: instance names, filters, thresholds, prices. No secrets by design. |
| The local store | One SQLite file (`store.path`) with `-wal` and `-shm` companions. | Medium: database, schema, table, index and partition names and boundaries, row counts, sizes, usage counts and times, procedure names and query hashes from Query Store; query text only with `query_store.store_text: true`. No row data. |
| Reports and exports | `report.out_dir`, `--out`, or standard output for exports | As the store: the same names, sizes, dates and last known readers (procedure names or query hashes, never query text). Reports make no network requests (D-055). |
| Logs | Standard error (JSON), the journal under systemd, the Application event log under the Windows Service. | Low: server and database names, counts, timings, SQL error messages. Never connection strings. |

## The SQL Server login

Dormant needs only the grants in `deploy/sql` (D-061): server or database state, definitions, and connection to each
database. It never needs, and never should be given, sysadmin, db_owner, `CONTROL` or any write permission. A stolen
Dormant credential therefore reads metadata and counters, and the plans in Query Store, and nothing else: no rows.
Query Store plans can contain literal values from queries; that is the exposure `VIEW SERVER (PERFORMANCE) STATE` and
`VIEW DATABASE STATE` carry on every SQL Server, not one Dormant adds.

## Files on disk

| Platform | Configuration | History and reports | Secrets |
|---|---|---|---|
| Linux, systemd | `/etc/dormant`, `root:dormant` 0750, file 0640 | `/var/lib/dormant`, `dormant` 0700, files 0600 (`StateDirectory`, `UMask=0077`) | `/etc/dormant/dormant.env`, `root` 0600, read by systemd before the service starts |
| Windows Service | `%ProgramData%\Dormant` | same directory, unless the configuration says otherwise | `file:` references to files in a directory with the same ACL |
| Container | mounted read-only at `/etc/dormant` | the volume `/var/lib/dormant`, user 1654, 0700 | environment variables or mounted secret files |
| CLI | where the operator puts it | Dormant creates a new store directory 0700 and file 0600 on Linux; new report directories 0700 and files 0600 | the operator's environment |

On Windows, a directory the service installer creates gets a protected ACL: Administrators and SYSTEM full control,
`NT SERVICE\Dormant` read (configuration) or modify (store, reports), and nobody else. An existing directory keeps its
ACL and only gains the service account's entry, so check it (D-059).

## How it runs

- **Windows Service**: the virtual account `NT SERVICE\Dormant`, which has no password, no interactive logon and no
  rights beyond those granted to it, or a group managed service account. Never LocalSystem, never a password on the
  command line.
- **systemd**: the system user `dormant`, no shell, no home, no capabilities, `NoNewPrivileges`, the file system
  read-only except its state directory, private `/tmp` and devices, only IP and Unix sockets, and the `@system-service`
  system call set (D-060).
- **Container**: Ubuntu chiseled runtime base, no shell or package manager, non-root user 1654. Run it with a
  read-only configuration mount; nothing in the image listens on a port.

## What Dormant never does

- Never writes to a monitored instance, and never reads row data: only catalog views, DMVs and server properties.
- Never takes data locks on user tables. Only brief schema-stability locks are taken while metadata is read, and they
  conflict only with schema changes. `PerformanceTests` holds an exclusive table lock throughout a collection of 50
  databases and checks this (D-063).
- Never takes the only connection to a SINGLE_USER database, and never opens AUTO_CLOSE databases (decisions D-017,
  D-030).
- Never sends anything anywhere except to the configured SQL Server instances: no telemetry, no update checks. The
  report forbids itself any request (Content-Security-Policy `default-src 'none'`).
- Never listens on a network port.

## Threats and mitigations

| Threat | Mitigation |
|---|---|
| Credentials leak through configuration files in version control | Connection strings are only accepted by reference (`env:`, `file:`); an inline string is a configuration error. |
| Credentials leak through logs or error messages | Connection strings are never logged; malformed ones are reported without the driver's message, which can quote them (D-008). |
| Credentials leak through the process list or service definition | Never passed on a command line: the service runs `dormant run -c <config>` only; secrets come from the environment file or secret files. |
| Another local user reads the store, reports or secrets | File and directory modes and ACLs above. |
| A compromised Dormant host is used against SQL Server | The login can only read metadata and counters (deploy/sql). Revoke it and nothing else is affected. |
| Dormant disturbs a production instance | Read-only queries, `READ UNCOMMITTED`, 2-second lock timeout, low deadlock priority, one connection per instance (D-016, D-033); no data locks (D-063); a 30-second budget at 20,000 indexes. |
| A malicious object name attacks whoever opens a report or export | Every value is HTML-encoded in the report; CSV fields starting with `=`, `+`, `-`, `@` are prefixed so spreadsheets never evaluate them (D-056); query plans are parsed with DTDs prohibited. |
| A corrupted or foreign store is misread | Schema version checked on open; newer schemas refused (D-035). |
| Made-up history is mistaken for real | Synthetic readings exist only in the demo, are recorded as such, and every status, report and JSON export says so (D-058). |
| The demo's passwords are reused | They are written in the compose file as demo-only and protect a throwaway container; the compose file says never to reuse them. |
| A false "Dormant" leads someone to delete data that is in use | Coverage is required before any negative conclusion (D-048); the report states each source's limits; DROP statements are shown for review only, with a warning, and Dormant never runs them. |
