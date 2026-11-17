using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.MarketData;
using Nobitex.Cross.Bot.Strategy;
using Microsoft.Extensions.Logging;
using Nobitex.Net.Models;

namespace Nobitex.Cross.Bot.Execution;

/// <summary>
/// Executes entry and exit plans through <see cref="IExchangeAdapter"/>. In DryRun mode every plan
/// is only logged, which makes the strategy observable without ever sending an order.
/// </summary>
public sealed class ExecutionEngine
{
    private readonly IExchangeAdapter _adapter;
    private readonly OrderManager _orders;
    private readonly RunMode _mode;
    private readonly ILogger<ExecutionEngine> _logger;

    /// <summary>Creates the engine.</summary>
    public ExecutionEngine(IExchangeAdapter adapter, OrderManager orders, RunMode mode, ILogger<ExecutionEngine> logger)
    {
        _adapter = adapter;
        _orders = orders;
        _mode = mode;
        _logger = logger;
    }

    /// <summary>Submits (or reports) an entry plan.</summary>
    /// <returns><c>true</c> when the order exists on the exchange (or would exist in DryRun).</returns>
    public async Task<bool> ExecuteEntryAsync(EntryPlan plan, string symbol, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_orders.TryBeginSubmit(plan.ClientOrderId, now))
        {
            return false;
        }

        if (_mode == RunMode.DryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] would place BUY {Qty} @ {Price} on {Symbol} (step {Step}, clientOrderId {ClientId})",
                plan.Quantity, plan.LimitPrice, symbol, plan.StepIndex, plan.ClientOrderId);
            return true;
        }

        var result = await _adapter.SubmitLimitOrderAsync(
            new LimitOrderRequest(symbol, OrderSide.Buy, plan.LimitPrice, plan.Quantity, plan.ClientOrderId),
            cancellationToken).ConfigureAwait(false);

        if (!result.Accepted || result.OrderId is null)
        {
            _orders.RegisterRejected(plan.ClientOrderId, now);
            _logger.LogWarning("Entry rejected step={Step} code={Code} message={Message}",
                plan.StepIndex, result.RejectCode, result.RejectMessage);
            return false;
        }

        _orders.RegisterAccepted(plan.ClientOrderId, result.OrderId, symbol, plan.StepIndex,
            OrderSide.Buy, plan.LimitPrice, plan.Quantity, now);
        return true;
    }

    /// <summary>Submits (or reports) an exit plan.</summary>
    public async Task<bool> ExecuteExitAsync(ExitPlan plan, string symbol, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_orders.TryBeginSubmit(plan.ClientOrderId, now))
        {
            return false;
        }

        if (_mode == RunMode.DryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] would place SELL {Qty} @ {Price} on {Symbol} (position {Position}, stage {Stage}, reason {Reason})",
                plan.Quantity, plan.LimitPrice, symbol, plan.PositionId, plan.StageIndex, plan.Reason);
            return true;
        }

        var result = await _adapter.SubmitLimitOrderAsync(
            new LimitOrderRequest(symbol, OrderSide.Sell, plan.LimitPrice, plan.Quantity, plan.ClientOrderId),
            cancellationToken).ConfigureAwait(false);

        if (!result.Accepted || result.OrderId is null)
        {
            _orders.RegisterRejected(plan.ClientOrderId, now);
            _logger.LogWarning("Exit rejected position={Position} code={Code} message={Message}",
                plan.PositionId, result.RejectCode, result.RejectMessage);
            return false;
        }

        _orders.RegisterAccepted(plan.ClientOrderId, result.OrderId, symbol, -1,
            OrderSide.Sell, plan.LimitPrice, plan.Quantity, now);
        return true;
    }
}