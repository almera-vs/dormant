using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Dormant.Cli.Hosting;

/// <summary>
/// Installs and removes the Dormant Windows Service with sc.exe (decision D-059). The service runs
/// <c>dormant run -c &lt;config&gt;</c> under the virtual account <c>NT SERVICE\Dormant</c> unless another account is
/// given, starts automatically (delayed), restarts after failures, and logs to the Application event log.
/// </summary>
internal static class WindowsService
{
    public const string Name = "Dormant";
    public const string VirtualAccount = @"NT SERVICE\" + Name;
    public const string EventSource = Name;

    /// <summary>The command line the service runs. Paths are quoted, so spaces in them are safe.</summary>
    public static string BinPath(string executable, string config) => $"\"{executable}\" run -c \"{config}\"";

    /// <summary>The sc.exe arguments that create the service.</summary>
    public static IReadOnlyList<string> CreateArguments(string executable, string config, string account) =>
    [
        "create", Name,
        "binPath=", BinPath(executable, config),
        "start=", "delayed-auto",
        "obj=", account,
        "DisplayName=", Name,
    ];

    /// <summary>Restart after 1 minute, again after 1 minute, then every 5 minutes; the count resets after a day.</summary>
    public static IReadOnlyList<string> FailureArguments() =>
        ["failure", Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000"];

    public static IReadOnlyList<string> DescriptionArguments() =>
        ["description", Name, "Dormant: read-only, restart-proof usage history for SQL Server. Reads usage counters and metadata only."];

    [SupportedOSPlatform("windows")]
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>True when sc.exe knows the service.</summary>
    public static async Task<bool> ExistsAsync(CancellationToken ct) => (await ScAsync(["query", Name], ct)).ExitCode == 0;

    /// <summary>Runs sc.exe; returns its exit code and everything it printed.</summary>
    public static async Task<(int ExitCode, string Output)> ScAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe could not be started.");
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (output + error).Trim());
    }

    /// <summary>Runs sc.exe and throws with its own message when it fails.</summary>
    public static async Task ScOrThrowAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var (exitCode, output) = await ScAsync(arguments, ct);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments[0]} failed ({exitCode}): {output}");
        }
    }

    /// <summary>
    /// Makes a directory usable by the service. A directory Dormant creates gets a protected ACL: Administrators and
    /// SYSTEM full control, the service account the given rights, nobody else. An existing directory keeps its ACL and
    /// only gains an entry for the service account.
    /// </summary>
    /// <param name="createdByInstaller">The installer created this directory (perhaps moments ago, to write the example configuration into it).</param>
    [SupportedOSPlatform("windows")]
    public static void SecureDirectory(string path, IdentityReference account, FileSystemRights rights, bool createdByInstaller)
    {
        var directory = Directory.CreateDirectory(path);
        var security = directory.GetAccessControl();
        const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        if (createdByInstaller)
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(rule);
            }

            foreach (var sid in (WellKnownSidType[])[WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid])
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(sid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
            }
        }

        security.AddAccessRule(new FileSystemAccessRule(account, rights, Inherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);

        // Files written before the ACL was protected (the example configuration) inherit the new one.
        if (createdByInstaller)
        {
            foreach (var file in directory.EnumerateFiles())
            {
                var fileSecurity = file.GetAccessControl();
                fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                foreach (FileSystemAccessRule rule in fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
                {
                    fileSecurity.RemoveAccessRuleSpecific(rule);
                }

                file.SetAccessControl(fileSecurity);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RegisterEventSource()
    {
        if (!EventLog.SourceExists(EventSource))
        {
            EventLog.CreateEventSource(EventSource, "Application");
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RemoveEventSource()
    {
        if (EventLog.SourceExists(EventSource))
        {
            EventLog.DeleteEventSource(EventSource);
        }
    }
}
