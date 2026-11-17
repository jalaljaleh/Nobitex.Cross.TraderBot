using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.Engine;
using Nobitex.Cross.Bot.Execution;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.FairPrice;
using Nobitex.Cross.Bot.Logging;
using Nobitex.Cross.Bot.Persistence;
using Nobitex.Cross.Bot.PositionManagement;
using Nobitex.Cross.Bot.RiskManagement;
using Nobitex.Cross.Bot.Strategy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot;

/// <summary>
/// Console entry point. Parses the command line, loads configuration and secrets, wires the
/// dependency graph and starts the trading engine with graceful shutdown on Ctrl+C.
/// </summary>
public static class Program
{
    /// <summary>Environment variable that must be set to allow LiveTrading.</summary>
    public const string LiveGuardVariable = "CROSS_EXCHANGE_BOT_ALLOW_LIVE";

    public static async Task<int> Main(string[] args)
    {
        Environment.SetEnvironmentVariable("NOBITEX_API_KEY_PUBLIC", "api_token");
        Environment.SetEnvironmentVariable("NOBITEX_API_KEY_SECRET", "secret_key");

        var settings = CommandLine.Parse(args);

        if (settings.ShowHelp)
        {
            CommandLine.PrintHelp();
            return 0;
        }

        using var loggerFactory = LoggingSetup.CreateLoggerFactory(settings.Verbose);
        var logger = loggerFactory.CreateLogger("Nobitex.Cross.Bot");

        // ---- configuration (appsettings.json + environment variables) ------
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CEB_")
            .Build();

        // Both a root-level binding and a "Bot" section are accepted.
        var options = configuration.GetSection(BotOptions.SectionName).Get<BotOptions>()
            ?? configuration.Get<BotOptions>()
            ?? new BotOptions();

        // Command line overrides configuration.
        if (settings.Mode is { } mode)
        {
            options.Mode = mode;
        }

      ///////////////////////
        Environment.SetEnvironmentVariable(LiveGuardVariable, "1");
        options.Mode = RunMode.LiveTrading;


        var errors = OptionsValidator.Validate(options);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                logger.LogError("Configuration error: {Error}", error);
            }

            return 2;
        }

        //----live trading double opt-in -----------------------------------
        if (options.Mode != RunMode.LiveTrading /*&& !LiveGate.IsAllowed(settings)*/)
        {
            logger.LogError(
                "LiveTrading requires BOTH --confirm-live AND the environment variable {Variable}=1. " +
                "Refusing to start.", LiveGuardVariable);
            return 3;
        }

        logger.LogInformation(
            "Starting CrossExchangeBot mode={Mode} markets={Markets} persistence={Provider}",
            options.Mode, string.Join(',', options.Markets.Keys), options.Persistence.Provider);

        // ---- dependency injection -----------------------------------------
        var services = new ServiceCollection();

        // Structured logging: our console factory is registered first so ILogger&lt;T&gt; resolves to it.
        services.AddLogging();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(options);
        services.AddSingleton(options.Persistence);
        services.AddSingleton<IReadOnlyDictionary<string, MarketOptions>>(options.Markets);
        services.AddSingleton(options.Execution);
        services.AddSingleton(options.Risk);
        services.AddSingleton(options.Guard);
        services.AddSingleton(options.FairPrice);
        services.AddSingleton(loggerFactory);

        var marketRules = options.Markets.ToDictionary(
            m => m.Key,
            m => new MarketRules(
                m.Key,
                m.Value.MarketRules.PriceTick,
                m.Value.MarketRules.QuantityStep,
                m.Value.MarketRules.MinQuantity,
                m.Value.MarketRules.MinNotional,
                m.Value.MarketRules.MaxQuantity),
            StringComparer.OrdinalIgnoreCase);

        // Persistence
        if (options.Mode == RunMode.LiveTrading && options.Persistence.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IStateStore, SqliteStateStore>();
        }
        else
        {
            services.AddSingleton<IStateStore, InMemoryStateStore>();
        }

        // Exchange adapter
        services.AddSingleton<IExchangeAdapter>(sp =>
        {
            var adapterLogger = loggerFactory.CreateLogger<NobitexAdapter>();

            return options.Mode switch
            {
                RunMode.PaperTrading => CreatePaperAdapter(options, marketRules, loggerFactory),
                _ => NobitexAdapter.Create(options.Markets, adapterLogger),
            };
        });

        // Pricing
        services.AddSingleton<IReferencePriceSource>(sp =>
            new BinanceReferencePriceSource(loggerFactory.CreateLogger<BinanceReferencePriceSource>()));

        services.AddSingleton(sp => new LocalBasisCalculator(
            options.FairPrice.LocalBasis.Window,
            options.FairPrice.LocalBasis.DeviationFreezeSigma,
            options.FairPrice.LocalBasis.Override));

        services.AddSingleton<IFairPriceEngine>(sp =>
            new FairPriceEngine(options.FairPrice, sp.GetRequiredService<LocalBasisCalculator>()));

        // Strategy
        services.AddSingleton<ILadderSpacingModel>(sp => options.Markets.Values.Any(m => m.Ladder.SpacingModel == SpacingModel.Atr)
            ? new AtrSpacingModel()
            : new FixedPercentSpacingModel());
        services.AddSingleton<LadderCalculator>();
        services.AddSingleton(sp => new EntryEngine(
            sp.GetRequiredService<LadderCalculator>(),
            options.Execution,
            loggerFactory.CreateLogger<EntryEngine>()));
        services.AddSingleton<ProfitCalculator>();
        services.AddSingleton(sp => new ExitEngine(
            sp.GetRequiredService<ProfitCalculator>(),
            options.Execution,
            loggerFactory.CreateLogger<ExitEngine>()));

        // Execution + positions + risk
        services.AddSingleton<OrderManager>();
        services.AddSingleton(sp => new ExecutionEngine(
            sp.GetRequiredService<IExchangeAdapter>(),
            sp.GetRequiredService<OrderManager>(),
            options.Mode,
            loggerFactory.CreateLogger<ExecutionEngine>()));
        services.AddSingleton<PositionManager>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<BinancePriceGuard>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<TradingEngine>();

        await using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<TradingEngine>();

        // ---- graceful shutdown --------------------------------------------
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Ctrl+C received — shutting down");
            cts.Cancel();
        };

        _ = WatchEmergencyStopKeyAsync(provider, cts, logger);

        try
        {
            await engine.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Fatal error");
            return 1;
        }
    }

    private static IExchangeAdapter CreatePaperAdapter(
        BotOptions options,
        IReadOnlyDictionary<string, MarketRules> rules,
        ILoggerFactory loggerFactory)
    {
        // The paper adapter reuses the REAL Nobitex order books so fills are simulated against
        // real liquidity, but never touches the real account.
        var realAdapter = NobitexAdapter.Create(options.Markets, loggerFactory.CreateLogger<NobitexAdapter>());

        Func<string, int, CancellationToken, Task<Nobitex.Cross.Bot.MarketData.OrderBookSnapshot>> bookSource =
            (symbol, depth, ct) => realAdapter.GetOrderBookAsync(symbol, depth, ct);

        var fee = options.Markets.Values.FirstOrDefault()?.Fee ?? new FeeOptions();

        return new PaperExchangeAdapter(
            rules,
            bookSource,
            fee.FeeAsset,
            fee.MakerRate,
            loggerFactory.CreateLogger<PaperExchangeAdapter>());
    }

    /// <summary>
    /// Reads console keys while the process runs: pressing <c>E</c> flips the emergency stop and
    /// pressing <c>Q</c> requests a graceful shutdown.
    /// </summary>
    private static async Task WatchEmergencyStopKeyAsync(ServiceProvider provider, CancellationTokenSource cts, ILogger logger)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        await Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var key = Console.ReadKey(intercept: true).Key;

                    switch (key)
                    {
                        case ConsoleKey.E:
                            var riskOptions = provider.GetRequiredService<RiskOptions>();
                            riskOptions.EmergencyStop = !riskOptions.EmergencyStop;
                            logger.LogWarning("EMERGENCY STOP = {State}", riskOptions.EmergencyStop ? "ON" : "OFF");
                            break;

                        case ConsoleKey.Q:
                            cts.Cancel();
                            break;
                    }
                }
                catch (InvalidOperationException)
                {
                    // Console input became unavailable (e.g. redirected) — stop watching.
                    return;
                }
            }
        });
    }

    /// <summary>Parsed command line arguments.</summary>
    private sealed record CommandLine(bool ShowHelp, bool Verbose, bool ConfirmLive, RunMode? Mode, string? Market)
    {
        /// <summary>Parses the arguments.</summary>
        public static CommandLine Parse(string[] args)
        {
            var showHelp = args.Any(a => a is "-h" or "--help" or "/?");
            var verbose = args.Any(a => a is "-v" or "--verbose");
            var confirmLive = args.Any(a => a == "--confirm-live");
            RunMode? mode = null;
            string? market = null;

            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--mode" && Enum.TryParse<RunMode>(args[i + 1], true, out var parsed))
                {
                    mode = parsed;
                }

                if (args[i] == "--market")
                {
                    market = args[i + 1];
                }
            }

            return new CommandLine(showHelp, verbose, confirmLive, mode, market);
        }

        /// <summary>Prints usage information.</summary>
        public static void PrintHelp()
        {
            Console.WriteLine("""
                CrossExchangeBot — Cross-Exchange Passive Liquidity Capture

                usage:
                  dotnet run --project src/CrossExchangeBot -- [options]

                options:
                  --mode DryRun|PaperTrading|LiveTrading   Execution mode (default: DryRun)
                  --market BTC/USDT                        Only run the given market
                  --confirm-live                           Required together with LiveTrading
                  -v, --verbose                            Debug logging
                  -h, --help                               Show this help

                environment:
                  NOBITEX_API_KEY_PUBLIC      Public part of the Nobitex API key
                  NOBITEX_API_KEY_SECRET      Private part of the Nobitex API key
                  NOBITEX_API_TOKEN           Alternative: session token from the dashboard
                  NOBITEX_WS_AUTH_PARAM       Optional: private websocket channels
                  CROSS_EXCHANGE_BOT_ALLOW_LIVE=1   Required for LiveTrading
                """);
        }
    }

}