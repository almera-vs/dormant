namespace Dormant.Integration;

/// <summary>One SQL Server 2022 container shared by a test class; each test works in its own databases.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private SqlServerInstance? _instance;
    private string? _unavailable;

    internal SqlServerInstance Sql
    {
        get
        {
            if (_unavailable is not null)
            {
                Assert.Skip(_unavailable);
            }

            return _instance!;
        }
    }

    public async ValueTask InitializeAsync() =>
        (_instance, _unavailable) = await SqlServerInstance.TryStartAsync(Image, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (_instance is not null)
        {
            await _instance.DisposeAsync();
        }
    }
}
