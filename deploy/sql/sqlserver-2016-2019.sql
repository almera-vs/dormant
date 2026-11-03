-- Dormant: least-privilege permissions on SQL Server 2016, 2017 and 2019, Amazon RDS for those versions, and
-- Azure SQL Managed Instance.
--
-- Dormant reads usage counters and metadata only. It never reads rows and needs nothing that changes anything.
-- These versions have no VIEW SERVER PERFORMANCE STATE, so the usage DMVs need VIEW SERVER STATE.
-- Proven by tests/Dormant.Integration/ValidateTests on SQL Server 2019.
--
-- Run in master with sqlcmd, or in SSMS with Query > SQLCMD Mode. Set the login name here:
:setvar DormantLogin "dormant"

-- 1. The login, if it does not exist yet. Pick one:
--    CREATE LOGIN [$(DormantLogin)] WITH PASSWORD = N'<a long random password>';    -- SQL authentication
--    CREATE LOGIN [$(DormantLogin)] FROM WINDOWS;                                  -- a domain account: [CORP\svc-dormant]
--    CREATE LOGIN [$(DormantLogin)] FROM EXTERNAL PROVIDER;                        -- Microsoft Entra ID (Managed Instance)

-- 2. The grants.
USE master;
GO
GRANT VIEW SERVER STATE TO [$(DormantLogin)];               -- usage and partition DMVs, instance start time, Query Store
GRANT VIEW ANY DEFINITION TO [$(DormantLogin)];             -- table, index and partition names and boundaries
GRANT CONNECT ANY DATABASE TO [$(DormantLogin)];            -- open each database without a user in it
GO
