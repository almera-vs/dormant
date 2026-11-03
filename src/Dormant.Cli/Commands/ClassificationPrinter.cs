using System.Globalization;
using Dormant.Core;
using Dormant.Core.Usage;
using Dormant.Core.Configuration;

namespace Dormant.Cli.Commands;

/// <summary>The classification summary that <c>status</c> shows per instance: size first, money second, and only as an estimate.</summary>
internal sealed class ClassificationPrinter
{
    private static readonly (UsageClass Class, string Name)[] Classes =
    [
        (UsageClass.Hot, "Hot"),
        (UsageClass.Warm, "Warm"),
        (UsageClass.Cold, "Cold"),
        (UsageClass.Dormant, "Dormant"),
        (UsageClass.InsufficientHistory, "Insufficient history"),
    ];

    private readonly TextWriter _out;
    private readonly ClassificationOptions _options;
    private readonly CostModel _cost;

    public ClassificationPrinter(TextWriter output, ClassificationOptions options, CostModel cost)
    {
        _out = output;
        _options = options;
        _cost = cost;
    }

    public async Task PrintAsync(EstateClassification estate, string instance)
    {
        var units = estate.Units.Where(u => u.Table.Instance == instance).ToList();
        if (units.Count == 0)
        {
            return;
        }

        await _out.WriteLineAsync();
        await _out.WriteLineAsync(Invariant(
            $"  classification as of {Text.Time(estate.AsOfUtc)}: Dormant means no reads in {_options.DormantDays} days with at least {Classifier.Percent(_options.MinCoverage)} of them observed"));

        foreach (var (usageClass, name) in Classes)
        {
            var inClass = units.Where(u => u.Classification.Class == usageClass).ToList();
            var bytes = inClass.Sum(u => u.ReservedBytes);
            var tables = inClass.Count(u => u.Partition is null);
            var partitions = inClass.Count - tables;
            var what = inClass.Count == 0
                ? string.Empty
                : string.Join(", ", new[]
                {
                    tables > 0 ? Text.Plural(tables, "table") : null,
                    partitions > 0 ? Text.Plural(partitions, "partition") : null,
                }.OfType<string>());
            await _out.WriteLineAsync($"    {name,-21} {Text.Bytes(bytes),10}  {Money(bytes),-22} {what}".TrimEnd());
        }

        var writeOnly = units.Where(u => u.Classification.WriteOnly).ToList();
        if (writeOnly.Count > 0)
        {
            await _out.WriteLineAsync(Invariant(
                $"    write-only            {Text.Bytes(writeOnly.Sum(u => u.ReservedBytes)),10}  written to, but not read in {_options.WarmDays} days: {Text.Plural(writeOnly.Count, "table or partition", "tables or partitions")}"));
        }

        var unused = estate.UnusedIndexes.Where(i => i.Instance == instance).ToList();
        await _out.WriteLineAsync(unused.Count == 0
            ? Invariant($"    unused indexes        none found (needs {_options.WarmDays} days with {Classifier.Percent(_options.MinCoverage)} observed)")
            : Invariant($"    unused indexes        {Text.Bytes(unused.Sum(i => i.ReservedBytes)),10}  {Text.Plural(unused.Count, "index", "indexes")}, {Text.Count(unused.Sum(i => i.UpdatesInWindow))} updates to maintain in {_options.WarmDays} days"));

        var tablesWithReaders = estate.Tables.Count(t => t.Instance == instance && t.LastReader is not null);
        if (tablesWithReaders > 0)
        {
            var total = estate.Tables.Count(t => t.Instance == instance);
            await _out.WriteLineAsync(Invariant($"    known readers         Query Store names a reader for {Text.Count(tablesWithReaders)} of {Text.Plural(total, "table")}"));
        }

        if (!_cost.HasPrices)
        {
            await _out.WriteLineAsync("    estimated cost        set your price: cost.data_storage_per_gb_month and cost.backup_storage_per_gb_month");
        }
    }

    private string Money(long bytes) =>
        _cost.MonthlyCost(bytes) is { } money
            ? string.Create(CultureInfo.InvariantCulture, $"≈ {money:N2} {_cost.Currency}/month")
            : string.Empty;

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
