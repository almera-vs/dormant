using System.CommandLine;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Dormant.Cli.Hosting;
using Dormant.Core.Configuration;

namespace Dormant.Cli.Commands;

/// <summary><c>dormant service install|uninstall</c>: the Windows Service. On Linux, deploy/systemd has the unit.</summary>
internal static class ServiceCommand
{
    public static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dormant", "dormant.yaml");

    public static Command Create(TextWriter stdout, TextWriter stderr)
    {
        var config = new Option<FileInfo>("--config", "-c")
        {
            Description = "The configuration the service runs with. Written from the example if it does not exist. Default: %ProgramData%\\Dormant\\dormant.yaml.",
            DefaultValueFactory = _ => new FileInfo(DefaultConfigPath),
        };
        var account = new Option<string>("--account")
        {
            Description = "The account the service runs as: the virtual account NT SERVICE\\Dormant (default), or a group managed service account such as CORP\\dormant$. Passwords are never accepted.",
            DefaultValueFactory = _ => WindowsService.VirtualAccount,
        };
        var install = new Command("install", "Install Dormant as a Windows Service that runs 'dormant run'. Run as administrator, from the installed dormant.exe.")
        {
            config,
            account,
        };
        var uninstall = new Command("uninstall", "Stop and remove the Windows Service. The configuration and history are kept.");
        var command = new Command("service", "Install or remove the Windows Service (Windows only; on Linux use the systemd unit in deploy/systemd).")
        {
            install,
            uninstall,
        };

        install.SetAction(async (parseResult, ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                await stderr.WriteLineAsync("dormant service is for Windows. On Linux, install the systemd unit from deploy/systemd (see README).");
                return ExitCodes.ConfigOrUsageError;
            }

            return await InstallAsync(parseResult.GetValue(config)!, parseResult.GetValue(account)!, stdout, stderr, ct);
        });
        uninstall.SetAction(async (_, ct) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                await stderr.WriteLineAsync("dormant service is for Windows. On Linux, remove the systemd unit (see deploy/systemd).");
                return ExitCodes.ConfigOrUsageError;
            }

            return await UninstallAsync(stdout, stderr, ct);
        });
        return command;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> InstallAsync(FileInfo configFile, string account, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!WindowsService.IsElevated())
        {
            await stderr.WriteLineAsync("Installing a service needs an elevated prompt: run this again as administrator.");
            return ExitCodes.ConfigOrUsageError;
        }

        var executable = Environment.ProcessPath;
        if (executable is null || !string.Equals(Path.GetFileName(executable), "dormant.exe", StringComparison.OrdinalIgnoreCase))
        {
            await stderr.WriteLineAsync($"Run this from the installed dormant.exe; the service would otherwise start {executable ?? "an unknown program"}.");
            return ExitCodes.ConfigOrUsageError;
        }

        if (await WindowsService.ExistsAsync(ct))
        {
            await stderr.WriteLineAsync($"A service named {WindowsService.Name} already exists. Remove it first with: dormant service uninstall");
            return ExitCodes.ConfigOrUsageError;
        }

        var configPath = configFile.FullName;
        var configDirectory = Path.GetDirectoryName(configPath)!;

        // Remember which directories exist before anything is written: those the installer creates get a protected ACL.
        var existedBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(configDirectory))
        {
            existedBefore.Add(Path.GetFullPath(configDirectory));
        }

        var wroteExample = false;
        if (!File.Exists(configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            await File.WriteAllTextAsync(configPath, ExampleConfig.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
            wroteExample = true;
        }

        var loaded = await ConfigFile.LoadAsync(configFile, stderr, ct);
        if (loaded is null)
        {
            return ExitCodes.ConfigOrUsageError;
        }

        foreach (var path in (string[])[Path.GetDirectoryName(loaded.Store.Path)!, loaded.Report.OutDir])
        {
            if (Directory.Exists(path) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(configDirectory), StringComparison.OrdinalIgnoreCase))
            {
                existedBefore.Add(Path.GetFullPath(path));
            }
        }

        await WindowsService.ScOrThrowAsync(WindowsService.CreateArguments(executable, configPath, account), ct);
        try
        {
            await WindowsService.ScOrThrowAsync(WindowsService.DescriptionArguments(), ct);
            await WindowsService.ScOrThrowAsync(WindowsService.FailureArguments(), ct);

            // The service account exists once the service does. It reads the configuration and writes the history and reports.
            var identity = new NTAccount(account);
            var directories = new (string Path, FileSystemRights Rights)[]
            {
                (Path.GetDirectoryName(configPath)!, FileSystemRights.ReadAndExecute),
                (Path.GetDirectoryName(loaded.Store.Path)!, FileSystemRights.Modify),
                (loaded.Report.OutDir, FileSystemRights.Modify),
            };
            foreach (var (path, rights) in directories.DistinctBy(d => d.Path.ToUpperInvariant() + d.Rights))
            {
                var created = !existedBefore.Contains(Path.GetFullPath(path));
                WindowsService.SecureDirectory(path, identity, rights, created);
                await stdout.WriteLineAsync(created
                    ? $"Created {path}: Administrators and SYSTEM full control, {account} {Describe(rights)}, nobody else."
                    : $"{path}: kept its permissions and gave {account} {Describe(rights)}.");
            }

            WindowsService.RegisterEventSource();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or IdentityNotMappedException or SystemException)
        {
            await WindowsService.ScAsync(["delete", WindowsService.Name], ct);
            await stderr.WriteLineAsync($"The service could not be set up, so it was removed again: {ex.Message}");
            return ExitCodes.ConfigOrUsageError;
        }

        await stdout.WriteLineAsync($"Installed the {WindowsService.Name} service: {WindowsService.BinPath(executable, configPath)}, as {account}, starting automatically (delayed).");
        if (wroteExample)
        {
            await stdout.WriteLineAsync($"Wrote an example configuration to {configPath}. Edit it before starting the service.");
        }

        await stdout.WriteLineAsync("Connection strings: prefer 'file:' references to files in a directory only the service account and administrators can read.");
        await stdout.WriteLineAsync($"Check the configuration with: dormant validate -c \"{configPath}\"");
        await stdout.WriteLineAsync($"Then start the service with: sc.exe start {WindowsService.Name}");
        return ExitCodes.Ok;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> UninstallAsync(TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        if (!WindowsService.IsElevated())
        {
            await stderr.WriteLineAsync("Removing a service needs an elevated prompt: run this again as administrator.");
            return ExitCodes.ConfigOrUsageError;
        }

        if (!await WindowsService.ExistsAsync(ct))
        {
            await stdout.WriteLineAsync($"No service named {WindowsService.Name} is installed.");
            return ExitCodes.Ok;
        }

        // Stopping a stopped service fails harmlessly; wait until it has stopped before deleting it.
        await WindowsService.ScAsync(["stop", WindowsService.Name], ct);
        for (var i = 0; i < 30 && !(await WindowsService.ScAsync(["query", WindowsService.Name], ct)).Output.Contains("STOPPED", StringComparison.Ordinal); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        try
        {
            await WindowsService.ScOrThrowAsync(["delete", WindowsService.Name], ct);
            WindowsService.RemoveEventSource();
        }
        catch (InvalidOperationException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return ExitCodes.PartialFailure;
        }

        await stdout.WriteLineAsync($"Removed the {WindowsService.Name} service. The configuration, history and reports were kept.");
        return ExitCodes.Ok;
    }

    private static string Describe(FileSystemRights rights) => rights == FileSystemRights.Modify ? "read and write" : "read";
}
