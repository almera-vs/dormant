namespace Dormant.Core.Configuration;

/// <summary>The commented example configuration written by <c>dormant init</c>.</summary>
public static class ExampleConfig
{
    private const string ResourceName = "Dormant.Core.Configuration.example.dormant.yaml";

    public static string Text { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(ExampleConfig).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
