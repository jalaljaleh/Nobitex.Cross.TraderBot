using System.Collections.Concurrent;
using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.MarketData;
using Nobitex.Cross.Bot.Persistence;
using Microsoft.Extensions.Logging;
using Nobitex.Net.Models;

namespace Nobitex.Cross.Bot.Execution;

/// <summary>
/// A locally tracked order. The instance is the engine's view of an order that either exists on the
/// exchange or is about to be created; it is persisted so state survives a restart.
/// </summary>
public sealed class TrackedOrder
{
    /// <summary>Idempotency key (client order id) — unique per market and step.</summary>
    public string ClientOrderId { get; set; } = string.Empty;

    /// <summary>Exchange order id, assigned after acceptance.</summary>
    public string? OrderId { get; set; }

    /// <summary>Unified market symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Ladder step index (or -1 for exit orders).</summary>
    public int StepIndex { get; set; }

    /// <summary>Order side.</summary>
    public OrderSide Side { get; set; }

    /// <summary>Limit price at which the order was placed.</summary>
    public decimal Price { get; set; }

    /// <summary>Original quantity.</summary>
    public decimal Quantity { get; set; }

    /// <summary>Quantity confirmed as filled by the exchange.</summary>
    public decimal FilledQuantity { get; set; }

    /// <summary>Fee accumulated on this order.</summary>
    public decimal Fee { get; set; }

    /// <summary>Current lifecycle status.</summary>
    public OrderLifecycleStatus Status { get; set; } = OrderLifecycleStatus.Open;

    /// <summary>UTC time the order was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC time of the last submit/cancel action (drives the cooldown).</summary>
    public DateTimeOffset LastActionAt { get; set; }

    /// <summary>UTC time the drift was first observed (drives the re-quote confirmation).</summary>
    public DateTimeOffset? DriftDetectedAt { get; set; }

    /// <summary>Returns <c>true</c> when the order is still resting on the exchange.</summary>
    public bool IsActive => Status is OrderLifecycleStatus.Open or OrderLifecycleStatus.PartiallyFilled;
}

/// <summary>
/// Owns the complete order lifecycle of one bot instance: idempotent submission, duplicate
/// prevention, cooldowns, drift-based re-quoting and persistence.
/// </summary>
/// <remarks>
/// The manager never decides <i>what</i> to trade — that is the strategy's job. It only guarantees
/// that the exchange ends up with exactly the set of orders the strategy asked for, without
/// duplicates and without churn.
/// </remarks>
public sealed class OrderManager
{
    private readonly ConcurrentDictionary<string, TrackedOrder> _orders = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentIntents = new();
    private readonly IStateStore _stateStore;
    private readonly ExecutionOptions _execution;
    private readonly ILogger<OrderManager> _logger;

    /// <summary>Creates the manager.</summary>
    public OrderManager(IStateStore stateStore, ExecutionOptions execution, ILogger<OrderManager> logger)
    {
        _stateStore = stateStore;
        _execution = execution;
        _logger = logger;
    }

    /// <summary>All locally tracked orders.</summary>
    public IReadOnlyCollection<TrackedOrder> Orders => _orders.Values.ToList();

    /// <summary>Number of orders currently resting on the exchange.</summary>
    public int ActiveOrderCount => _orders.Values.Count(o => o.IsActive);

    /// <summary>Registers an order created during a previous run (used by reconciliation).</summary>
    public void Restore(IEnumerable<TrackedOrder> orders)
    {
        foreach (var order in orders)
        {
            _orders[order.ClientOrderId] = order;
        }

        _logger.LogInformation("Restored {Count} tracked orders from persistent state", _orders.Count);
    }

    /// <summary>
    /// Returns <c>true</c> when the given idempotency key has not been used recently — the caller
    /// may then submit the order. A second call with the same key inside
    /// <see cref="ExecutionOptions.DuplicateOrderWindow"/> is rejected.
    /// </summary>
    public bool TryBeginSubmit(string clientOrderId, DateTimeOffset nowUtc)
    {
        if (_orders.ContainsKey(clientOrderId))
        {
            _logger.LogWarning("Duplicate order suppressed: {ClientOrderId} is already tracked", clientOrderId);
            return false;
        }

        if (_recentIntents.TryGetValue(clientOrderId, out var last) &&
            nowUtc - last < _execution.DuplicateOrderWindow)
        {
            _logger.LogWarning("Duplicate order suppressed: {ClientOrderId} was attempted {Seconds}s ago",
                clientOrderId, (nowUtc - last).TotalSeconds);
            return false;
        }

        _recentIntents[clientOrderId] = nowUtc;
        return true;
    }

    /// <summary>Registers an accepted order.</summary>
    public TrackedOrder RegisterAccepted(string clientOrderId, string orderId, string symbol, int stepIndex,
        OrderSide side, decimal price, decimal quantity, DateTimeOffset nowUtc)
    {
        var tracked = new TrackedOrder
        {
            ClientOrderId = clientOrderId,
            OrderId = orderId,
            Symbol = symbol,
            StepIndex = stepIndex,
            Side = side,
            Price = price,
            Quantity = quantity,
            Status = OrderLifecycleStatus.Open,
            CreatedAt = nowUtc,
            LastActionAt = nowUtc,
        };

        _orders[clientOrderId] = tracked;
        return tracked;
    }

    /// <summary>Registers a rejected submission so the same key cannot be retried instantly.</summary>
    public void RegisterRejected(string clientOrderId, DateTimeOffset nowUtc)
        => _recentIntents[clientOrderId] = nowUtc;

    /// <summary>Applies a fresh exchange status to a tracked order.</summary>
    public void ApplyStatus(string clientOrderId, OrderStatusResult status)
    {
        if (!_orders.TryGetValue(clientOrderId, out var order))
        {
            return;
        }

        order.FilledQuantity = status.FilledQuantity;
        order.Fee = status.Fee;
        order.Status = status.Status;

        if (status.Status == OrderLifecycleStatus.Filled || status.Status == OrderLifecycleStatus.Cancelled)
        {
            order.DriftDetectedAt = null;
        }
    }

    /// <summary>
    /// Decides whether a resting order should be replaced because its price drifted too far from
    /// the current target. The drift must persist for <see cref="ExecutionOptions.RequoteConfirm"/>
    /// and the per-step cooldown must have elapsed — this protects the time priority of the order.
    /// </summary>
    public bool ShouldRequote(TrackedOrder order, decimal targetPrice, DateTimeOffset nowUtc)
    {
        if (!order.IsActive)
        {
            return false;
        }

        var drift = order.Price == 0 ? 0m : Math.Abs(order.Price - targetPrice) / order.Price * 100m;

        if (drift <= _execution.RequoteBandPercent)
        {
            order.DriftDetectedAt = null;
            return false;
        }

        order.DriftDetectedAt ??= nowUtc;

        if (nowUtc - order.DriftDetectedAt.Value < _execution.RequoteConfirm)
        {
            return false;
        }

        return nowUtc - order.LastActionAt >= _execution.RequoteCooldown;
    }

    /// <summary>Cancels an order and records the action time (starts the cooldown).</summary>
    public async Task CancelAsync(IExchangeAdapter adapter, TrackedOrder order, CancellationToken cancellationToken)
    {
        if (order.OrderId is null)
        {
            return;
        }

        await adapter.CancelOrderAsync(order.OrderId, cancellationToken).ConfigureAwait(false);

        order.Status = OrderLifecycleStatus.Cancelled;
        order.LastActionAt = DateTimeOffset.UtcNow;
        order.DriftDetectedAt = null;

        _logger.LogInformation("Order cancelled {ClientOrderId} ({OrderId})", order.ClientOrderId, order.OrderId);
    }

    /// <summary>Cancels every resting order (shock / shutdown / emergency).</summary>
    public async Task CancelAllAsync(IExchangeAdapter adapter, string symbol, CancellationToken cancellationToken)
    {
        var active = _orders.Values.Where(o => o.IsActive && o.Symbol == symbol && o.OrderId is not null).ToList();
        if (active.Count == 0)
        {
            return;
        }

        _logger.LogWarning("Cancelling {Count} resting orders on {Symbol}", active.Count, symbol);
        await adapter.CancelOrdersAsync(active.Select(o => o.OrderId!).ToList(), cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var order in active)
        {
            order.Status = OrderLifecycleStatus.Cancelled;
            order.LastActionAt = now;
            order.DriftDetectedAt = null;
        }
    }

    /// <summary>Checks whether an order is older than <see cref="ExecutionOptions.MaxOrderAge"/>.</summary>
    public bool IsExpired(TrackedOrder order, DateTimeOffset nowUtc)
        => order.IsActive && nowUtc - order.CreatedAt > _execution.MaxOrderAge;

    /// <summary>Serialises the current order table into the persistent state store.</summary>
    public Task SaveAsync(CancellationToken cancellationToken)
        => _stateStore.SaveOrdersAsync(_orders.Values.ToList(), cancellationToken);
}