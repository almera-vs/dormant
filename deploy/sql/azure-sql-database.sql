-- Dormant: least-privilege permissions on Azure SQL Database.
--
-- Permissions are per database: run part 2 in EACH monitored database (Azure SQL Database has no USE).
-- Not yet covered by an automated test against a real Azure database (docs/roadmap.md).
--
-- Run with sqlcmd, or in SSMS with Query > SQLCMD Mode. Set the login or Entra principal name here:
:setvar DormantLogin "dormant"

-- 1. In master, a SQL login if you use SQL authentication (skip for Microsoft Entra ID):
--    CREATE LOGIN [$(DormantLogin)] WITH PASSWORD = N'<a long random password>';

-- 2. In each monitored database:
CREATE USER [$(DormantLogin)] FROM LOGIN [$(DormantLogin)];        -- or: FROM EXTERNAL PROVIDER, for Microsoft Entra ID
GRANT VIEW DATABASE STATE TO [$(DormantLogin)];                    -- usage and partition DMVs, Query Store
GRANT VIEW DEFINITION TO [$(DormantLogin)];                        -- table, index and partition names and boundaries
GO

-- 3. Only for Basic, S0 and S1 databases and databases in elastic pools, which do not expose the usage DMVs through
--    VIEW DATABASE STATE. In master:
--    ALTER SERVER ROLE ##MS_ServerStateReader## ADD MEMBER [$(DormantLogin)];
