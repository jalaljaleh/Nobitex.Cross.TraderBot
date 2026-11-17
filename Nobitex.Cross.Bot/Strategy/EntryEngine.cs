using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.MarketData;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.Strategy;

/// <summary>Plan for one ladder level that should be resting on the exchange.</summary>
/// <param name="StepIndex">Ladder step index.</param>
/// <param name="LimitPrice">Target limit price.</param>
/// <param name="Quantity">Target quantity.</param>
/// <param name="ClientOrderId">Idempotency key derived from market + step + price grid.</param>
public sealed record EntryPlan(
    int StepIndex,
    decimal LimitPrice,
    decimal Quantity,
    string ClientOrderId);

/// <summary>Builds the passive buy ladder that should exist for a market right now.</summary>
public sealed class EntryEngine
{
    private readonly LadderCalculator _ladder;
    private readonly ExecutionOptions _execution;
    private readonly ILogger<EntryEngine> _logger;

    /// <summary>Creates the engine.</summary>
    public EntryEngine(LadderCalculator ladder, ExecutionOptions execution, ILogger<EntryEngine> logger)
    {
        _ladder = ladder;
        _execution = execution;
        _logger = logger;
    }

    /// <summary>
    /// Computes the desired ladder and filters it through the maker-only rule and the market rules.
    /// </summary>
    /// <param name="marketKey">Configuration key of the market.</param>
    /// <param name="market">Market configuration.</param>
    /// <param name="rules">Exchange trading rules.</param>
    /// <param name="snapshot">Current market snapshot (local book + reference + fair price).</param>
    /// <param name="maxOrderSizeQuote">Risk cap for a single order.</param>
    /// <param name="maxOpenOrders">Risk cap for the number of resting orders.</param>
    /// <returns>The levels that may be placed as passive orders.</returns>
    public IReadOnlyList<EntryPlan> BuildPlans(
        string marketKey,
        MarketOptions market,
        MarketRules rules,
        MarketSnapshot snapshot,
        decimal maxOrderSizeQuote,
        int maxOpenOrders)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var levels = _ladder.Compute(marketKey, market, rules, snapshot.FairPrice, maxOrderSizeQuote);
        var bestAsk = snapshot.LocalBook.BestAsk;

        if (bestAsk is null)
        {
            _logger.LogWarning("{Market}: local order book has no asks — no entry plans", marketKey);
            return Array.Empty<EntryPlan>();
        }

        var plans = new List<EntryPlan>(levels.Count);

        foreach (var level in levels)
        {
            if (plans.Count >= maxOpenOrders)
            {
                _logger.LogDebug("{Market}: MaxOpenOrders reached, skipping step {Step}", marketKey, level.StepIndex);
                break;
            }

            // Maker-only gate: Nobitex has no post-only flag, so we refuse anything that could
            // cross the book and execute immediately as a taker.
            var makerCeiling = bestAsk.Value * (1m - _execution.MakerBufferPercent / 100m);
            if (level.LimitPrice >= makerCeiling)
            {
                _logger.LogDebug(
                    "{Market}: step {Step} price {Price} would cross the book (bestAsk {BestAsk}) — skipped",
                    marketKey, level.StepIndex, level.LimitPrice, bestAsk.Value);
                continue;
            }

            // Price deviation guard: never quote too far from the fair price.
            var deviation = Math.Abs(snapshot.FairPrice - level.LimitPrice) / snapshot.FairPrice * 100m;
            if (deviation > market.MarketRules.MaxPriceDeviationPercent)
            {
                _logger.LogWarning("{Market}: step {Step} deviates {Deviation:0.00}% from fair price — skipped",
                    marketKey, level.StepIndex, deviation);
                continue;
            }

            plans.Add(new EntryPlan(
                level.StepIndex,
                level.LimitPrice,
                level.Quantity,
                BuildClientOrderId(marketKey, level.StepIndex, level.LimitPrice)));
        }

        return plans;
    }

    /// <summary>
    /// Deterministic idempotency key: same market + step + price grid always produce the same key,
    /// so a restart can never duplicate an order.
    /// </summary>
    public static string BuildClientOrderId(string marketKey, int stepIndex, decimal limitPrice)
    {
        var market = marketKey.Replace("/", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var price = ((long)(limitPrice * 100m)).ToString();

        // Nobitex limits clientOrderId to 32 characters.
        var key = $"ce-{market}-{stepIndex}-{price}";
        return key.Length <= 32 ? key : key[..32];
    }
}