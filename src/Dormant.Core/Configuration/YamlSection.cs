using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Dormant.Core.Configuration;

/// <summary>Collects configuration errors with their field path and source position.</summary>
internal sealed class ErrorSink
{
    private readonly List<ConfigError> _errors = [];

    public IReadOnlyList<ConfigError> Errors => _errors;

    public void Add(string path, string message, YamlNode? node = null) =>
        _errors.Add(node is null
            ? new ConfigError(path, message)
            : new ConfigError(path, message, (int)node.Start.Line, (int)node.Start.Column));
}

/// <summary>
/// A typed, error-reporting view over one YAML mapping. Unknown keys are reported immediately, with a
/// suggestion when the key looks like a typo of an allowed one.
/// </summary>
internal sealed class YamlSection
{
    private readonly Dictionary<string, (YamlNode Key, YamlNode Value)> _fields = new(StringComparer.Ordinal);
    private readonly ErrorSink _errors;

    private YamlSection(string path, YamlNode node, ErrorSink errors)
    {
        Path = path;
        Node = node;
        _errors = errors;
    }

    public string Path { get; }

    public YamlNode Node { get; }

    public static YamlSection? Create(YamlNode node, string path, ErrorSink errors, IReadOnlyCollection<string> allowedKeys)
    {
        if (node is not YamlMappingNode mapping)
        {
            errors.Add(path, $"expected a mapping (key: value pairs), got {Describe(node)}", node);
            return null;
        }

        var section = new YamlSection(path, node, errors);
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                errors.Add(path, "keys must be plain names", keyNode);
                continue;
            }

            var keyPath = Join(path, key);
            if (!allowedKeys.Contains(key))
            {
                errors.Add(keyPath, UnknownKeyMessage(key, allowedKeys), keyNode);
                continue;
            }

            if (!section._fields.TryAdd(key, (keyNode, valueNode)))
            {
                errors.Add(keyPath, "is set more than once", keyNode);
            }
        }

        return section;
    }

    public static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    public static bool IsNull(YamlNode node) =>
        node is YamlScalarNode scalar
        && (scalar.Tag == "tag:yaml.org,2002:null"
            || (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL"));

    public static string Describe(YamlNode node) => node switch
    {
        YamlMappingNode => "a mapping",
        YamlSequenceNode => "a list",
        YamlScalarNode s when IsNull(s) => "nothing",
        YamlScalarNode s => $"'{s.Value}'",
        _ => "an unsupported value",
    };

    /// <summary>The value node for <paramref name="key"/>, or null when the key is absent or explicitly empty.</summary>
    public YamlNode? Get(string key) =>
        _fields.TryGetValue(key, out var field) && !IsNull(field.Value) ? field.Value : null;

    public bool Has(string key) => Get(key) is not null;

    public string KeyPath(string key) => Join(Path, key);

    public void Error(string key, string message) =>
        _errors.Add(KeyPath(key), message, _fields.TryGetValue(key, out var f) ? f.Value : Node);

    public YamlSection? Section(string key, IReadOnlyCollection<string> allowedKeys) =>
        Get(key) is { } node ? Create(node, KeyPath(key), _errors, allowedKeys) : null;

    public string? String(string key, bool required = false)
    {
        var node = Get(key);
        if (node is null)
        {
            if (required)
            {
                Error(key, "is required");
            }

            return null;
        }

        if (node is not YamlScalarNode { Value: { } value })
        {
            Error(key, $"expected text, got {Describe(node)}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Error(key, "must not be blank");
            return null;
        }

        return value.Trim();
    }

    public int Int(string key, int defaultValue, int min, int max)
    {
        if (ScalarText(key, "a whole number") is not { } text)
        {
            return defaultValue;
        }

        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            Error(key, $"expected a whole number, got '{text}'");
            return defaultValue;
        }

        if (value < min || value > max)
        {
            Error(key, $"must be between {min} and {max}, got {value}");
            return defaultValue;
        }

        return value;
    }

    public decimal Decimal(string key, decimal defaultValue, decimal min)
    {
        if (ScalarText(key, "a number") is not { } text)
        {
            return defaultValue;
        }

        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            Error(key, $"expected a number such as 0.023, got '{text}'");
            return defaultValue;
        }

        if (value < min)
        {
            Error(key, string.Create(CultureInfo.InvariantCulture, $"must be {min} or more, got {value}"));
            return defaultValue;
        }

        return value;
    }

    public double Fraction(string key, double defaultValue)
    {
        if (ScalarText(key, "a number") is not { } text)
        {
            return defaultValue;
        }

        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value))
        {
            Error(key, $"expected a fraction between 0 and 1 such as 0.9, got '{text}'");
            return defaultValue;
        }

        if (value is <= 0 or > 1)
        {
            Error(key, string.Create(CultureInfo.InvariantCulture, $"must be greater than 0 and at most 1, got {value}"));
            return defaultValue;
        }

        return value;
    }

    public bool Bool(string key, bool defaultValue)
    {
        if (ScalarText(key, "true or false") is not { } text)
        {
            return defaultValue;
        }

        switch (text)
        {
            case "true" or "True" or "TRUE":
                return true;
            case "false" or "False" or "FALSE":
                return false;
            default:
                Error(key, $"expected true or false, got '{text}'");
                return defaultValue;
        }
    }

    public TimeSpan Duration(string key, TimeSpan defaultValue, TimeSpan min)
    {
        if (ScalarText(key, "a duration") is not { } text)
        {
            return defaultValue;
        }

        if (!Configuration.Duration.TryParse(text, out var value))
        {
            Error(key, $"expected a duration such as 15m, 6h or 30d, got '{text}'");
            return defaultValue;
        }

        if (value < min)
        {
            Error(key, $"must be at least {Configuration.Duration.Format(min)}, got {text}");
            return defaultValue;
        }

        return value;
    }

    public IReadOnlyList<string>? StringList(string key)
    {
        var node = Get(key);
        if (node is null)
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            Error(key, $"expected a list such as [\"a\", \"b\"], got {Describe(node)}");
            return null;
        }

        var result = new List<string>(sequence.Children.Count);
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            var item = sequence.Children[i];
            if (item is YamlScalarNode { Value: { } value } && !IsNull(item) && !string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
            else
            {
                _errors.Add($"{KeyPath(key)}[{i}]", $"expected non-empty text, got {Describe(item)}", item);
            }
        }

        return result;
    }

    public YamlSequenceNode? Sequence(string key)
    {
        var node = Get(key);
        if (node is null)
        {
            return null;
        }

        if (node is not YamlSequenceNode sequence)
        {
            Error(key, $"expected a list, got {Describe(node)}");
            return null;
        }

        return sequence;
    }

    private string? ScalarText(string key, string expected)
    {
        var node = Get(key);
        if (node is null)
        {
            return null;
        }

        if (node is not YamlScalarNode { Value: { } text })
        {
            Error(key, $"expected {expected}, got {Describe(node)}");
            return null;
        }

        return text.Trim();
    }

    private static string UnknownKeyMessage(string key, IReadOnlyCollection<string> allowed)
    {
        var suggestion = allowed
            .Select(a => (Name: a, Distance: EditDistance(key.ToLowerInvariant(), a)))
            .Where(x => x.Distance <= 2)
            .OrderBy(x => x.Distance)
            .Select(x => x.Name)
            .FirstOrDefault();

        return suggestion is not null
            ? $"unknown key; did you mean '{suggestion}'?"
            : $"unknown key; expected one of: {string.Join(", ", allowed)}";
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
