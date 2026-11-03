namespace Dormant.Store;

/// <summary>Dormant's local store could not be opened, read or written. Maps to exit code 4.</summary>
public sealed class StoreException : Exception
{
    public StoreException()
    {
    }

    public StoreException(string message)
        : base(message)
    {
    }

    public StoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
