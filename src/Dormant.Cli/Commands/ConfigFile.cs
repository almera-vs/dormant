using Dormant.Core.Configuration;

namespace Dormant.Cli.Commands;

internal static class ConfigFile
{
    /// <summary>Loads the configuration, printing every problem to <paramref name="stderr"/> when it is not valid.</summary>
    public static async Task<DormantConfig?> LoadAsync(FileInfo file, TextWriter stderr, CancellationToken ct)
    {
        try
        {
            return await ConfigLoader.LoadAsync(file.FullName, ct);
        }
        catch (ConfigException ex)
        {
            await stderr.WriteLineAsync(ex.Errors.Count == 1
                ? "The configuration is not valid:"
                : $"The configuration is not valid ({ex.Errors.Count} problems):");
            foreach (var error in ex.Errors)
            {
                await stderr.WriteLineAsync("  " + error.Format(ex.SourcePath));
            }

            return null;
        }
    }

    /// <summary>All configured instances, or only the one named by --instance.</summary>
    public static bool TrySelectInstances(DormantConfig config, string? name, TextWriter stderr, out IReadOnlyList<InstanceOptions> instances)
    {
        if (name is null)
        {
            instances = config.Instances;
            return true;
        }

        var match = config.Instances.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            stderr.WriteLine($"No instance named '{name}' in {config.SourcePath}. Configured: {string.Join(", ", config.Instances.Select(i => i.Name))}.");
            instances = [];
            return false;
        }

        instances = [match];
        return true;
    }
}
