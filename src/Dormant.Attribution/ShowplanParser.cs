using System.Xml;
using Dormant.Core.Attribution;

namespace Dormant.Attribution;

/// <summary>
/// Finds the tables an execution plan (showplan XML) reads and writes. An <c>Object</c> element directly inside an
/// <c>IndexScan</c> or <c>TableScan</c> is read (seeks, scans and lookups all use these elements); one inside an
/// <c>Update</c>, <c>SimpleUpdate</c>, <c>ScalarInsert</c> or <c>CreateIndex</c> is written. Everything else
/// (column references, spools, functions) is ignored, as are temporary tables, table variables and tempdb.
/// Index builds, statistics statements and the engine's internal statistics queries (<c>SELECT StatMan(...) FROM ...
/// _MS_UPDSTATS_TBL</c>, which Query Store captures as ordinary SELECTs, as the Query Store integration test showed)
/// scan their table but are maintenance, not readers: their scans are not reported.
/// Streams the XML, so large plans cost little memory.
/// </summary>
public static class ShowplanParser
{
    public const string Namespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static readonly string[] MaintenanceStatements = ["CREATE INDEX", "ALTER INDEX", "CREATE STATISTICS", "UPDATE STATISTICS", "DBCC"];

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    /// <exception cref="XmlException">The plan is not well-formed XML.</exception>
    public static IReadOnlyList<PlanObjectReference> Parse(string planXml)
    {
        ArgumentNullException.ThrowIfNull(planXml);
        var found = new HashSet<PlanObjectReference>();
        var parents = new Stack<string>();
        var indexBuilds = 0;
        var maintenanceStatement = false;

        using var text = new StringReader(planXml);
        using var reader = XmlReader.Create(text, Settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (parents.Pop() == "CreateIndex")
                {
                    indexBuilds--;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "StmtSimple" && IsMaintenance(reader.GetAttribute("StatementType"), reader.GetAttribute("StatementText")))
            {
                maintenanceStatement = true;
            }

            if (reader.LocalName == "Object" && reader.NamespaceURI == Namespace && parents.TryPeek(out var parent)
                && AccessFor(parent) is { } access
                && !(access == PlanAccess.Read && indexBuilds > 0)
                && Reference(reader, access) is { } reference)
            {
                found.Add(reference);
            }

            if (!reader.IsEmptyElement)
            {
                parents.Push(reader.LocalName);
                if (reader.LocalName == "CreateIndex")
                {
                    indexBuilds++;
                }
            }
        }

        return maintenanceStatement ? found.Where(r => r.Access == PlanAccess.Write).ToList() : found.ToList();
    }

    private static bool IsMaintenance(string? statementType, string? statementText) =>
        (statementType is not null && MaintenanceStatements.Any(m => statementType.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
        || (statementText is not null
            && (statementText.Contains("_MS_UPDSTATS_TBL", StringComparison.OrdinalIgnoreCase)
                || statementText.TrimStart().StartsWith("SELECT StatMan(", StringComparison.OrdinalIgnoreCase)));

    private static PlanAccess? AccessFor(string parent) => parent switch
    {
        "IndexScan" or "TableScan" => PlanAccess.Read,
        "Update" or "SimpleUpdate" or "ScalarInsert" or "CreateIndex" => PlanAccess.Write,
        _ => null,
    };

    private static PlanObjectReference? Reference(XmlReader reader, PlanAccess access)
    {
        var database = Unquote(reader.GetAttribute("Database"));
        var schema = Unquote(reader.GetAttribute("Schema"));
        var table = Unquote(reader.GetAttribute("Table"));
        if (schema is null || table is null
            || table.StartsWith('#') || table.StartsWith('@')
            || string.Equals(database, "tempdb", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new PlanObjectReference(database, schema, table, Unquote(reader.GetAttribute("Index")), access);
    }

    /// <summary>Plans write names as <c>[name]</c>, with <c>]</c> doubled inside.</summary>
    internal static string? Unquote(string? name) =>
        name switch
        {
            null or "" => null,
            ['[', .. var inner, ']'] => new string(inner).Replace("]]", "]", StringComparison.Ordinal),
            _ => name,
        };
}
