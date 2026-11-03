-- Dormant: least-privilege permissions on SQL Server 2022 and later, and Amazon RDS for SQL Server 2022 and later.
--
-- Dormant reads usage counters and metadata only. It never reads rows and needs nothing that changes anything.
-- Proven by tests/Dormant.Integration/ValidateTests on SQL Server 2022 and 2025: with these grants and nothing else,
-- 'dormant validate' passes every check, including Query Store.
--
-- Run in master with sqlcmd, or in SSMS with Query > SQLCMD Mode. Set the login name here:
:setvar DormantLogin "dormant"

-- 1. The login, if it does not exist yet. Pick one:
--    CREATE LOGIN [$(DormantLogin)] WITH PASSWORD = N'<a long random password>';    -- SQL authentication
--    CREATE LOGIN [$(DormantLogin)] FROM WINDOWS;                                  -- a domain account: [CORP\svc-dormant]
--    CREATE LOGIN [$(DormantLogin)] FROM EXTERNAL PROVIDER;                        -- Microsoft Entra ID (where supported)
--    The Windows Service's virtual account authenticates to remote servers as the computer account: [CORP\HOST$].

-- 2. The grants.
USE master;
GO
GRANT VIEW SERVER PERFORMANCE STATE TO [$(DormantLogin)];   -- usage and partition DMVs, instance start time, Query Store
GRANT VIEW ANY DEFINITION TO [$(DormantLogin)];             -- table, index and partition names and boundaries
GRANT CONNECT ANY DATABASE TO [$(DormantLogin)];            -- open each database without a user in it
GO
