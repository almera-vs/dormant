using Dormant.Cli.Commands;
using Dormant.Cli.Running;
using Dormant.Core.Attribution;
using Dormant.Core.Collection;
using Dormant.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Dormant.Cli.Tests;

public sealed class CollectionScheduleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static MaintenanceWindow Window(string text)
    {
        MaintenanceWindow.TryParse(text, out var window, out var error).ShouldBeTrue(error);
        return window!;
    }

    [Theory]
    [InlineData(7, 15)]
    [InlineData(0, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 30)]
    [InlineData(59, 60)]
    public void Collections_line_up_with_multiples_of_the_interval(int nowMinute, int nextMinute) =>
        CollectionSchedule.Next(Noon.AddMinutes(nowMinute), TimeSpan.FromMinutes(15), [])
            .ShouldBe(Noon.AddMinutes(nextMinute));

    [Fact]
    public void A_window_opening_or_closing_before_the_next_tick_comes_first()
    {
        IReadOnlyList<MaintenanceWindow> windows = [Window("Daily 12:10-14:00 UTC")];

        CollectionSchedule.Next(Noon.AddMinutes(2), TimeSpan.FromMinutes(15), windows).ShouldBe(Noon.AddMinutes(10));
        CollectionSchedule.Next(Noon.AddMinutes(10), TimeSpan.FromMinutes(15), windows).ShouldBe(Noon.AddMinutes(15));
        CollectionSchedule.Next(Noon.AddMinutes(118), TimeSpan.FromMinutes(15), windows).ShouldBe(Noon.AddMinutes(120));
    }
}

public sealed class RunTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("dormant-run-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private DormantConfig Config(string storePath)
    {
        var secret = Path.Combine(_directory, "erp.secret");
        File.WriteAllText(secret, "Server=fake");
        var yaml = $"version: 1\nstore: {{ path: '{storePath}' }}\ninstances:\n  - name: erp-prod\n    connection: {{ file: '{secret}' }}\n";
        return ConfigLoader.Parse(yaml, Path.Combine(_directory, "dormant.yaml"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The worker did not reach the expected state.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Run_collects_immediately_then_on_each_aligned_tick_and_stops_cleanly()
    {
        var time = new CountingTimeProvider(Noon.AddMinutes(3));
        var collectors = new FakeCollectorFactory(time);
        var storePath = Path.Combine(_directory, "dormant.db");
        using var host = RunCommand.BuildHost(Config(storePath), LogLevel.None, collectors, time);

        // Advance the clock only once the worker waits: one timer for pruning, one per collection so far.
        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => collectors.Calls == 1 && time.Timers == 2);

        // Nothing happens before the next quarter hour.
        time.Advance(TimeSpan.FromMinutes(11));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        collectors.Calls.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => collectors.Calls == 2 && time.Timers == 3);
        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(() => collectors.Calls == 3 && time.Timers == 4);
        await host.StopAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<RunOutcome>().ExitCode.ShouldBe(0);
        collectors.Times.ShouldBe([Noon.AddMinutes(3), Noon.AddMinutes(15), Noon.AddMinutes(30)]);
        await using var check = new SqliteConnection($"Data Source={storePath};Mode=ReadOnly;Pooling=False");
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = check.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_interval WHERE kind = 'continuous'";
        ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(2);
    }

    [Fact]
    public async Task A_store_that_cannot_be_opened_stops_run_with_exit_code_4()
    {
        var time = new FakeTimeProvider(Noon);
        using var host = RunCommand.BuildHost(Config(_directory), LogLevel.None, new FakeCollectorFactory(time), time);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<RunOutcome>().ExitCode.ShouldBe(4);
    }

    /// <summary>A fake clock that counts the timers created on it, so a test can tell when the worker is waiting.</summary>
    private sealed class CountingTimeProvider(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    /// <summary>A collector that reports one database whose single index gains a read each time.</summary>
    private sealed class FakeCollectorFactory : ICollectorFactory
    {
        private readonly TimeProvider _time;
        private int _calls;

        public FakeCollectorFactory(TimeProvider time)
        {
            _time = time;
        }

        public int Calls => Volatile.Read(ref _calls);

        public List<DateTimeOffset> Times { get; } = [];

        public ICollector Create(string connectionString, InstanceOptions instance) => new FakeCollector(this);

        public IQueryStoreHarvester CreateHarvester(string connectionString) => throw new NotSupportedException("Query Store is not enabled in these tests.");

        private sealed class FakeCollector(FakeCollectorFactory factory) : ICollector
        {
            public Task<InstanceInfo> DescribeAsync(CancellationToken ct) => throw new NotSupportedException();

            public Task<Snapshot> CollectAsync(CancellationToken ct)
            {
                var now = factory._time.GetUtcNow();
                lock (factory.Times)
                {
                    factory.Times.Add(now);
                }

                var calls = Interlocked.Increment(ref factory._calls);
                var engine = new EngineIncarnation(1_000, 1_000 + (long)(now - Noon.AddDays(-1)).TotalMilliseconds, Noon.AddDays(-1));
                var index = new IndexObservation(
                    new ObjectRef(100, new DateTime(2024, 1, 1), "dbo", "Orders", "U"), 1, "PK_Orders", "CLUSTERED",
                    IsPrimaryKey: true, IsUnique: true, IsUniqueConstraint: false, IsDisabled: false,
                    new UsageCounters(calls, 0, 0, 0, 0, 0, 0, 0, null, null),
                    [new PartitionObservation(1, 100_001, PartitionRange.Whole, 10, 8, 8, "NONE", null)]);
                var database = new DatabaseSnapshot("guid:1", "Sales", 5, DatabaseOutcome.Collected, SkipReason.None, null, now, engine, [index]);
                return Task.FromResult(new Snapshot(new InstanceInfo("fake", "SQL Server 2022", "16.0", "Developer", "dormant", engine.StartTimeUtc), now, TimeSpan.FromMilliseconds(5), [database]));
            }
        }
    }
}
