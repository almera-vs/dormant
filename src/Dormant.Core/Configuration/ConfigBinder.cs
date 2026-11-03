using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Dormant.Core.Configuration;

/// <summary>Binds a YAML document to <see cref="DormantConfig"/>, collecting every problem rather than stopping at the first.</summary>
internal sealed partial class ConfigBinder
{
    public const int SupportedVersion = 1;

    private static readonly string[] RootKeys = ["version", "store", "collection", "instances", "classification", "cost", "report"];
    private static readonly string[] StoreKeys = ["path", "retention"];
    private static readonly string[] RetentionKeys = ["raw", "hourly", "daily"];
    private static readonly string[] CollectionKeys = ["interval"];
    private static readonly string[] InstanceKeys = ["name", "connection", "databases", "query_store", "maintenance_windows"];
    private static readonly string[] ConnectionKeys = ["env", "file"];
    private static readonly string[] DatabaseKeys = ["include", "exclude"];
    private static readonly string[] QueryStoreKeys = ["enabled", "lookback", "store_text"];
    private static readonly string[] ClassificationKeys = ["hot_days", "warm_days", "dormant_days", "min_coverage"];
    private static readonly string[] CostKeys = ["currency", "data_storage_per_gb_month", "backup_copies", "backup_storage_per_gb_month"];
    private static readonly string[] ReportKeys = ["out_dir"];

    private readonly ErrorSink _errors = new();
    private readonly string _baseDirectory;
    private TimeSpan _interval = CollectionOptions.DefaultInterval;

    public ConfigBinder(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    public IReadOnlyList<ConfigError> Errors => _errors.Errors;

    public DormantConfig? Bind(YamlNode root, string sourcePath)
    {
        if (YamlSection.IsNull(root))
        {
            _errors.Add("(file)", "is empty; run 'dormant init' to write an example configuration", root);
            return null;
        }

        var section = YamlSection.Create(root, string.Empty, _errors, RootKeys);
        if (section is null)
        {
            return null;
        }

        var version = BindVersion(section);
        var store = BindStore(section);
        var collection = BindCollection(section);
        _interval = collection.Interval;
        var instances = BindInstances(section);
        var classification = BindClassification(section);
        var cost = BindCost(section);
        var report = BindReport(section);

        if (_errors.Errors.Count > 0 || store is null)
        {
            return null;
        }

        return new DormantConfig(version, sourcePath, store, collection, instances, classification, cost, report);
    }

    private static int BindVersion(YamlSection root)
    {
        if (!root.Has("version"))
        {
            root.Error("version", $"is required; set 'version: {SupportedVersion}'");
            return SupportedVersion;
        }

        var version = root.Int("version", SupportedVersion, int.MinValue, int.MaxValue);
        if (version != SupportedVersion)
        {
            root.Error("version", $"version {version} is not supported by this build of Dormant; expected {SupportedVersion}");
        }

        return version;
    }

    private StoreOptions? BindStore(YamlSection root)
    {
        if (!root.Has("store"))
        {
            root.Error("store", "is required; set 'store: { path: /var/lib/dormant/dormant.db }'");
            return null;
        }

        var store = root.Section("store", StoreKeys);
        var path = store?.String("path", required: true);
        var retention = store is null ? RetentionOptions.Default : BindRetention(store);
        return path is null ? null : new StoreOptions(ResolvePath(path), retention);
    }

    private static RetentionOptions BindRetention(YamlSection store)
    {
        var defaults = RetentionOptions.Default;
        var section = store.Section("retention", RetentionKeys);
        if (section is null)
        {
            return defaults;
        }

        var raw = section.Duration("raw", defaults.Raw, TimeSpan.FromDays(1));
        var hourly = section.Duration("hourly", defaults.Hourly, TimeSpan.FromDays(1));
        var daily = section.Duration("daily", defaults.Daily, TimeSpan.FromDays(1));
        if (hourly < raw)
        {
            section.Error("hourly", $"must be at least as long as raw ({Configuration.Duration.Format(raw)}), got {Configuration.Duration.Format(hourly)}");
        }

        if (daily < hourly)
        {
            section.Error("daily", $"must be at least as long as hourly ({Configuration.Duration.Format(hourly)}), got {Configuration.Duration.Format(daily)}");
        }

        return new RetentionOptions(raw, hourly, daily);
    }

    private static CollectionOptions BindCollection(YamlSection root)
    {
        var collection = root.Section("collection", CollectionKeys);
        var interval = collection?.Duration("interval", CollectionOptions.DefaultInterval, CollectionOptions.MinimumInterval)
            ?? CollectionOptions.DefaultInterval;
        return new CollectionOptions(interval);
    }

    private List<InstanceOptions> BindInstances(YamlSection root)
    {
        var result = new List<InstanceOptions>();
        var sequence = root.Sequence("instances");
        if (sequence is null)
        {
            if (!root.Has("instances"))
            {
                root.Error("instances", "is required; list at least one SQL Server instance");
            }

            return result;
        }

        if (sequence.Children.Count == 0)
        {
            root.Error("instances", "is empty; list at least one SQL Server instance");
            return result;
        }

        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            var path = $"instances[{i}]";
            var section = YamlSection.Create(sequence.Children[i], path, _errors, InstanceKeys);
            if (section is null)
            {
                continue;
            }

            var instance = BindInstance(section);
            if (instance is null)
            {
                continue;
            }

            if (names.TryGetValue(instance.Name, out var first))
            {
                section.Error("name", $"'{instance.Name}' is already used by instances[{first}]; instance names must be unique");
                continue;
            }

            names.Add(instance.Name, i);
            result.Add(instance);
        }

        return result;
    }

    private InstanceOptions? BindInstance(YamlSection section)
    {
        var name = section.String("name", required: true);
        if (name is not null && !InstanceNamePattern().IsMatch(name))
        {
            section.Error("name", $"'{name}' is not a valid name; use 1-64 letters, digits, '.', '_' or '-', starting with a letter or digit");
            name = null;
        }

        var connection = BindConnection(section);
        var databases = BindDatabases(section);
        var queryStore = BindQueryStore(section);
        var windows = BindMaintenanceWindows(section);

        return name is null || connection is null || databases is null
            ? null
            : new InstanceOptions(name, connection, databases, queryStore, windows);
    }

    private SecretReference? BindConnection(YamlSection instance)
    {
        var node = instance.Get("connection");
        if (node is null)
        {
            instance.Error("connection", "is required; use { env: VARIABLE_NAME } or { file: /path/to/secret }");
            return null;
        }

        if (node is YamlScalarNode)
        {
            instance.Error("connection", "connection strings are never written in the configuration; use { env: VARIABLE_NAME } or { file: /path/to/secret }");
            return null;
        }

        var connection = instance.Section("connection", ConnectionKeys);
        if (connection is null)
        {
            return null;
        }

        var env = connection.String("env");
        var file = connection.String("file");
        if ((env is null) == (file is null))
        {
            if (connection.Has("env") == connection.Has("file"))
            {
                instance.Error("connection", "set exactly one of 'env' or 'file'");
            }

            return null;
        }

        if (env is not null)
        {
            if (!EnvironmentVariablePattern().IsMatch(env))
            {
                connection.Error("env", $"'{env}' is not a valid environment variable name; use letters, digits and '_', not starting with a digit");
                return null;
            }

            return new EnvironmentSecret(env);
        }

        return new FileSecret(ResolvePath(file!));
    }

    private static DatabaseFilter? BindDatabases(YamlSection instance)
    {
        if (!instance.Has("databases"))
        {
            return DatabaseFilter.Default;
        }

        var databases = instance.Section("databases", DatabaseKeys);
        if (databases is null)
        {
            return null;
        }

        var include = databases.StringList("include") ?? DatabaseFilter.Default.Include;
        if (include.Count == 0)
        {
            databases.Error("include", "is empty, so no database would be monitored; use [\"*\"] for all databases");
            return null;
        }

        var exclude = databases.StringList("exclude") ?? DatabaseFilter.SystemDatabases;
        return new DatabaseFilter(include, exclude);
    }

    private static QueryStoreOptions BindQueryStore(YamlSection instance)
    {
        var queryStore = instance.Section("query_store", QueryStoreKeys);
        if (queryStore is null)
        {
            return QueryStoreOptions.Default;
        }

        var defaults = QueryStoreOptions.Default;
        return new QueryStoreOptions(
            queryStore.Bool("enabled", defaults.Enabled),
            queryStore.Duration("lookback", defaults.Lookback, TimeSpan.FromHours(1)),
            queryStore.Bool("store_text", defaults.StoreText));
    }

    private List<MaintenanceWindow> BindMaintenanceWindows(YamlSection instance)
    {
        var result = new List<MaintenanceWindow>();
        var texts = instance.StringList("maintenance_windows");
        if (texts is null)
        {
            return result;
        }

        var sequence = (YamlSequenceNode)instance.Get("maintenance_windows")!;
        for (var i = 0; i < texts.Count; i++)
        {
            if (!MaintenanceWindow.TryParse(texts[i], out var window, out var error))
            {
                _errors.Add($"{instance.KeyPath("maintenance_windows")}[{i}]", error!, sequence.Children[i]);
            }
            else if (window!.Length < _interval * 2)
            {
                _errors.Add(
                    $"{instance.KeyPath("maintenance_windows")}[{i}]",
                    $"is {Configuration.Duration.Format(window.Length)} long; a maintenance window must last at least twice the collection interval ({Configuration.Duration.Format(_interval * 2)}) so that whole collection intervals fit inside it",
                    sequence.Children[i]);
            }
            else
            {
                result.Add(window);
            }
        }

        return result;
    }

    private static ClassificationOptions BindClassification(YamlSection root)
    {
        var defaults = ClassificationOptions.Default;
        var section = root.Section("classification", ClassificationKeys);
        if (section is null)
        {
            return defaults;
        }

        var hot = section.Int("hot_days", defaults.HotDays, 1, 3650);
        var warm = section.Int("warm_days", defaults.WarmDays, 1, 3650);
        var dormant = section.Int("dormant_days", defaults.DormantDays, 1, 3650);
        var coverage = section.Fraction("min_coverage", defaults.MinCoverage);

        if (warm <= hot)
        {
            section.Error("warm_days", string.Create(CultureInfo.InvariantCulture, $"must be greater than hot_days ({hot}), got {warm}"));
        }

        if (dormant <= warm)
        {
            section.Error("dormant_days", string.Create(CultureInfo.InvariantCulture, $"must be greater than warm_days ({warm}), got {dormant}"));
        }

        return new ClassificationOptions(hot, warm, dormant, coverage);
    }

    private static CostOptions BindCost(YamlSection root)
    {
        var defaults = CostOptions.Default;
        var section = root.Section("cost", CostKeys);
        if (section is null)
        {
            return defaults;
        }

        var currency = section.String("currency") ?? defaults.Currency;
        if (!CurrencyPattern().IsMatch(currency))
        {
            section.Error("currency", $"'{currency}' is not a currency code; use three capital letters such as EUR, USD or PLN");
        }

        return new CostOptions(
            currency,
            section.Decimal("data_storage_per_gb_month", defaults.DataStoragePerGbMonth, 0m),
            section.Int("backup_copies", defaults.BackupCopies, 0, 1000),
            section.Decimal("backup_storage_per_gb_month", defaults.BackupStoragePerGbMonth, 0m));
    }

    private ReportOptions BindReport(YamlSection root)
    {
        var section = root.Section("report", ReportKeys);
        var outDir = section?.String("out_dir") ?? "reports";
        return new ReportOptions(ResolvePath(outDir));
    }

    private string ResolvePath(string path) => Path.GetFullPath(Path.Combine(_baseDirectory, path));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex InstanceNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariablePattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();
}
