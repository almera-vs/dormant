namespace Dormant.Core.Configuration;

/// <summary>Reads secrets from environment variables or files. Error messages never contain the secret.</summary>
public sealed class SecretResolver
{
    private readonly Func<string, string?> _getEnvironmentVariable;

    public SecretResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    public SecretResolver(Func<string, string?> getEnvironmentVariable)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
    }

    public async Task<string> ResolveAsync(SecretReference reference, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);
        switch (reference)
        {
            case EnvironmentSecret env:
                var value = _getEnvironmentVariable(env.VariableName);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new SecretUnavailableException($"Environment variable {env.VariableName} is not set or is empty.");
                }

                return value;

            case FileSecret file:
                string text;
                try
                {
                    text = await File.ReadAllTextAsync(file.Path, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new SecretUnavailableException($"Secret file {file.Path} could not be read: {ex.Message}", ex);
                }

                text = text.Trim();
                if (text.Length == 0)
                {
                    throw new SecretUnavailableException($"Secret file {file.Path} is empty.");
                }

                return text;

            default:
                throw new ArgumentOutOfRangeException(nameof(reference), reference.GetType().Name, "Unknown secret reference type.");
        }
    }
}

public sealed class SecretUnavailableException : Exception
{
    public SecretUnavailableException(string message)
        : base(message)
    {
    }

    public SecretUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
