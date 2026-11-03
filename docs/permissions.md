# Permissions

Dormant needs to read usage counters and metadata. It never needs to read rows, and never needs any permission that
changes anything. `dormant validate` checks every permission below and prints the exact statement for anything missing.

Ready-to-run scripts are in [`deploy/sql`](../deploy/sql): set the login name on their `:setvar` line and run them with
sqlcmd or in SSMS's SQLCMD mode.

| Platform | Script |
|---|---|
| SQL Server 2022 and later, Amazon RDS for those versions | `sqlserver-2022-and-later.sql` |
| SQL Server 2016, 2017 and 2019, Amazon RDS for those versions, Azure SQL Managed Instance | `sqlserver-2016-2019.sql` |
| Azure SQL Database (per database) | `azure-sql-database.sql` |

The scripts grant to an existing login; their comments show how to create one (SQL authentication, a domain account,
or Microsoft Entra ID). The Windows Service's virtual account reaches remote servers as the computer account
(`CORP\HOST$`); use that, or a group managed service account, as the login.

## SQL Server 2022 and later (including Amazon RDS)

```sql
CREATE LOGIN [dormant] WITH PASSWORD = N'...';        -- or FROM WINDOWS for a domain account
GRANT VIEW SERVER PERFORMANCE STATE TO [dormant];      -- usage DMVs, instance start time
GRANT VIEW ANY DEFINITION TO [dormant];                -- table, index and partition names
GRANT CONNECT ANY DATABASE TO [dormant];               -- open each database without a user in it
```

## SQL Server 2016, 2017 and 2019 (including Amazon RDS)

```sql
CREATE LOGIN [dormant] WITH PASSWORD = N'...';
GRANT VIEW SERVER STATE TO [dormant];
GRANT VIEW ANY DEFINITION TO [dormant];
GRANT CONNECT ANY DATABASE TO [dormant];
```

Instead of `CONNECT ANY DATABASE` and `VIEW ANY DEFINITION` you can create a user for the login in each monitored
database and grant `VIEW DEFINITION` there.

## Azure SQL Managed Instance

As SQL Server 2016–2019 above: Managed Instance uses `VIEW SERVER STATE`.

## Azure SQL Database

Permissions are per database. In each monitored database:

```sql
CREATE USER [dormant] FROM LOGIN [dormant];             -- or FROM EXTERNAL PROVIDER for Microsoft Entra ID
GRANT VIEW DATABASE STATE TO [dormant];
GRANT VIEW DEFINITION TO [dormant];
```

Basic, S0 and S1 databases, and databases in elastic pools, do not expose the usage DMVs through `VIEW DATABASE STATE`.
For those, in `master`:

```sql
ALTER SERVER ROLE ##MS_ServerStateReader## ADD MEMBER [dormant];
```

## Database visibility

Dormant lists databases from `sys.databases` and reads each database's `database_guid` from
`sys.database_recovery_status`. Both rely on `VIEW ANY DATABASE`, which every login has through `public` by default. If
your servers revoke it from `public`, grant it to the Dormant login.

## Query Store (optional)

Covered by the grants above: Query Store views need `VIEW DATABASE STATE` (`VIEW DATABASE PERFORMANCE STATE` on
SQL Server 2022 and later), which the server-level grant implies.

## What is tested

`tests/Dormant.Integration/ValidateTests` creates a login with no grants on SQL Server 2019, 2022 and 2025, checks
that `validate` names each missing permission, then runs the script for that version from `deploy/sql` and checks that
every check passes, Query Store included. The demo and the CI systemd job collect as a login granted by
`sqlserver-2022-and-later.sql` and nothing else. Azure SQL Database, Managed Instance and RDS are not yet covered by
automated tests (docs/roadmap.md).
