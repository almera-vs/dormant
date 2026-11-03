using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Dormant.Cli;

internal static class CliLogging
{
    /// <summary>
    /// Structured JSON logs, all written to standard error so that standard output carries only command results
    /// (reports, exports, validation output) and stays safe to pipe.
    /// </summary>
    public static ILoggerFactory Create(LogLevel minimumLevel) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddJsonConsole(options =>
            {
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                options.IncludeScopes = true;
            });
            builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        });
}
