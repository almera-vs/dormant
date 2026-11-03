using Microsoft.Data.SqlClient;

namespace Dormant.Demo;

/// <summary>
/// The demo's application: reads the last three months of orders and some customers, and places orders, which also
/// writes the audit log and every index of Orders, including the one nothing reads. It never touches older months,
/// products, the archive or the audit log's rows, so what Dormant finds is exactly what <see cref="DemoPlan"/> planted.
/// </summary>
public static class DemoWorkload
{
    public static async Task RunOnceAsync(string adminConnectionString, Random random, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(random);
        var since = DemoPlan.BaseMonth(DateTimeOffset.UtcNow).AddMonths(-2).ToDateTime(TimeOnly.MinValue);
        var builder = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = DemoPlan.Database, ApplicationName = "demo-shop" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);

        await ExecAsync(connection, "dbo.RecentOrders", ct, ("@since", since));
        for (var i = 0; i < 3; i++)
        {
            var customer = random.Next(1, 5001);
            await ExecAsync(connection, "dbo.CustomerProfile", ct, ("@customer", customer));
            await ExecAsync(connection, "dbo.CustomerOrders", ct, ("@customer", customer), ("@since", since));
            await ExecAsync(connection, "dbo.PlaceOrder", ct, ("@customer", customer));
        }
    }

    private static async Task ExecAsync(SqlConnection connection, string procedure, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(procedure, connection) { CommandType = System.Data.CommandType.StoredProcedure };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
        }
    }
}
