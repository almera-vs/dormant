using Dormant.Core.Collection;

namespace Dormant.Core.Tests.Collection;

public sealed class PartitionRangeTests
{
    private static BoundaryValue Date(string value) => new("date", value);

    private static PartitionRange Right(BoundaryValue? lower, BoundaryValue? upper, string column = "OrderDate") =>
        new(true, true, lower, upper, column);

    [Fact]
    public void An_unpartitioned_index_has_one_range_for_all_rows()
    {
        PartitionRange.Whole.Key.ShouldBe("all");
        PartitionRange.Whole.Label.ShouldBe("all");
        PartitionRange.Whole.Description.ShouldBe("all rows");
    }

    [Theory]
    [InlineData("2021-03-01", "2021-04-01", "2021-03")]
    [InlineData("2021-12-01", "2022-01-01", "2021-12")]
    [InlineData("2021-04-01", "2021-07-01", "2021-Q2")]
    [InlineData("2021-01-01", "2022-01-01", "2021")]
    [InlineData("2021-03-15", "2021-03-16", "2021-03-15")]
    [InlineData("2021-01-01", "2021-03-01", "2021-01-01 ≤ OrderDate < 2021-03-01")]
    [InlineData("2021-02-15", "2021-03-15", "2021-02-15 ≤ OrderDate < 2021-03-15")]
    public void Calendar_ranges_of_a_range_right_date_function_get_short_labels(string from, string to, string label) =>
        Right(Date(from), Date(to)).Label.ShouldBe(label);

    [Fact]
    public void Open_ends_read_as_before_and_from()
    {
        Right(null, Date("2019-01-01")).Label.ShouldBe("before 2019-01");
        Right(Date("2026-11-01"), null).Label.ShouldBe("from 2026-11");
        Right(null, Date("2019-01-01")).Description.ShouldBe("OrderDate < 2019-01-01");
        Right(Date("2026-11-01"), null).Description.ShouldBe("OrderDate ≥ 2026-11-01");
    }

    [Fact]
    public void Datetime_boundaries_at_midnight_read_as_dates()
    {
        var range = Right(new BoundaryValue("datetime2", "2021-03-01T00:00:00"), new BoundaryValue("datetime2", "2021-04-01T00:00:00"));

        range.Label.ShouldBe("2021-03");
        range.Description.ShouldBe("2021-03-01 ≤ OrderDate < 2021-04-01");
    }

    [Fact]
    public void Range_left_functions_put_the_upper_boundary_inside_the_partition()
    {
        var range = new PartitionRange(true, false, Date("2021-02-28"), Date("2021-03-31"), "OrderDate");

        range.Description.ShouldBe("2021-02-28 < OrderDate ≤ 2021-03-31");
        range.Label.ShouldBe(range.Description);
        new PartitionRange(true, false, Date("2021-03-31"), null, "OrderDate").Description.ShouldBe("OrderDate > 2021-03-31");
    }

    [Fact]
    public void Numeric_ranges_are_described_exactly()
    {
        var range = Right(new BoundaryValue("int", "100"), new BoundaryValue("int", "200"), "CustomerId");

        range.Label.ShouldBe("100 ≤ CustomerId < 200");
    }

    [Fact]
    public void The_key_depends_on_boundaries_types_and_side_only()
    {
        var march = Right(Date("2021-03-01"), Date("2021-04-01"));

        march.Key.ShouldBe("R|date:2021-03-01|date:2021-04-01");
        (march with { Column = "ShippedOn" }).Key.ShouldBe(march.Key);
        (march with { RangeRight = false }).Key.ShouldNotBe(march.Key);
        Right(new BoundaryValue("datetime2", "2021-03-01"), Date("2021-04-01")).Key.ShouldNotBe(march.Key);
        Right(null, Date("2021-01-01")).Key.ShouldBe("R|-|date:2021-01-01");
        Right(Date("2021-01-01"), null).Key.ShouldBe("R|date:2021-01-01|+");
    }
}
