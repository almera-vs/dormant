using System.Globalization;

namespace Dormant.Demo;

/// <summary>
/// The demo database: monthly partitioned orders over two years, customers, products, an archive nobody reads, an audit
/// log nobody reads, one index nobody reads, and the procedures the workload calls (so Query Store can name readers).
/// </summary>
public static class DemoSchema
{
    public static string Create(DateOnly baseMonth)
    {
        var boundaries = string.Join(", ", Enumerable.Range(DemoPlan.OldestMonth, 1 - DemoPlan.OldestMonth + 1)
            .Select(m => $"'{baseMonth.AddMonths(m).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'"));
        var oldest = baseMonth.AddMonths(DemoPlan.OldestMonth).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"""
            CREATE DATABASE [{DemoPlan.Database}];
            GO
            -- Query Store names the procedures that read each table.
            ALTER DATABASE [{DemoPlan.Database}] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL);
            GO
            USE [{DemoPlan.Database}];
            CREATE PARTITION FUNCTION pf_month (date) AS RANGE RIGHT FOR VALUES ({boundaries});
            CREATE PARTITION SCHEME ps_month AS PARTITION pf_month ALL TO ([PRIMARY]);

            CREATE TABLE dbo.Customers (
                CustomerId int NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
                Email nvarchar(200) NOT NULL,
                Name nvarchar(200) NOT NULL);
            CREATE UNIQUE INDEX UX_Customers_Email ON dbo.Customers (Email);

            CREATE TABLE dbo.Products (
                ProductId int NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
                Name nvarchar(200) NOT NULL,
                Price decimal(12, 2) NOT NULL);

            CREATE TABLE dbo.Orders (
                OrderId int IDENTITY NOT NULL,
                OrderDate date NOT NULL,
                CustomerId int NOT NULL,
                Status tinyint NOT NULL,
                Total decimal(12, 2) NOT NULL,
                Notes char(200) NOT NULL,
                CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (OrderDate, OrderId) ON ps_month (OrderDate));
            CREATE INDEX IX_Orders_Customer ON dbo.Orders (CustomerId) INCLUDE (Total) ON ps_month (OrderDate);
            CREATE INDEX {DemoPlan.UnusedIndex} ON dbo.Orders (Status) ON ps_month (OrderDate);

            CREATE TABLE dbo.OrderArchive (
                OrderId int NOT NULL CONSTRAINT PK_OrderArchive PRIMARY KEY,
                OrderDate date NOT NULL,
                CustomerId int NOT NULL,
                Total decimal(12, 2) NOT NULL,
                Notes char(400) NOT NULL);

            CREATE TABLE dbo.AuditLog (
                At datetime2(0) NOT NULL,
                Action varchar(40) NOT NULL,
                Detail char(200) NOT NULL);
            GO
            INSERT dbo.Customers (CustomerId, Email, Name)
            SELECT value, CONCAT(N'customer', value, N'@example.com'), CONCAT(N'Customer ', value) FROM GENERATE_SERIES(1, 5000);
            INSERT dbo.Products (ProductId, Name, Price)
            SELECT value, CONCAT(N'Product ', value), 5 + value % 200 FROM GENERATE_SERIES(1, 500);
            -- 3,000 orders a month for 24 months.
            INSERT dbo.Orders (OrderDate, CustomerId, Status, Total, Notes)
            SELECT DATEADD(day, s.value % 28, DATEADD(month, s.value / 3000, CAST('{oldest}' AS date))),
                   1 + s.value % 5000, s.value % 4, 10 + s.value % 300, 'demo order'
            FROM GENERATE_SERIES(0, 3000 * 24 - 1) AS s;
            INSERT dbo.OrderArchive (OrderId, OrderDate, CustomerId, Total, Notes)
            SELECT value, DATEADD(day, -(value % 2000), CAST('{oldest}' AS date)), 1 + value % 5000, 10 + value % 300, 'archived order'
            FROM GENERATE_SERIES(1, 20000);
            INSERT dbo.AuditLog (At, Action, Detail)
            SELECT DATEADD(minute, -value, SYSUTCDATETIME()), 'order placed', 'demo' FROM GENERATE_SERIES(1, 10000);
            GO
            -- Statistics are built once, here, and never automatically again: SQL Server's automatic statistics updates
            -- scan whole indexes, and partition-level counters cannot tell such a scan from a user's (docs/roadmap.md).
            EXEC sp_updatestats;
            ALTER DATABASE [{DemoPlan.Database}] SET AUTO_UPDATE_STATISTICS OFF;
            ALTER DATABASE [{DemoPlan.Database}] SET AUTO_CREATE_STATISTICS OFF;
            GO
            CREATE PROCEDURE dbo.RecentOrders @since date AS
                SELECT COUNT(*), SUM(Total) FROM dbo.Orders WHERE OrderDate >= @since;
            GO
            CREATE PROCEDURE dbo.CustomerOrders @customer int, @since date AS
                SELECT COUNT(*), SUM(Total) FROM dbo.Orders WITH (INDEX(IX_Orders_Customer))
                WHERE CustomerId = @customer AND OrderDate >= @since;
            GO
            CREATE PROCEDURE dbo.CustomerProfile @customer int AS
                SELECT Name, Email FROM dbo.Customers WHERE CustomerId = @customer;
            GO
            CREATE PROCEDURE dbo.PlaceOrder @customer int AS
                INSERT dbo.Orders (OrderDate, CustomerId, Status, Total, Notes)
                VALUES (CAST(SYSUTCDATETIME() AS date), @customer, 0, 42, 'demo order');
                INSERT dbo.AuditLog (At, Action, Detail) VALUES (SYSUTCDATETIME(), 'order placed', 'demo');
            GO
            """;
    }
}
