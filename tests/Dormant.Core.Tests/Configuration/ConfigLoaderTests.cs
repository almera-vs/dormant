using Dormant.Core.Configuration;

namespace Dormant.Core.Tests.Configuration;

public sealed class ConfigLoaderTests
{
    private static readonly string SourcePath = Path.Combine(Path.GetTempPath(), "dormant-tests", "dormant.yaml");
    private static readonly string BaseDirectory = Path.GetDirectoryName(SourcePath)!;

    // The documented example configuration, verbatim.
    private const string SpecExample = """
        version: 1
        store:
          path: /var/lib/dormant/dormant.db
        collection:
          interval: 15m
        instances:
          - name: erp-prod
            connection: { env: DORMANT_ERP_PROD }        # full connection string; integrated auth allowed
            databases: { include: ["*"], exclude: ["master", "model", "msdb", "tempdb"] }
            query_store: { enabled: true, lookback: 30d }
            maintenance_windows: ["Sun 01:00-05:00 Europe/Warsaw"]
        classification:
          hot_days: 7
          warm_days: 30
          dormant_days: 180
          min_coverage: 0.9
        cost:
          currency: EUR
          data_storage_per_gb_month: 0
          backup_copies: 1
          backup_storage_per_gb_month: 0
        report:
          out_dir: /var/lib/dormant/reports
        """;

    private const string Minimal = """
        version: 1
        store: { path: dormant.db }
        instances:
          - name: a
            connection: { env: A }
        """;

    [Fact]
    public void Loads_the_documented_example_exactly()
    {
        var config = ConfigLoader.Parse(SpecExample, SourcePath);

        config.Version.ShouldBe(1);
        config.Store.Path.ShouldBe(Path.GetFullPath("/var/lib/dormant/dormant.db"));
        config.Collection.Interval.ShouldBe(TimeSpan.FromMinutes(15));

        var instance = config.Instances.ShouldHaveSingleItem();
        instance.Name.ShouldBe("erp-prod");
        instance.Connection.ShouldBe(new EnvironmentSecret("DORMANT_ERP_PROD"));
        instance.Databases.Include.ShouldBe(["*"]);
        instance.Databases.Exclude.ShouldBe(["master", "model", "msdb", "tempdb"]);
        instance.QueryStore.ShouldBe(new QueryStoreOptions(Enabled: true, Lookback: TimeSpan.FromDays(30), StoreText: false));
        var window = instance.MaintenanceWindows.ShouldHaveSingleItem();
        window.Day.ShouldBe(DayOfWeek.Sunday);
        window.Text.ShouldBe("Sun 01:00-05:00 Europe/Warsaw");

        config.Classification.ShouldBe(new ClassificationOptions(7, 30, 180, 0.9));
        config.Cost.ShouldBe(new CostOptions("EUR", 0m, 1, 0m));
        config.Cost.HasPrices.ShouldBeFalse();
        config.Report.OutDir.ShouldBe(Path.GetFullPath("/var/lib/dormant/reports"));
    }

    [Fact]
    public void The_init_example_is_valid()
    {
        var config = ConfigLoader.Parse(ExampleConfig.Text, SourcePath);

        config.Instances.ShouldHaveSingleItem().Name.ShouldBe("erp-prod");
        config.Store.Path.ShouldBe(Path.Combine(BaseDirectory, "dormant.db"));
        config.Report.OutDir.ShouldBe(Path.Combine(BaseDirectory, "reports"));
    }

    [Fact]
    public void Applies_defaults_for_omitted_sections()
    {
        var config = ConfigLoader.Parse(Minimal, SourcePath);

        config.Collection.Interval.ShouldBe(CollectionOptions.DefaultInterval);
        config.Classification.ShouldBe(ClassificationOptions.Default);
        config.Cost.ShouldBe(CostOptions.Default);
        config.Report.OutDir.ShouldBe(Path.Combine(BaseDirectory, "reports"));
        var instance = config.Instances.ShouldHaveSingleItem();
        instance.Databases.ShouldBe(DatabaseFilter.Default);
        instance.QueryStore.ShouldBe(QueryStoreOptions.Default);
        instance.MaintenanceWindows.ShouldBeEmpty();
    }

    [Fact]
    public void Retention_defaults_to_14_days_raw_90_days_hourly_and_3_years_daily()
    {
        var config = ConfigLoader.Parse(Minimal, SourcePath);

        config.Store.Retention.ShouldBe(new RetentionOptions(TimeSpan.FromDays(14), TimeSpan.FromDays(90), TimeSpan.FromDays(1095)));
    }

    [Fact]
    public void Retention_can_be_set_and_years_are_365_days()
    {
        var yaml = Minimal.Replace("store: { path: dormant.db }", "store: { path: dormant.db, retention: { raw: 7d, hourly: 30d, daily: 5y } }", StringComparison.Ordinal);

        ConfigLoader.Parse(yaml, SourcePath).Store.Retention.ShouldBe(new RetentionOptions(TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromDays(1825)));
    }

    [Fact]
    public void Retention_levels_must_not_shrink()
    {
        var yaml = Minimal.Replace("store: { path: dormant.db }", "store: { path: dormant.db, retention: { raw: 30d, hourly: 14d } }", StringComparison.Ordinal);

        var error = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath)).Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("store.retention.hourly");
        error.Message.ShouldBe("must be at least as long as raw (30d), got 14d");
    }

    [Fact]
    public void Maintenance_windows_must_fit_whole_collection_intervals()
    {
        var yaml = Minimal.Replace("version: 1", "version: 1\ncollection: { interval: 30m }", StringComparison.Ordinal)
            + "\n    maintenance_windows: [\"Sun 01:00-01:45\"]";

        var error = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath)).Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("instances[0].maintenance_windows[0]");
        error.Message.ShouldBe("is 45m long; a maintenance window must last at least twice the collection interval (1h) so that whole collection intervals fit inside it");
    }

    [Fact]
    public void Omitted_exclude_still_skips_system_databases()
    {
        var config = ConfigLoader.Parse(Minimal + "\n    databases: { include: [\"Sales*\"] }", SourcePath);

        config.Instances[0].Databases.Exclude.ShouldBe(DatabaseFilter.SystemDatabases);
    }

    [Fact]
    public void Resolves_relative_paths_against_the_configuration_directory()
    {
        var config = ConfigLoader.Parse(Minimal.Replace("{ env: A }", "{ file: secrets/a.txt }", StringComparison.Ordinal), SourcePath);

        config.Instances[0].Connection.ShouldBe(new FileSecret(Path.Combine(BaseDirectory, "secrets", "a.txt")));
    }

    [Fact]
    public void Reports_every_problem_at_once_in_file_order()
    {
        const string yaml = """
            version: 1
            store: { path: x.db }
            collection: { interval: 1m }
            instances:
              - name: a
                connection: { env: A }
            classification: { warm_days: 3 }
            cost: { currency: euro }
            """;

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        ex.Errors.Select(e => e.Path).ShouldBe(["collection.interval", "classification.warm_days", "cost.currency"]);
        ex.Errors[0].Message.ShouldBe("must be at least 5m, got 1m");
        ex.Errors[0].Line.ShouldBe(3);
    }

    [Theory]
    [InlineData("collection: { intervall: 15m }", "collection.intervall", "unknown key; did you mean 'interval'?")]
    [InlineData("colection: { interval: 15m }", "colection", "unknown key; did you mean 'collection'?")]
    [InlineData("extra: 1", "extra", "unknown key; expected one of: version, store, collection, instances, classification, cost, report")]
    [InlineData("collection: { interval: soon }", "collection.interval", "expected a duration such as 15m, 6h or 30d, got 'soon'")]
    [InlineData("classification: { min_coverage: 0 }", "classification.min_coverage", "must be greater than 0 and at most 1, got 0")]
    [InlineData("classification: { min_coverage: 1.1 }", "classification.min_coverage", "must be greater than 0 and at most 1, got 1.1")]
    [InlineData("classification: { hot_days: 30 }", "classification.warm_days", "must be greater than hot_days (30), got 30")]
    [InlineData("classification: { dormant_days: 30 }", "classification.dormant_days", "must be greater than warm_days (30), got 30")]
    [InlineData("classification: { hot_days: seven }", "classification.hot_days", "expected a whole number, got 'seven'")]
    [InlineData("cost: { backup_copies: -1 }", "cost.backup_copies", "must be between 0 and 1000, got -1")]
    [InlineData("cost: { data_storage_per_gb_month: -0.5 }", "cost.data_storage_per_gb_month", "must be 0 or more, got -0.5")]
    [InlineData("cost:\n  data_storage_per_gb_month: 1,5", "cost.data_storage_per_gb_month", "expected a number such as 0.023, got '1,5'")]
    [InlineData("report: [a]", "report", "expected a mapping (key: value pairs), got a list")]
    public void Rejects_invalid_top_level_values(string extraYaml, string path, string message)
    {
        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(Minimal + "\n" + extraYaml, SourcePath));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(path);
        error.Message.ShouldBe(message);
    }

    [Theory]
    [InlineData("connection: \"Server=x;Password=secret\"", "instances[0].connection", "connection strings are never written in the configuration; use { env: VARIABLE_NAME } or { file: /path/to/secret }")]
    [InlineData("connection: { env: A, file: b }", "instances[0].connection", "set exactly one of 'env' or 'file'")]
    [InlineData("connection: { }", "instances[0].connection", "set exactly one of 'env' or 'file'")]
    [InlineData("connection: { env: 9LIVES }", "instances[0].connection.env", "'9LIVES' is not a valid environment variable name; use letters, digits and '_', not starting with a digit")]
    public void Rejects_connections_that_are_not_references(string connectionYaml, string path, string message)
    {
        var yaml = $"""
            version: 1
            store: {"{ path: x.db }"}
            instances:
              - name: a
                {connectionYaml}
            """;

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe(path);
        error.Message.ShouldBe(message);
    }

    [Fact]
    public void Rejects_unknown_connection_keys()
    {
        var yaml = Minimal.Replace("{ env: A }", "{ password: x }", StringComparison.Ordinal);

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        ex.Errors.ShouldContain(e => e.Path == "instances[0].connection.password" && e.Message == "unknown key; expected one of: env, file");
    }

    [Fact]
    public void Error_messages_never_echo_an_inline_connection_string()
    {
        var yaml = Minimal.Replace("{ env: A }", "\"Server=x;Password=hunter2\"", StringComparison.Ordinal);

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        ex.Message.ShouldNotContain("hunter2");
        ex.Errors.ShouldAllBe(e => !e.Message.Contains("hunter2"));
    }

    [Fact]
    public void Instance_names_must_be_unique_ignoring_case()
    {
        var yaml = Minimal + "\n  - name: A\n    connection: { env: B }";

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("instances[1].name");
        error.Message.ShouldBe("'A' is already used by instances[0]; instance names must be unique");
    }

    [Theory]
    [InlineData("", "(file)")]
    [InlineData("# only a comment\n", "(file)")]
    [InlineData("store: { path: x.db }\ninstances: [ { name: a, connection: { env: A } } ]", "version")]
    [InlineData("version: 1\ninstances: [ { name: a, connection: { env: A } } ]", "store")]
    [InlineData("version: 1\nstore: { path: x.db }", "instances")]
    [InlineData("version: 1\nstore: { path: x.db }\ninstances: []", "instances")]
    [InlineData("version: 1\nstore: { path: x.db }\ninstances: [ { connection: { env: A } } ]", "instances[0].name")]
    public void Requires_the_essential_fields(string yaml, string path)
    {
        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        ex.Errors.ShouldContain(e => e.Path == path);
    }

    [Fact]
    public void Rejects_unsupported_versions()
    {
        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(Minimal.Replace("version: 1", "version: 2", StringComparison.Ordinal), SourcePath));

        ex.Errors.ShouldHaveSingleItem().Message.ShouldBe("version 2 is not supported by this build of Dormant; expected 1");
    }

    [Fact]
    public void Reports_yaml_syntax_errors_with_their_position()
    {
        const string yaml = "version: 1\nstore:\n  path: x.db\n bad indent: 1\n";

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("(yaml)");
        error.Line.ShouldBe(4);
    }

    [Fact]
    public void Reports_unclosed_brackets_as_configuration_errors()
    {
        const string yaml = "version: 1\nstore: { path: x.db\ninstances: []\n";

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        ex.Errors.ShouldHaveSingleItem().Message.ShouldBe("is not valid YAML; check for an unclosed bracket, brace or quote");
    }

    [Fact]
    public void Rejects_duplicate_keys()
    {
        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(Minimal + "\nversion: 1", SourcePath));

        ex.Errors.ShouldHaveSingleItem().Line.ShouldNotBeNull();
    }

    [Fact]
    public void Reports_invalid_maintenance_windows_by_index()
    {
        var yaml = Minimal + "\n    maintenance_windows: [\"Sun 01:00-05:00\", \"Sun 25:00-26:00\"]";

        var ex = Should.Throw<ConfigException>(() => ConfigLoader.Parse(yaml, SourcePath));

        var error = ex.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("instances[0].maintenance_windows[1]");
        error.Message.ShouldBe("'25:00-26:00' is not a time range; use HH:MM-HH:MM in 24-hour time, e.g. 01:00-05:00");
    }

    [Fact]
    public async Task Reports_a_missing_file_as_a_configuration_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dormant-missing-{Guid.NewGuid():N}.yaml");

        var ex = await Should.ThrowAsync<ConfigException>(() => ConfigLoader.LoadAsync(path, TestContext.Current.CancellationToken));

        ex.Errors.ShouldHaveSingleItem().Path.ShouldBe("(file)");
    }
}
