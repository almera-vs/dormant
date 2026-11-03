using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dormant.Core.Configuration;

/// <summary>Loads and validates a configuration file. Every problem is reported at once, then loading fails.</summary>
public static class ConfigLoader
{
    public static async Task<DormantConfig> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(fullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException(fullPath, [new ConfigError("(file)", $"cannot be read: {ex.Message}")]);
        }

        return Parse(text, fullPath);
    }

    /// <param name="yaml">The configuration text.</param>
    /// <param name="sourcePath">Absolute path of the file; relative paths in the configuration resolve against its directory.</param>
    public static DormantConfig Parse(string yaml, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            var message = ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}";
            throw new ConfigException(sourcePath, [new ConfigError("(yaml)", message, (int)ex.Start.Line, (int)ex.Start.Column)]);
        }
        catch (InvalidOperationException)
        {
            // YamlDotNet's scanner reports some malformed input (e.g. an unclosed { or [) this way, without a position.
            throw new ConfigException(sourcePath, [new ConfigError("(yaml)", "is not valid YAML; check for an unclosed bracket, brace or quote")]);
        }

        if (stream.Documents.Count == 0)
        {
            throw new ConfigException(sourcePath, [new ConfigError("(file)", "is empty; run 'dormant init' to write an example configuration")]);
        }

        if (stream.Documents.Count > 1)
        {
            throw new ConfigException(sourcePath, [new ConfigError("(file)", "contains more than one YAML document; keep a single document")]);
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory();
        var binder = new ConfigBinder(baseDirectory);
        var config = binder.Bind(stream.Documents[0].RootNode, sourcePath);
        if (binder.Errors.Count > 0 || config is null)
        {
            throw new ConfigException(sourcePath, binder.Errors);
        }

        return config;
    }
}
