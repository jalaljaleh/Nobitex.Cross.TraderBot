using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.Execution;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.MarketData;
using Nobitex.Cross.Bot.Persistence;
using Nobitex.Cross.Bot.PositionManagement;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.Engine;

/// <summary>
/// Reconciles the persisted local state with the real exchange state at startup.
/// Until reconciliation completes, no new order may be placed (see <see cref="TradingEngine.IsReconciled"/>).
/// </summary>
public sealed class ReconciliationService
{
    private readonly IExchangeAdapter _adapter;
    private readonly IStateStore _store;
    private readonly OrderManager _orders;
    private readonly PositionManager _positions;
    private readonly IReadOnlyDictionary<string, MarketOptions> _markets;
    private readonly ILogger<ReconciliationService> _logger;

    /// <summary>Creates the service.</summary>
    public ReconciliationService(
        IExchangeAdapter adapter,
        IStateStore store,
        OrderManager orders,
        PositionManager positions,
        IReadOnlyDictionary<string, MarketOptions> markets,
        ILogger<ReconciliationService> logger)
    {
        _adapter = adapter;
        _store = store;
        _orders = orders;
        _positions = positions;
        _markets = markets;
        _logger = logger;
    }

    /// <summary>
    /// Runs the full reconciliation:
    /// 1. load local state, 2. fetch real open orders, 3. fetch real balances,
    /// 4. fetch recent fills, 5. merge and report differences.
    /// </summary>
    /// <returns><c>true</c> when the local state is consistent with the exchange.</returns>
    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Reconciliation started");

        // 1) local state
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            _orders.Restore(state.Orders);
            _positions.Restore(state.Positions);
            _logger.LogInformation("Local state loaded: {Orders} orders, {Positions} positions (saved {SavedAt})",
                state.Orders.Count, state.Positions.Count, state.SavedAt);
        }

        var consistent = true;

        foreach (var (symbol, market) in _markets)
        {
            try
            {
                // 2) real open orders
                var openOrders = await _adapter.GetOpenOrdersAsync(symbol, cancellationToken).ConfigureAwait(false);

                var knownIds = _orders.Orders.Where(o => o.OrderId is not null).Select(o => o.OrderId).ToHashSet();
                var unknown = openOrders.Where(o => !knownIds.Contains(o.OrderId)).ToList();

                if (unknown.Count > 0)
                {
                    _logger.LogWarning("{Symbol}: {Count} orders exist on the exchange but not locally — adopting them",
                        symbol, unknown.Count);

                    foreach (var order in unknown)
                    {
                        _orders.RegisterAccepted(
                            order.ClientOrderId ?? $"restored-{order.OrderId}",
                            order.OrderId,
                            symbol,
                            stepIndex: -1,
                            order.Side,
                            order.Price,
                            order.OriginalQuantity,
                            order.CreatedAt);
                    }
                }

                // Orders tracked locally but no longer active on the exchange are marked accordingly.
                foreach (var tracked in _orders.Orders.Where(o => o.Symbol == symbol && o.IsActive))
                {
                    if (openOrders.All(o => o.OrderId != tracked.OrderId))
                    {
                        var status = await _adapter.GetOrderStatusAsync(tracked.OrderId!, cancellationToken).ConfigureAwait(false);
                        _orders.ApplyStatus(tracked.ClientOrderId, status);
                        _logger.LogInformation("Order {OrderId} resolved locally to {Status}", tracked.OrderId, status.Status);
                    }
                }

                // 3) real balances
                var baseBalance = await _adapter.GetAvailableBalanceAsync(market.Symbol.Base, cancellationToken).ConfigureAwait(false);
                var quoteBalance = await _adapter.GetAvailableBalanceAsync(market.Symbol.Quote, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("{Symbol}: exchange balances base={Base} quote={Quote}", symbol, baseBalance, quoteBalance);

                // 4) recent fills -> positions
                var since = DateTimeOffset.UtcNow.AddDays(-3);
                var fills = await _adapter.GetFillsAsync(symbol, since, cancellationToken).ConfigureAwait(false);
                _positions.ApplyFills(symbol, fills, referencePriceAtEntry: 0m, market.Fee);
                _logger.LogInformation("{Symbol}: processed {Count} recent fills", symbol, fills.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consistent = false;
                _logger.LogError(ex, "Reconciliation failed for {Symbol} — trading stays blocked", symbol);
            }
        }

        // 5) persist the merged state
        await _store.SaveAsync(new StoredState
        {
            Orders = _orders.Orders.ToList(),
            Positions = _positions.Positions.ToList(),
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Reconciliation finished: {Status}", consistent ? "CONSISTENT" : "INCONSISTENT");
        return consistent;
    }
}