namespace Dormant.Core.Configuration;

/// <summary>One problem in a configuration file, located by field path and, where known, line and column.</summary>
public sealed record ConfigError(string Path, string Message, int? Line = null, int? Column = null)
{
    public string Format(string file) =>
        Line is null
            ? $"{file}: {Path}: {Message}"
            : $"{file}:{Line}:{Column}: {Path}: {Message}";
}

public sealed class ConfigException : Exception
{
    public ConfigException(string sourcePath, IReadOnlyList<ConfigError> errors)
        : this(sourcePath, InFileOrder(errors))
    {
    }

    private ConfigException(string sourcePath, List<ConfigError> errors)
        : base(BuildMessage(sourcePath, errors))
    {
        SourcePath = sourcePath;
        Errors = errors;
    }

    public string SourcePath { get; }

    public IReadOnlyList<ConfigError> Errors { get; }

    // Stable sort: errors without a position keep their order and come last.
    private static List<ConfigError> InFileOrder(IReadOnlyList<ConfigError> errors) =>
        [.. errors.OrderBy(e => e.Line ?? int.MaxValue).ThenBy(e => e.Column ?? 0)];

    private static string BuildMessage(string sourcePath, List<ConfigError> errors) =>
        errors.Count == 1
            ? $"Configuration is not valid: {errors[0].Format(sourcePath)}"
            : $"Configuration is not valid ({errors.Count} problems):{Environment.NewLine}"
              + string.Join(Environment.NewLine, errors.Select(e => "  " + e.Format(sourcePath)));
}
