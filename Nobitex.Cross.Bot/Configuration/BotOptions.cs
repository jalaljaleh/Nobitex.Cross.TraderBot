using System.ComponentModel.DataAnnotations;

namespace Nobitex.Cross.Bot.Configuration;

/// <summary>Execution mode of the bot.</summary>
public enum RunMode
{
    /// <summary>Default. Decisions are computed and logged; nothing is ever sent to the exchange.</summary>
    DryRun,

    /// <summary>Orders are simulated against live order books with simulated balances and fills.</summary>
    PaperTrading,

    /// <summary>Real orders are sent to Nobitex. Requires explicit confirmation (see Program.cs).</summary>
    LiveTrading,
}

/// <summary>How the price offset of each ladder step is derived.</summary>
public enum SpacingModel
{
    /// <summary>Fixed percentage offsets configured in <see cref="LadderStepOptions.OffsetPercent"/>.</summary>
    FixedPercent,

    /// <summary>Reserved for v2: offsets derived from ATR multiples.</summary>
    Atr,
}

/// <summary>Currency in which trading fees are charged.</summary>
public enum FeeAsset
{
    /// <summary>Fee is deducted from the received base asset (typical for buys).</summary>
    Base,

    /// <summary>Fee is deducted from the quote asset.</summary>
    Quote,
}

/// <summary>Root configuration bound from appsettings.json + environment variables.</summary>
public sealed class BotOptions
{
    /// <summary>Section name in appsettings.json.</summary>
    public const string SectionName = "Bot";

    /// <summary>Run mode. Defaults to <see cref="RunMode.DryRun"/>.</summary>
    public RunMode Mode { get; set; } = RunMode.DryRun;

    /// <summary>Markets keyed by the unified symbol, e.g. <c>BTC/USDT</c>. Each market has an isolated budget.</summary>
    public Dictionary<string, MarketOptions> Markets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fair price settings.</summary>
    public FairPriceOptions FairPrice { get; set; } = new();

    /// <summary>Binance price guard settings.</summary>
    public GuardOptions Guard { get; set; } = new();

    /// <summary>Risk limits.</summary>
    public RiskOptions Risk { get; set; } = new();

    /// <summary>Execution loop settings.</summary>
    public ExecutionOptions Execution { get; set; } = new();

    /// <summary>Persistence settings.</summary>
    public PersistenceOptions Persistence { get; set; } = new();
}

/// <summary>Settings of a single tradable market.</summary>
public sealed class MarketOptions
{
    /// <summary>When <c>false</c> the market is loaded but never quoted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Total budget allocated to this market, in USDT. Never shared with other markets.</summary>
    [Range(typeof(decimal), "1", "1000000000")]
    public decimal BudgetUsdt { get; set; } = 1000m;

    /// <summary>Percentage of the budget intentionally left unused (0–90).</summary>
    [Range(0, 90)]
    public decimal ReservePercent { get; set; } = 5m;

    /// <summary>Base / quote asset pair.</summary>
    public SymbolOptions Symbol { get; set; } = new();

    /// <summary>Exchange trading rules applied before every submission.</summary>
    public MarketRulesOptions MarketRules { get; set; } = new();

    /// <summary>Fee parameters used by the profit calculator.</summary>
    public FeeOptions Fee { get; set; } = new();

    /// <summary>Order ladder definition.</summary>
    public LadderOptions Ladder { get; set; } = new();

    /// <summary>Exit / take-profit definition.</summary>
    public ExitOptions Exit { get; set; } = new();
}

/// <summary>Base and quote asset of a market.</summary>
public sealed class SymbolOptions
{
    /// <summary>Base asset, e.g. <c>BTC</c>.</summary>
    public string Base { get; set; } = string.Empty;

    /// <summary>Quote asset, e.g. <c>USDT</c>. Must be USDT for this strategy.</summary>
    public string Quote { get; set; } = "USDT";

    /// <summary>Unified symbol, e.g. <c>BTC/USDT</c>.</summary>
    public string Unified => $"{Base.ToUpperInvariant()}/{Quote.ToUpperInvariant()}";
}

/// <summary>Exchange trading rules of a market (validated at startup).</summary>
public sealed class MarketRulesOptions
{
    /// <summary>Minimum price increment (tick size).</summary>
    public decimal PriceTick { get; set; } = 0.01m;

    /// <summary>Minimum quantity increment (lot size / step size).</summary>
    public decimal QuantityStep { get; set; } = 0.00001m;

    /// <summary>Minimum order quantity.</summary>
    public decimal MinQuantity { get; set; } = 0m;

    /// <summary>Minimum order notional in the quote asset.</summary>
    public decimal MinNotional { get; set; } = 11m;

    /// <summary>Maximum order quantity (hard cap).</summary>
    public decimal MaxQuantity { get; set; } = decimal.MaxValue;

    /// <summary>Maximum permitted price deviation from fair price, in percent.</summary>
    public decimal MaxPriceDeviationPercent { get; set; } = 3m;
}

/// <summary>Fee parameters of a market.</summary>
public sealed class FeeOptions
{
    /// <summary>Maker fee rate (0.001 = 0.1 %).</summary>
    public decimal MakerRate { get; set; } = 0.001m;

    /// <summary>Taker fee rate (0.001 = 0.1 %).</summary>
    public decimal TakerRate { get; set; } = 0.001m;

    /// <summary>Currency the fee is charged in (Nobitex reports a bare number).</summary>
    public FeeAsset FeeAsset { get; set; } = FeeAsset.Base;
}

/// <summary>Order ladder definition.</summary>
public sealed class LadderOptions
{
    /// <summary>Spacing model. Only <see cref="SpacingModel.FixedPercent"/> is implemented in v1.</summary>
    public SpacingModel SpacingModel { get; set; } = SpacingModel.FixedPercent;

    /// <summary>
    /// Ladder steps. The list is intentionally <b>empty</b> by default.
    /// <para>
    /// ⚠ The configuration binder (<c>Get&lt;T&gt;()</c> / <c>Bind()</c>) <b>appends</b> to items that
    /// already exist in a <c>List&lt;T&gt;</c>; it does not replace them. A pre-populated default list would
    /// therefore be merged with the configured one and produce 25+50+25 + 25+50+25 = 200%.
    /// Defaults are filled in by <see cref="OptionsDefaults.Apply"/> only when the list is empty.
    /// </para>
    /// </summary>
    public List<LadderStepOptions> Steps { get; set; } = new();
}

/// <summary>A single ladder step.</summary>
public sealed class LadderStepOptions
{
    /// <summary>Distance below the fair price, in percent (1 = 1 % below fair price).</summary>
    [Range(0.01, 90)]
    public decimal OffsetPercent { get; set; } = 1m;

    /// <summary>Share of the deployed budget for this step, in percent.</summary>
    [Range(0.01, 100)]
    public decimal AllocationPercent { get; set; } = 25m;
}

/// <summary>Exit / take-profit definition of a market.</summary>
public sealed class ExitOptions
{
    /// <summary>Minimum NET profit after fees and slippage, in percent.</summary>
    public decimal TargetNetProfitPercent { get; set; } = 0.6m;

    /// <summary>Slippage allowance added to the required sell price, in percent.</summary>
    public decimal SlippageAllowancePercent { get; set; } = 0.15m;

    /// <summary>Maximum slippage tolerated by an emergency market exit, in percent.</summary>
    public decimal MaxSlippagePercent { get; set; } = 0.5m;

    /// <summary>
    /// Multi-stage take profit. Fill percentages must sum to 100.
    /// Intentionally <b>empty</b> by default — the configuration binder appends to existing list items,
    /// so a pre-populated default would be merged with the configured stages (50+50 + 50+50 = 200%).
    /// Defaults are filled in by <see cref="OptionsDefaults.Apply"/> only when the list is empty.
    /// </summary>
    public List<TakeProfitStage> TakeProfitStages { get; set; } = new();

    /// <summary>Maximum time a position may stay open before it is flagged for exit.</summary>
    public TimeSpan MaxHoldingTime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>When <c>true</c>, Live mode may liquidate at market during an emergency.</summary>
    public bool EmergencyExit { get; set; }
}

/// <summary>One take-profit stage.</summary>
public sealed class TakeProfitStage
{
    /// <summary>Percentage of the position released by this stage.</summary>
    [Range(1, 100)]
    public decimal FillPercent { get; set; } = 100m;

    /// <summary>Extra premium above the required sell price, in percent.</summary>
    public decimal PremiumPercent { get; set; }
}

/// <summary>
/// Applies the built-in defaults to collections that the configuration binder fills in.
/// </summary>
/// <remarks>
/// <para>
/// Why this class exists: <c>ConfigurationBinder</c> <b>appends</b> to the items already present in a
/// <c>List&lt;T&gt;</c> instead of replacing them. Declaring defaults directly on the property
/// (for example <c>Steps = { 25, 50, 25 }</c>) therefore produces a merged list once
/// <c>appsettings.json</c> is bound — and the validator reports
/// <c>"allocations sum to 200%; it must not exceed 100%"</c>.
/// </para>
/// <para>
/// The fix is to keep the lists empty and fill them here, <b>after</b> binding, only when the
/// configuration did not provide any item.
/// </para>
/// </remarks>
public static class OptionsDefaults
{
    /// <summary>Default ladder: 3 steps at −1%, −2%, −3% with 25% / 50% / 25% of the deployed budget.</summary>
    public static readonly IReadOnlyList<LadderStepOptions> LadderSteps = new[]
    {
        new LadderStepOptions { OffsetPercent = 1m, AllocationPercent = 25m },
        new LadderStepOptions { OffsetPercent = 2m, AllocationPercent = 50m },
        new LadderStepOptions { OffsetPercent = 3m, AllocationPercent = 25m },
    };

    /// <summary>Default exit: two stages releasing 50% + 50% of the position.</summary>
    public static readonly IReadOnlyList<TakeProfitStage> TakeProfitStages = new[]
    {
        new TakeProfitStage { FillPercent = 50m, PremiumPercent = 0.4m },
        new TakeProfitStage { FillPercent = 50m, PremiumPercent = 0.9m },
    };

    /// <summary>Fills every empty list with its default values. Must run right after configuration binding.</summary>
    public static void Apply(BotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var market in options.Markets.Values)
        {
            if (market.Ladder.Steps.Count == 0)
            {
                market.Ladder.Steps.AddRange(LadderSteps);
            }

            if (market.Exit.TakeProfitStages.Count == 0)
            {
                market.Exit.TakeProfitStages.AddRange(TakeProfitStages);
            }
        }
    }
}

/// <summary>Fair price configuration.</summary>
public sealed class FairPriceOptions
{
    /// <summary>Exchange providing the reference price (only Binance is implemented).</summary>
    public string ReferenceExchange { get; set; } = "Binance";

    /// <summary>Reference market symbol, e.g. <c>BTC/USDT</c>.</summary>
    public string ReferenceSymbol { get; set; } = "BTC/USDT";

    /// <summary>Maximum accepted age of the reference tick.</summary>
    public TimeSpan MaxReferenceAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Local basis settings.</summary>
    public LocalBasisOptions LocalBasis { get; set; } = new();
}

/// <summary>Local basis (persistent premium/discount of Nobitex vs Binance) settings.</summary>
public sealed class LocalBasisOptions
{
    /// <summary>Estimator mode. v1 implements the median over a rolling window.</summary>
    public string Mode { get; set; } = "MedianWindow";

    /// <summary>Number of samples kept in the rolling window.</summary>
    public int Window { get; set; } = 600;

    /// <summary>Manual override. When set, the estimator is disabled and this value is used.</summary>
    public decimal? Override { get; set; }

    /// <summary>Freeze updates when the observed basis deviates more than N sigmas from the median.</summary>
    public decimal DeviationFreezeSigma { get; set; } = 3m;
}

/// <summary>Binance price guard thresholds.</summary>
public sealed class GuardOptions
{
    /// <summary>Maximum absolute price change per second before a trip, in percent.</summary>
    public decimal MaxDropPerSecondPercent { get; set; } = 0.35m;

    /// <summary>Maximum rolling volatility before a trip, in percent.</summary>
    public decimal MaxVolatilityPercent { get; set; } = 1.2m;

    /// <summary>Spread widening factor relative to the baseline spread.</summary>
    public decimal MaxSpreadWidenFactor { get; set; } = 3m;

    /// <summary>Maximum fair price jump between two evaluations, in percent.</summary>
    public decimal MaxFairPriceJumpPercent { get; set; } = 0.5m;

    /// <summary>After a trip, no new quotes are placed for this duration.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(45);
}

/// <summary>Risk limits.</summary>
public sealed class RiskOptions
{
    /// <summary>Maximum base-asset inventory per market.</summary>
    public decimal MaxInventoryBase { get; set; } = decimal.MaxValue;

    /// <summary>Maximum deployed exposure per market, in quote asset.</summary>
    public decimal MaxExposureUsdt { get; set; } = decimal.MaxValue;

    /// <summary>Maximum drawdown of the market budget, in percent. Breach pauses the market.</summary>
    public decimal MaxDrawdownPercent { get; set; } = 4m;

    /// <summary>Maximum time a position may be held (falls back to the market exit setting).</summary>
    public TimeSpan MaxHoldingTime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Maximum number of simultaneously open orders per market.</summary>
    public int MaxOpenOrders { get; set; } = 6;

    /// <summary>Maximum notional of a single order, in quote asset.</summary>
    public decimal MaxOrderSizeUsdt { get; set; } = 500m;

    /// <summary>Maximum permitted deviation of a submitted price from fair price, in percent.</summary>
    public decimal MaxPriceDeviationPercent { get; set; } = 3m;

    /// <summary>Reference move that counts as a market shock, in percent.</summary>
    public decimal MarketShockThresholdPercent { get; set; } = 2m;

    /// <summary>Global kill switch.</summary>
    public bool EmergencyStop { get; set; }

    /// <summary>Number of consecutive adapter failures that count as "exchange unreachable".</summary>
    public int MaxConsecutiveFailures { get; set; } = 3;
}

/// <summary>Execution loop settings.</summary>
public sealed class ExecutionOptions
{
    /// <summary>Interval between engine ticks.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// An order is only re-quoted when its price drifts further than this percentage from the
    /// current target. Protects the queue priority of resting orders.
    /// </summary>
    public decimal RequoteBandPercent { get; set; } = 0.25m;

    /// <summary>The drift must persist this long before a re-quote is executed.</summary>
    public TimeSpan RequoteConfirm { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Minimum delay between two actions on the same ladder step.</summary>
    public TimeSpan RequoteCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maker buffer: a buy limit must sit at least this percentage below the local best ask,
    /// otherwise it is rejected client-side (Nobitex has no post-only flag).
    /// </summary>
    public decimal MakerBufferPercent { get; set; } = 0.05m;

    /// <summary>Maximum age of a resting order before it is cancelled and re-quoted.</summary>
    public TimeSpan MaxOrderAge { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Cancel every resting order when the process stops.</summary>
    public bool CancelAllOnExit { get; set; }

    /// <summary>Deduplicate identical order intents within this window.</summary>
    public TimeSpan DuplicateOrderWindow { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Persistence settings.</summary>
public sealed class PersistenceOptions
{
    /// <summary>Storage provider: <c>Sqlite</c> or <c>InMemory</c>.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>SQLite database file path (relative paths resolve next to the executable).</summary>
    public string DatabasePath { get; set; } = "cross-exchange-bot.db";
}

/// <summary>Validates configuration consistency at startup and reports every problem at once.</summary>
public static class OptionsValidator
{
    /// <summary>Returns a list of human readable errors; empty means the configuration is valid.</summary>
    public static IReadOnlyList<string> Validate(BotOptions options)
    {
        var errors = new List<string>();

        if (options.Markets.Count == 0)
        {
            errors.Add("Bot:Markets must contain at least one market.");
        }

        foreach (var (key, market) in options.Markets)
        {
            var prefix = $"Bot:Markets:{key}";

            if (!string.Equals(market.Symbol.Quote, "USDT", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{prefix}:Symbol:Quote must be 'USDT' — this strategy prices both venues in USDT.");
            }

            if (market.BudgetUsdt <= 0)
            {
                errors.Add($"{prefix}:BudgetUsdt must be greater than zero.");
            }

            if (market.ReservePercent is < 0 or > 90)
            {
                errors.Add($"{prefix}:ReservePercent must be between 0 and 90.");
            }

            if (market.Ladder.Steps.Count == 0)
            {
                errors.Add($"{prefix}:Ladder:Steps must contain at least one step.");
            }

            var total = market.Ladder.Steps.Sum(s => s.AllocationPercent);
            if (total > 100m)
            {
                errors.Add($"{prefix}:Ladder:Steps allocations sum to {total}%; it must not exceed 100%.");
            }

            if (market.Ladder.SpacingModel == SpacingModel.Atr)
            {
                errors.Add($"{prefix}:Ladder:SpacingModel 'Atr' is reserved for v2 and is not implemented yet.");
            }

            var stageTotal = market.Exit.TakeProfitStages.Sum(s => s.FillPercent);
            if (stageTotal != 100m)
            {
                errors.Add($"{prefix}:Exit:TakeProfitStages must sum to 100% (currently {stageTotal}%).");
            }

            if (market.MarketRules.PriceTick <= 0 || market.MarketRules.QuantityStep <= 0)
            {
                errors.Add($"{prefix}:MarketRules PriceTick and QuantityStep must be greater than zero.");
            }
        }

        if (options.Execution.MakerBufferPercent < 0)
        {
            errors.Add("Bot:Execution:MakerBufferPercent must not be negative.");
        }

        return errors;
    }
}