# Dormant

*Pay for the data you use.*

Production SQL Server databases only grow. Old partitions, abandoned tables and indexes nobody reads sit on expensive
storage, inflate backups, slow restores and lengthen nightly maintenance. They are hard to act on safely, because SQL
Server's usage statistics reset on every restart: a table that looks unused may have been read yesterday, before
Tuesday's patch reboot.

Dormant is a small, read-only agent that snapshots SQL Server's usage and size statistics on a schedule, stitches them
into a continuous history that survives restarts, and classifies every table, index and partition as Hot, Warm, Cold
or Dormant, always stating how much of the period it actually observed. It never changes, moves or deletes anything,
and nothing leaves your environment.

## How it differs

Point-in-time tools such as sp_BlitzIndex are excellent but inherit the reset problem. Performance monitors (Redgate
Monitor, SolarWinds DPA, Quest Spotlight) focus on performance rather than long-term object usage. Some tools now keep
index usage across restarts. Dormant's emphasis is different:

- **History that survives restarts**, failovers and statistics resets, with gaps shown as gaps.
- **Partition granularity**, labelled by boundary range (`Orders · 2021-03`), so history follows SPLIT and MERGE.
- **Evidence before action**: every classification carries its coverage, and nothing is called Dormant without enough
  observed history. Unknown is never treated as zero.
- **Read-only and local**: metadata and counters only, no row data, no telemetry.

## Status

All eight v1 milestones are complete:

- configuration and validation, with least-privilege permission scripts;
- continuous collection into a restart-proof history down to each partition;
- classification as Hot, Warm, Cold, Dormant or Insufficient history, with unused indexes and cost estimates;
- the last known reader of each table, where Query Store is on;
- the HTML heatmap report and CSV/JSON exports;
- a Windows Service, a systemd unit and a container image;
- a docker-compose demo.

What comes after v1 is in [docs/roadmap.md](docs/roadmap.md).

## Quickstart: a first report in 15 minutes

Tested from scratch on Windows against SQL Server Express. The steps are the same on Linux.

1. **Get Dormant.** Download the package for your platform from the latest release on the
   [Releases page](../../releases/latest): `dormant-<version>-win-x64.zip`, `-linux-x64.tar.gz` or
   `-linux-arm64.tar.gz`, and check it against `SHA256SUMS`. Each holds one executable with no prerequisites, plus the
   permission scripts and the service files for its platform. You can also build it yourself (see [Build](#build)).

2. **Create a read-only login.** Run the script for your platform from [`deploy/sql`](deploy/sql) as an
   administrator, after creating the login as its comments show. Dormant needs no write access and never reads rows.
   See [docs/permissions.md](docs/permissions.md).

3. **Write a configuration** and edit the instance's name, connection reference and databases:

   ```bash
   dormant init
   ```

4. **Provide the connection string** through the environment variable the configuration names. Connection strings
   are never written in the configuration itself:

   ```bash
   export DORMANT_ERP_PROD='Server=sql01.corp.local;Integrated Security=true;Encrypt=true'
   ```

   On Windows PowerShell: `$env:DORMANT_ERP_PROD = '...'`.

5. **Check connectivity and permissions.** Nothing is collected, and each failed check prints the statement that fixes
   it:

   ```bash
   dormant validate -c dormant.yaml
   ```

6. **Take two snapshots.** The first one starts the history; the second closes its first interval:

   ```bash
   dormant collect -c dormant.yaml --once
   ```

7. **Write the report:**

   ```bash
   dormant report -c dormant.yaml
   ```

   It goes to `report.out_dir` (default `reports/`). It is one HTML file that opens in any browser, makes no network
   requests and prints on A4 landscape. On the first day everything shows as Insufficient history and the heatmap is
   hatched: Dormant says what it does not know yet. Dormant needs 30 well-observed days before it calls anything Cold
   or an index unused, and 180 days before it calls anything Dormant.

8. **Keep collecting:** install the service (below) or run `dormant run -c dormant.yaml`. Time Dormant is not running
   is recorded as a gap, never as inactivity.

`dormant status -c dormant.yaml` shows how much of the history was observed, with every restart, reset and gap.
`dormant export -c dormant.yaml --format csv --level partition --out dormant.csv` gives the same classification to a
spreadsheet or a script.

## Running it as a service

**Windows.** In an elevated PowerShell:

```powershell
.\deploy\windows\install.ps1 -Executable .\dormant.exe
```

This copies `dormant.exe` to `C:\Program Files\Dormant` and runs `dormant service install`, which:

- creates the service `Dormant`, which runs as the virtual account `NT SERVICE\Dormant` (or `-Account CORP\dormant$`
  for a group managed service account), starts automatically and restarts after failures;
- writes `%ProgramData%\Dormant\dormant.yaml` and locks that directory to Administrators, SYSTEM and the service;
- logs to the Application event log.

Edit the configuration, prefer `file:` references for connection strings, then start the service with
`sc.exe start Dormant`. Remove it with `deploy\windows\uninstall.ps1`; the history is kept.

**Linux (systemd).**

```bash
sudo deploy/systemd/install.sh ./dormant
```

This installs `/usr/local/bin/dormant`, a `dormant` system user, `/etc/dormant/dormant.yaml`, a root-only
`/etc/dormant/dormant.env` for connection strings, and a hardened unit with its history in `/var/lib/dormant`. Edit the
two files, then start it:

```bash
sudo systemctl start dormant
```

Follow it with `journalctl -u dormant -f`. Remove it with `deploy/systemd/uninstall.sh`; the history is kept.

**Container.** Each release publishes an image for `linux/amd64` and `linux/arm64` to the GitHub Container Registry
(`ghcr.io/<owner>/dormant`, tagged with the version; the release notes give the exact name):

```bash
docker run -d --name dormant -v dormant-state:/var/lib/dormant -v ./dormant.yaml:/etc/dormant/dormant.yaml:ro -e DORMANT_ERP_PROD='Server=...' ghcr.io/<owner>/dormant:<version>
```

Or build it yourself: `docker build -f deploy/docker/Dockerfile -t dormant .`. The image has no shell and runs as a
non-root user. Set `store.path` to `/var/lib/dormant/dormant.db` and `report.out_dir` to `/var/lib/dormant/reports`
in the mounted configuration.

The service set-ups are described in [docs/threat-model.md](docs/threat-model.md) and decisions D-059 and D-060.

## Try the demo

The docker-compose demo starts SQL Server 2022 with a small shop database and an application using it, and Dormant
collecting from it:

```bash
docker compose -f deploy/demo/docker-compose.yml up -d --build
```

Its first 200 days of history are **synthetic**, backfilled in about a minute and marked as such in every report. After
that, everything is collected for real. Then write and copy out the report:

```bash
docker compose -f deploy/demo/docker-compose.yml exec dormant dormant report -c /etc/dormant/demo.yaml --out /var/lib/dormant/reports/demo.html
docker compose -f deploy/demo/docker-compose.yml cp dormant:/var/lib/dormant/reports/demo.html .
```

The demo plants old order partitions nobody reads, an archive, an audit log that is only written, and an index that
nothing reads. The report finds exactly those, and nothing else (decision D-062). `docker compose ... down -v` removes
it all.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build
```

```bash
dotnet test
```

The SQL Server integration tests use Docker (Testcontainers with `mcr.microsoft.com/mssql/server`). Without Docker they
are skipped locally; CI runs them on Linux, along with the fault scenarios ([docs/fault-scenarios.md](docs/fault-scenarios.md)),
the performance test, the services and the demo.

Self-contained single-file builds:

```bash
dotnet publish src/Dormant.Cli -c Release -r linux-x64 -o out/linux-x64
```

Runtime identifiers: `win-x64`, `linux-x64`, `linux-arm64`.

To release, push a version tag. The release workflow runs every test, then publishes the three packages with their
checksums as a GitHub release and pushes the container image. A tag with a suffix (`v0.2.0-rc.1`) makes a
pre-release, and never moves the image's `latest` tag.

```bash
git tag v0.1.0 && git push origin v0.1.0
```

## Commands

| Command | What it does |
|---|---|
| `dormant init [-o dormant.yaml] [--force]` | Writes a commented example configuration. Never overwrites without `--force`. |
| `dormant validate -c dormant.yaml` | Checks the configuration, then connectivity and permissions for every instance and database. |
| `dormant collect -c dormant.yaml [--once] [--instance NAME]` | Takes one snapshot of usage counters and adds it to the history. |
| `dormant status -c dormant.yaml [--instance NAME]` | Last collection, observed coverage, restarts, resets and gaps, and the size (and estimated cost) of Hot, Warm, Cold, Dormant and Insufficient-history data, with unused indexes. Reads only the local store. |
| `dormant report -c dormant.yaml [--since 180d] [--instance NAME] [--out heatmap.html]` | Writes the self-contained HTML report: summary, heatmap, Cold & Dormant by size, unused indexes, partitioned tables, method & caveats. Reads only the local store. |
| `dormant export -c dormant.yaml --format csv\|json [--level table\|index\|partition] [--instance NAME] [--out FILE]` | Writes the classification as CSV or JSON, one row per table, index or partition, to standard output or a file. Reads only the local store. |
| `dormant run -c dormant.yaml` | Collects continuously on schedule and prunes history past its retention. What the services run. |
| `dormant service install\|uninstall [-c CONFIG] [--account ACCOUNT]` | Installs or removes the Windows Service (Windows only, elevated). |
| `dormant version` | Shows the version and runtime. |

Every command accepts `--log-level` (default `Warning`). Logs are JSON on standard error; standard output carries only
results.

Exit codes: `0` ok, `1` partial failure (some instances unreachable or missing permissions), `3` configuration or usage
error, `4` store error.

## Configuration

See the commented example written by `dormant init`
([source](src/Dormant.Core/Configuration/example.dormant.yaml)). Relative paths resolve against the configuration
file's directory. Validation reports every problem at once, with its line, column and field.

## Documentation

- [Architecture](docs/architecture.md)
- [Fault scenarios](docs/fault-scenarios.md)
- [Decisions](docs/decisions.md)
- [SQL Server internals](docs/sqlserver-internals.md)
- [Permissions](docs/permissions.md)
- [Threat model](docs/threat-model.md)
- [Roadmap](docs/roadmap.md)

## Licence

Dormant is released under the [MIT licence](LICENSE).
