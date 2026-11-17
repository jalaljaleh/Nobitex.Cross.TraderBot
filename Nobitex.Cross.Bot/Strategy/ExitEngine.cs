using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.PositionManagement;
using Microsoft.Extensions.Logging;


namespace Nobitex.Cross.Bot.Strategy;

/// <summary>Plan for closing (fully or partially) a position.</summary>
/// <param name="PositionId">Local position identifier.</param>
/// <param name="StageIndex">Take-profit stage index.</param>
/// <param name="LimitPrice">Sell limit price.</param>
/// <param name="Quantity">Quantity to sell in this stage.</param>
/// <param name="ClientOrderId">Idempotency key.</param>
/// <param name="Reason">Why this exit was produced (for the audit log).</param>
public sealed record ExitPlan(
    string PositionId,
    int StageIndex,
    decimal LimitPrice,
    decimal Quantity,
    string ClientOrderId,
    string Reason);

/// <summary>
/// Builds sell plans for open positions. Prices come from <see cref="ProfitCalculator"/>, so every
/// target already includes buy fee, sell fee, expected slippage and the configured net profit.
/// </summary>
public sealed class ExitEngine
{
    private readonly ProfitCalculator _profit;
    private readonly ExecutionOptions _execution;
    private readonly ILogger<ExitEngine> _logger;

    /// <summary>Creates the engine.</summary>
    public ExitEngine(ProfitCalculator profit, ExecutionOptions execution, ILogger<ExitEngine> logger)
    {
        _profit = profit;
        _execution = execution;
        _logger = logger;
    }

    /// <summary>
    /// Produces the exit plans of a position. Returns an empty list when the position is not yet
    /// eligible for exit (no fill, already fully staged, or targets not reachable).
    /// </summary>
    public IReadOnlyList<ExitPlan> BuildPlans(Position position, MarketOptions market, MarketRules rules)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(market);

        if (position.Status == PositionStatus.Closed || position.RemainingQuantity <= 0)
        {
            return Array.Empty<ExitPlan>();
        }

        var plans = new List<ExitPlan>();

        // 1) Time-based exit: holding for too long forces a flat target (break-even + costs only).
        var holdingTime = DateTimeOffset.UtcNow - position.OpenedAt;
        var forceExit = holdingTime > market.Exit.MaxHoldingTime;

        // 2) Stage-based exits.
        for (var i = 0; i < market.Exit.TakeProfitStages.Count; i++)
        {
            var stage = market.Exit.TakeProfitStages[i];

            if (position.StagesCompleted >= i + 1)
            {
                continue;
            }

            var stageQuantity = rules.NormalizeQuantity(position.OriginalQuantity * stage.FillPercent / 100m);
            stageQuantity = Math.Min(stageQuantity, position.RemainingQuantity);

            if (stageQuantity <= 0 || !rules.MeetsMinimums(position.AverageEntryPrice, stageQuantity))
            {
                continue;
            }

            // Required price covers buy fee, sell fee, slippage allowance and the net target.
            var requiredPrice = _profit.GetRequiredSellPrice(
                position,
                market.Fee,
                targetNetProfitPercent: forceExit ? 0m : market.Exit.TargetNetProfitPercent + stage.PremiumPercent,
                slippageAllowancePercent: market.Exit.SlippageAllowancePercent);

            var limitPrice = rules.NormalizePriceUp(requiredPrice);

            plans.Add(new ExitPlan(
                position.Id,
                i,
                limitPrice,
                stageQuantity,
                $"ce-x-{position.Id}-{i}",
                forceExit ? "MaxHoldingTime" : $"TakeProfitStage{i + 1}"));
        }

        if (plans.Count == 0)
        {
            _logger.LogDebug("Position {PositionId}: no exit plan yet (avg entry {AvgEntry})",
                position.Id, position.AverageEntryPrice);
        }

        return plans;
    }

    /// <summary>
    /// Emergency exit price: best bid minus the configured slippage tolerance. Used only when
    /// <see cref="ExitOptions.EmergencyExit"/> is enabled and the mode is LiveTrading.
    /// </summary>
    public decimal GetEmergencyPrice(decimal bestBid, MarketOptions market, MarketRules rules)
        => rules.NormalizePrice(bestBid * (1m - market.Exit.MaxSlippagePercent / 100m));
}