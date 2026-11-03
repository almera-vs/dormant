using System.Security.Cryptography;
using Dormant.Core.Configuration;
using Dormant.Demo;

// dormant-demo: prepares and drives the docker-compose demo (deploy/demo). Demo only: it uses a sysadmin connection to
// build its own database. Dormant itself never needs one.
//
//   dormant-demo setup -c /etc/dormant/demo.yaml     create the demo database and login, backfill synthetic history
//   dormant-demo workload [seconds]                  run the demo application's queries every few seconds, forever
//
// Environment: DORMANT_DEMO_ADMIN (sysadmin connection string), DORMANT_DEMO_PASSWORD (password for the 'dormant' login).

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

var admin = Environment.GetEnvironmentVariable("DORMANT_DEMO_ADMIN");
var password = Environment.GetEnvironmentVariable("DORMANT_DEMO_PASSWORD");
if (string.IsNullOrEmpty(admin) || string.IsNullOrEmpty(password))
{
    await Console.Error.WriteLineAsync("Set DORMANT_DEMO_ADMIN (a sysadmin connection string) and DORMANT_DEMO_PASSWORD.");
    return 3;
}

try
{
    switch (args)
    {
        case ["setup", "-c", var configPath]:
            var config = await ConfigLoader.LoadAsync(configPath, cancel.Token);
            await DemoSetup.RunAsync(new ComposeServer(admin, password), config.Store.Path, password, config.Collection.MaxObservedSpan, Console.Out, cancel.Token);
            return 0;

        case ["workload", ..]:
            var every = TimeSpan.FromSeconds(args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 15);
            await Sql.WaitUntilOnlineAsync(admin, TimeSpan.FromMinutes(5), cancel.Token);
            var random = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
            await Console.Out.WriteLineAsync($"Running the demo workload every {every.TotalSeconds:0} seconds: recent orders, customers, new orders.");
            while (!cancel.IsCancellationRequested)
            {
                await DemoWorkload.RunOnceAsync(admin, random, cancel.Token);
                await Task.Delay(every, cancel.Token);
            }

            return 0;

        default:
            await Console.Error.WriteLineAsync("Usage: dormant-demo setup -c <config> | dormant-demo workload [seconds]");
            return 3;
    }
}
catch (OperationCanceledException)
{
    return 0;
}
catch (ConfigException ex)
{
    await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, ex.Errors.Select(e => e.Format(ex.SourcePath))));
    return 3;
}
