namespace Dormant.Cli;

/// <summary>Process exit codes. These are part of Dormant's public contract; scripts depend on them.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>Some instances were unreachable or are missing permissions; the rest succeeded.</summary>
    public const int PartialFailure = 1;

    /// <summary>The configuration or the command line is not valid.</summary>
    public const int ConfigOrUsageError = 3;

    /// <summary>Dormant's local store could not be opened, read or written.</summary>
    public const int StoreError = 4;
}
