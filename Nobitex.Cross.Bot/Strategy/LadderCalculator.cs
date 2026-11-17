using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.ExchangeAdapters;

namespace Nobitex.Cross.Bot.Strategy;

/// <summary>One computed ladder level.</summary>
/// <param name="StepIndex">Zero based step index.</param>
/// <param name="OffsetPercent">Distance below the fair price, in percent.</param>
/// <param name="LimitPrice">Limit price, normalized down to the tick grid.</param>
/// <param name="AllocationPercent">Share of the deployed budget.</param>
/// <param name="OrderValue">Notional of the order in the quote asset.</param>
/// <param name="Quantity">Order quantity in base asset, normalized to the step grid.</param>
public sealed record LadderLevel(
    int StepIndex,
    decimal OffsetPercent,
    decimal LimitPrice,
    decimal AllocationPercent,
    decimal OrderValue,
    decimal Quantity);

/// <summary>Produces the offsets of the ladder steps.</summary>
public interface ILadderSpacingModel
{
    /// <summary>Returns the offsets (in percent below fair price) of the ladder.</summary>
    IReadOnlyList<decimal> GetOffsets(LadderOptions ladder);

    /// <summary>Name of the model, used in logs.</summary>
    string Name { get; }
}

/// <summary>v1 model: fixed percentage offsets taken from the configuration.</summary>
public sealed class FixedPercentSpacingModel : ILadderSpacingModel
{
    /// <inheritdoc />
    public string Name => "FixedPercent";

    /// <inheritdoc />
    public IReadOnlyList<decimal> GetOffsets(LadderOptions ladder)
        => ladder.Steps.Select(s => s.OffsetPercent).ToList();
}

/// <summary>
/// v2 placeholder: spacing derived from ATR multiples. The strategy depends only on
/// <see cref="ILadderSpacingModel"/>, so adding this model requires no change elsewhere.
/// </summary>
public sealed class AtrSpacingModel : ILadderSpacingModel
{
    /// <inheritdoc />
    public string Name => "Atr";

    /// <inheritdoc />
    public IReadOnlyList<decimal> GetOffsets(LadderOptions ladder)
        => throw new NotSupportedException(
            "The ATR spacing model is reserved for v2. Configure Ladder:SpacingModel = FixedPercent.");
}

/// <summary>
/// Computes the resting buy ladder from the fair price and the market budget.
/// </summary>
/// <remarks>
/// Guarantees:
/// <list type="bullet">
///   <item><description>Σ allocation ≤ 100 % of the deployed budget (ReservePercent is never touched).</description></item>
///   <item><description>Every order respects tick size, step size, min quantity and min notional.</description></item>
///   <item><description>No order exceeds Risk:MaxOrderSizeUsdt or MarketRules:MaxQuantity.</description></item>
///   <item><description>Prices are rounded DOWN and quantities DOWN — rounding can never make an order more aggressive.</description></item>
/// </list>
/// </remarks>
public sealed class LadderCalculator
{
    private readonly ILadderSpacingModel _spacing;

    /// <summary>Creates the calculator with the given spacing model.</summary>
    public LadderCalculator(ILadderSpacingModel spacing) => _spacing = spacing;

    /// <summary>Computes the ladder for a market.</summary>
    /// <param name="marketKey">Configuration key of the market (used for logging).</param>
    /// <param name="market">Market configuration.</param>
    /// <param name="rules">Exchange trading rules.</param>
    /// <param name="fairPrice">Fair price anchor.</param>
    /// <param name="maxOrderSizeQuote">Maximum notional per order (risk limit).</param>
    /// <returns>The computed levels, possibly empty when the budget is too small.</returns>
    public IReadOnlyList<LadderLevel> Compute(
        string marketKey,
        MarketOptions market,
        MarketRules rules,
        decimal fairPrice,
        decimal maxOrderSizeQuote)
    {
        ArgumentNullException.ThrowIfNull(market);
        ArgumentNullException.ThrowIfNull(rules);

        if (fairPrice <= 0)
        {
            return Array.Empty<LadderLevel>();
        }

        var offsets = _spacing.GetOffsets(market.Ladder);

        if (offsets.Count != market.Ladder.Steps.Count)
        {
            throw new InvalidOperationException(
                $"Spacing model '{_spacing.Name}' returned {offsets.Count} offsets for {market.Ladder.Steps.Count} steps.");
        }

        // Reserve is never deployed; allocations are applied to what remains.
        var deployedBudget = market.BudgetUsdt * (1m - market.ReservePercent / 100m);
        var levels = new List<LadderLevel>(offsets.Count);

        for (var i = 0; i < offsets.Count; i++)
        {
            var step = market.Ladder.Steps[i];
            var offset = offsets[i];

            // Price: fair price minus the offset, rounded DOWN to the tick grid.
            var rawPrice = fairPrice * (1m - offset / 100m);
            var limitPrice = rules.NormalizePrice(rawPrice);

            if (limitPrice <= 0)
            {
                continue;
            }

            // Budget: allocation share of the deployed budget, capped by the risk limit.
            var rawValue = deployedBudget * (step.AllocationPercent / 100m);
            var orderValue = Math.Min(rawValue, maxOrderSizeQuote);

            // Quantity: value / price, rounded DOWN to the step grid.
            var quantity = rules.NormalizeQuantity(orderValue / limitPrice);

            // Skip orders that cannot satisfy the exchange minimums.
            if (!rules.MeetsMinimums(limitPrice, quantity))
            {
                continue;
            }

            levels.Add(new LadderLevel(
                StepIndex: i,
                OffsetPercent: offset,
                LimitPrice: limitPrice,
                AllocationPercent: step.AllocationPercent,
                OrderValue: limitPrice * quantity,
                Quantity: quantity));
        }

        return levels;
    }
}