using System.Xml;
using Dormant.Core.Attribution;

namespace Dormant.Attribution.Tests;

public sealed class ShowplanParserTests
{
    private static string Plan(string relOps) => $"""
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564" Build="16.0.4135.4">
          <BatchSequence><Batch><Statements>
            <StmtSimple StatementText="..." StatementType="SELECT">
              <QueryPlan>
                {relOps}
              </QueryPlan>
            </StmtSimple>
          </Statements></Batch></BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Seeks_scans_and_lookups_are_reads_of_the_index_they_use()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
              <OutputList><ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Orders]" Column="Total" /></OutputList>
              <NestedLoops Optimized="0">
                <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek">
                  <IndexScan Ordered="1" ScanDirection="FORWARD">
                    <DefinedValues />
                    <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Customer]" IndexKind="NonClustered" Storage="RowStore" />
                  </IndexScan>
                </RelOp>
                <RelOp NodeId="2" PhysicalOp="Key Lookup" LogicalOp="Key Lookup">
                  <IndexScan Lookup="1" Ordered="1">
                    <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" TableReferenceId="-1" IndexKind="Clustered" Storage="RowStore" />
                  </IndexScan>
                </RelOp>
              </NestedLoops>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldBe(
        [
            new PlanObjectReference("Sales", "dbo", "Orders", "IX_Orders_Customer", PlanAccess.Read),
            new PlanObjectReference("Sales", "dbo", "Orders", "PK_Orders", PlanAccess.Read),
        ],
        ignoreOrder: true);
    }

    [Fact]
    public void A_heap_scan_has_no_index()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan Ordered="0"><Object Database="[Sales]" Schema="[dbo]" Table="[Staging]" IndexKind="Heap" Storage="RowStore" /></TableScan>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldHaveSingleItem().ShouldBe(new PlanObjectReference("Sales", "dbo", "Staging", null, PlanAccess.Read));
    }

    [Fact]
    public void Insert_update_and_delete_targets_are_writes_and_the_rows_they_find_are_reads()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Clustered Index Delete" LogicalOp="Delete">
              <Update DMLRequestSort="0">
                <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" IndexKind="Clustered" Storage="RowStore" />
                <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Customer]" IndexKind="NonClustered" Storage="RowStore" />
                <RelOp NodeId="1" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek">
                  <IndexScan Ordered="1"><Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" IndexKind="Clustered" Storage="RowStore" /></IndexScan>
                </RelOp>
              </Update>
            </RelOp>
            """);

        var references = ShowplanParser.Parse(xml);

        references.Count(r => r.Access == PlanAccess.Write).ShouldBe(2);
        references.ShouldContain(new PlanObjectReference("Sales", "dbo", "Orders", "PK_Orders", PlanAccess.Read));
    }

    [Fact]
    public void An_index_build_is_not_a_reader_of_its_table()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Index Insert" LogicalOp="Insert">
              <CreateIndex>
                <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Customer]" IndexKind="NonClustered" />
                <RelOp NodeId="1" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan">
                  <IndexScan Ordered="0"><Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" IndexKind="Clustered" /></IndexScan>
                </RelOp>
              </CreateIndex>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldHaveSingleItem().ShouldBe(
            new PlanObjectReference("Sales", "dbo", "Orders", "IX_Orders_Customer", PlanAccess.Write));
    }

    [Theory]
    [InlineData("CREATE INDEX")]
    [InlineData("ALTER INDEX")]
    [InlineData("UPDATE STATISTICS")]
    public void Maintenance_statements_are_never_readers(string statementType)
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan">
              <IndexScan><Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" /></IndexScan>
            </RelOp>
            """).Replace("StatementType=\"SELECT\"", $"StatementType=\"{statementType}\"", StringComparison.Ordinal);

        ShowplanParser.Parse(xml).ShouldBeEmpty();
    }

    [Fact]
    public void The_engines_statistics_queries_are_not_readers()
    {
        // Captured by Query Store on SQL Server 2022 in CI, statement type SELECT.
        const string Text = "SELECT StatMan([SC0], [SC1]) FROM (SELECT TOP 100 PERCENT [CustomerId] AS [SC0], [Id] AS [SC1] FROM [dbo].[Orders] WITH (READUNCOMMITTED)  ORDER BY [SC0], [SC1] ) AS _MS_UPDSTATS_TBL  OPTION (MAXDOP 16)";
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Index Scan" LogicalOp="Index Scan">
              <IndexScan><Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Customer]" /></IndexScan>
            </RelOp>
            """).Replace("StatementText=\"...\"", $"StatementText=\"{System.Security.SecurityElement.Escape(Text)}\"", StringComparison.Ordinal);

        ShowplanParser.Parse(xml).ShouldBeEmpty();
    }

    [Fact]
    public void Temporary_tables_table_variables_and_tempdb_are_ignored()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Database="[tempdb]" Schema="[dbo]" Table="[#work_____0000000001A2]" /></TableScan>
            </RelOp>
            <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Table="[@rows]" /></TableScan>
            </RelOp>
            <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan">
              <IndexScan><Object Database="[tempdb]" Schema="[dbo]" Table="[Shared]" Index="[PK]" /></IndexScan>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldBeEmpty();
    }

    [Fact]
    public void Column_references_and_other_operators_are_not_objects_read()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Compute Scalar" LogicalOp="Compute Scalar">
              <OutputList><ColumnReference Database="[Sales]" Schema="[dbo]" Table="[Invoices]" Column="Id" /></OutputList>
              <ComputeScalar><DefinedValues /></ComputeScalar>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldBeEmpty();
    }

    [Fact]
    public void Bracketed_names_are_unquoted_exactly()
    {
        var xml = Plan("""
            <RelOp NodeId="0" PhysicalOp="Index Scan" LogicalOp="Index Scan">
              <IndexScan><Object Database="[Odd]]Db]" Schema="[my schema]" Table="[Order Lines]" Index="[IX a]]b]" /></IndexScan>
            </RelOp>
            """);

        ShowplanParser.Parse(xml).ShouldHaveSingleItem().ShouldBe(new PlanObjectReference("Odd]Db", "my schema", "Order Lines", "IX a]b", PlanAccess.Read));
    }

    [Fact]
    public void A_missing_database_attribute_is_left_for_the_caller_to_fill_in()
    {
        var xml = Plan("""<RelOp NodeId="0"><IndexScan><Object Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" /></IndexScan></RelOp>""");

        ShowplanParser.Parse(xml).ShouldHaveSingleItem().Database.ShouldBeNull();
    }

    [Fact]
    public void Malformed_xml_is_reported()
    {
        Should.Throw<XmlException>(() => ShowplanParser.Parse("<ShowPlanXML><unclosed>"));
    }

    [Fact]
    public void Doctype_declarations_are_refused()
    {
        const string Hostile = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" />""";

        Should.Throw<XmlException>(() => ShowplanParser.Parse(Hostile));
    }

    [Theory]
    [InlineData("[dbo]", "dbo")]
    [InlineData("dbo", "dbo")]
    [InlineData("[a]]b]", "a]b")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Unquote_handles_plain_and_bracketed_names(string? input, string? expected) =>
        ShowplanParser.Unquote(input).ShouldBe(expected);
}
