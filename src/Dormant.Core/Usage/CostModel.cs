using Dormant.Core.Configuration;

namespace Dormant.Core.Usage;

/// <summary>
/// Monthly cost of keeping data, from the customer's own prices only. Sizes are converted with 1 GB = 1024³ bytes, the
/// way SQL Server reports them. While no price is set, there is no money figure at all (decision D-050).
/// </summary>
public sealed class CostModel
{
    public const double BytesPerGb = 1024d * 1024 * 1024;

    private readonly CostOptions _options;

    public CostModel(CostOptions options)
    {
        _options = options;
    }

    public string Currency => _options.Currency;

    public bool HasPrices => _options.HasPrices;

    public static double Gigabytes(long bytes) => bytes / BytesPerGb;

    /// <summary>Estimated cost per month: primary storage plus every retained full-backup copy. Null while no price is set.</summary>
    public decimal? MonthlyCost(long bytes)
    {
        if (!HasPrices)
        {
            return null;
        }

        var gb = (decimal)Gigabytes(bytes);
        return (gb * _options.DataStoragePerGbMonth) + (gb * _options.BackupCopies * _options.BackupStoragePerGbMonth);
    }
}
