using Dormant.Core.Usage;
using Dormant.Core.Configuration;

namespace Dormant.Core.Tests.Usage;

public sealed class ClassifierTests
{
    private const long Today = 20_000;
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);
    private static readonly Classifier Classifier = new(ClassificationOptions.Default);

    // Fully observed windows of 30 and 180 days, ending at noon today.
    private static readonly TimeSpan Warm = TimeSpan.FromDays(29) + Noon;
    private static readonly TimeSpan Dormant = TimeSpan.FromDays(179) + Noon;

    private static Classification Classify(
        int readDays = 0, long? lastRead = null, TimeSpan? warm = null, TimeSpan? dormant = null, long updates = 0, long maintenanceReads = 0) =>
        Classifier.Classify(new UsageEvidence(readDays, lastRead, warm ?? Warm, dormant ?? Dormant, updates, maintenanceReads), Today, Noon);

    [Theory]
    [InlineData(4, UsageClass.Hot)]
    [InlineData(7, UsageClass.Hot)]
    [InlineData(3, UsageClass.Warm)]
    public void Hot_means_read_on_most_of_the_last_seven_days(int readDays, UsageClass expected) =>
        Classify(readDays, lastRead: Today).Class.ShouldBe(expected);

    [Fact]
    public void Warm_means_read_within_thirty_days()
    {
        var result = Classify(readDays: 0, lastRead: Today - 29);

        result.Class.ShouldBe(UsageClass.Warm);
        result.Explanation.ShouldBe("last read " + Classifier.Date(Today - 29) + " (29 days ago)");
    }

    [Fact]
    public void Cold_needs_a_read_within_180_days_and_a_well_observed_30_day_window()
    {
        var cold = Classify(lastRead: Today - 30);
        cold.Class.ShouldBe(UsageClass.Cold);
        cold.DecidingCoverage.ShouldBe(1.0);
        cold.Explanation.ShouldBe($"no reads in the last 30 days (100.0% observed); last read {Classifier.Date(Today - 30)}");

        var unsure = Classify(lastRead: Today - 30, warm: Warm * 0.8);
        unsure.Class.ShouldBe(UsageClass.InsufficientHistory);
        unsure.Explanation.ShouldStartWith("no reads seen in the last 30 days, but only 80.0% of them were observed (Cold needs 90.0%)");
    }

    [Fact]
    public void Dormant_needs_no_reads_in_180_days_and_90_percent_of_them_observed()
    {
        Classify(lastRead: null).Class.ShouldBe(UsageClass.Dormant);
        Classify(lastRead: Today - 180).Class.ShouldBe(UsageClass.Dormant);
        Classify(lastRead: Today - 179).Class.ShouldBe(UsageClass.Cold);
    }

    [Fact]
    public void Scenario_10_thirty_days_of_history_never_makes_anything_dormant()
    {
        var result = Classify(lastRead: null, warm: Warm, dormant: Warm);

        result.Class.ShouldBe(UsageClass.InsufficientHistory);
        result.DormantCoverage.ShouldBeLessThan(0.17);
        result.Explanation.ShouldBe("no reads seen, but only 16.4% of the last 180 days were observed (Dormant needs 90.0%)");
    }

    [Fact]
    public void Coverage_just_below_the_threshold_is_never_rounded_up()
    {
        var result = Classify(dormant: Dormant * 0.8999);

        result.Class.ShouldBe(UsageClass.InsufficientHistory);
        result.Explanation.ShouldContain("89.9%");
    }

    [Fact]
    public void Write_only_is_flagged_when_updates_have_no_reads_in_a_well_observed_window()
    {
        Classify(lastRead: null, updates: 12).WriteOnly.ShouldBeTrue();
        Classify(lastRead: Today - 3, updates: 12).WriteOnly.ShouldBeFalse();
        Classify(lastRead: null, updates: 12, warm: Warm * 0.5).WriteOnly.ShouldBeFalse();
        Classify(lastRead: null, updates: 0).WriteOnly.ShouldBeFalse();
    }

    [Fact]
    public void Reads_only_inside_maintenance_windows_are_explained()
    {
        var result = Classify(lastRead: null, maintenanceReads: 40);

        result.Class.ShouldBe(UsageClass.Dormant);
        result.Explanation.ShouldEndWith("; reads inside maintenance windows are not counted as use");
    }

    [Fact]
    public void Coverage_is_the_observed_share_of_a_window_ending_now()
    {
        Classifier.Coverage(TimeSpan.FromDays(6) + Noon, 7, Noon).ShouldBe(1.0);
        Classifier.Coverage(TimeSpan.FromDays(3) + (Noon / 2), 7, Noon).ShouldBe(0.5);
        Classifier.Coverage(TimeSpan.FromDays(9), 7, Noon).ShouldBe(1.0);
    }
}

public sealed class CostModelTests
{
    private const long OneGb = 1024L * 1024 * 1024;

    [Fact]
    public void No_price_means_no_money_figure()
    {
        var model = new CostModel(CostOptions.Default);

        model.HasPrices.ShouldBeFalse();
        model.MonthlyCost(100 * OneGb).ShouldBeNull();
    }

    [Fact]
    public void Cost_is_storage_plus_every_retained_backup_copy()
    {
        var model = new CostModel(new CostOptions("EUR", DataStoragePerGbMonth: 0.10m, BackupCopies: 3, BackupStoragePerGbMonth: 0.02m));

        model.MonthlyCost(100 * OneGb).ShouldBe(16.00m);
        CostModel.Gigabytes(OneGb / 2).ShouldBe(0.5);
    }
}
