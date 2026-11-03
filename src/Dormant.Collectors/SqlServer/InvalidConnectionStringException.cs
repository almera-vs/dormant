namespace Dormant.Collectors.SqlServer;

/// <summary>A connection string that could not be parsed. Carries no part of the string itself.</summary>
public sealed class InvalidConnectionStringException : Exception
{
    public InvalidConnectionStringException()
        : this("unknown")
    {
    }

    public InvalidConnectionStringException(string parserError)
        : base($"The connection string could not be parsed ({parserError}). Check its keywords, separators and quoting.")
    {
    }

    public InvalidConnectionStringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
