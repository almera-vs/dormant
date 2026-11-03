using System.Text.RegularExpressions;

namespace Dormant.Demo;

/// <summary>
/// The subset of sqlcmd syntax the scripts in deploy/sql use: <c>:setvar Name "value"</c> lines, <c>$(Name)</c>
/// references, and batches separated by <c>GO</c> lines.
/// </summary>
public static partial class SqlScript
{
    /// <summary>The script's batches, with variables replaced. <paramref name="overrides"/> win over :setvar lines.</summary>
    /// <exception cref="InvalidOperationException">A variable is used but never set.</exception>
    public static IReadOnlyList<string> Batches(string script, IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var line in script.ReplaceLineEndings("\n").Split('\n'))
        {
            var setvar = SetVar().Match(line);
            if (setvar.Success)
            {
                variables[setvar.Groups["name"].Value] = setvar.Groups["value"].Value;
                continue;
            }

            lines.Add(line);
        }

        foreach (var (name, value) in overrides ?? new Dictionary<string, string>())
        {
            variables[name] = value;
        }

        var text = Reference().Replace(string.Join('\n', lines), match =>
            variables.TryGetValue(match.Groups["name"].Value, out var value)
                ? value
                : throw new InvalidOperationException($"The script uses $({match.Groups["name"].Value}) but never sets it."));

        return Go().Split(text).Where(batch => !string.IsNullOrWhiteSpace(batch)).Select(batch => batch.Trim()).ToList();
    }

    [GeneratedRegex("""^:setvar\s+(?<name>\w+)\s+"?(?<value>[^"]*)"?\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex SetVar();

    [GeneratedRegex(@"\$\((?<name>\w+)\)")]
    private static partial Regex Reference();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex Go();
}
