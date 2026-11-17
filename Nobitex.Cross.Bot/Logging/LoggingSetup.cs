using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.Logging;

/// <summary>
/// Builds the structured console logger used by the whole application.
/// Every message is timestamped in UTC and includes the category (component) name, which makes the
/// console output greppable when a live session needs to be audited.
/// </summary>
public static class LoggingSetup
{
    /// <summary>Creates a console logger factory with UTC timestamps.</summary>
    /// <param name="verbose">When <c>true</c>, debug level messages are shown as well.</param>
    public static ILoggerFactory CreateLoggerFactory(bool verbose = false)
        => LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.IncludeScopes = true;
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss.fff ";
                    console.UseUtcTimestamp = true;
                    console.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
                });
        });

    /// <summary>Component scope names used by the engine.</summary>
    public static class Scopes
    {
        /// <summary>Market data related messages.</summary>
        public const string MarketData = "MarketData";

        /// <summary>Order execution related messages.</summary>
        public const string Execution = "Execution";

        /// <summary>Risk and guard related messages.</summary>
        public const string Risk = "Risk";

        /// <summary>Reconciliation messages.</summary>
        public const string Reconciliation = "Reconciliation";
    }
}