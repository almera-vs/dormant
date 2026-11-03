namespace Dormant.Core.Configuration;

/// <summary>
/// Include/exclude patterns for database names. Patterns support <c>*</c> (any run of characters) and
/// <c>?</c> (one character) and match case-insensitively. A database is selected when it matches at least
/// one include pattern and no exclude pattern.
/// </summary>
public sealed record DatabaseFilter(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    public static readonly IReadOnlyList<string> SystemDatabases = ["master", "model", "msdb", "tempdb"];

    public static readonly DatabaseFilter Default = new(["*"], SystemDatabases);

    public bool Matches(string databaseName)
    {
        ArgumentNullException.ThrowIfNull(databaseName);
        return Include.Any(p => Glob.IsMatch(p, databaseName)) && !Exclude.Any(p => Glob.IsMatch(p, databaseName));
    }
}

internal static class Glob
{
    public static bool IsMatch(string pattern, string text)
    {
        // Iterative wildcard matching with single-star backtracking: O(pattern * text) worst case, no regex.
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}
