using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.MarketData;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Nobitex.Net.Models;

namespace Nobitex.Cross.Bot.ExchangeAdapters;

/// <summary>
/// Base class for adapters that never touch a real account: it keeps the market rules, a simulated
/// balance ledger and an in-memory order table shared by the DryRun and PaperTrading modes.
/// </summary>
public abstract class SimulatedExchangeAdapterBase : IExchangeAdapter
{
    private readonly ConcurrentDictionary<string, SimulatedOrder> _orders = new();
    private readonly ConcurrentDictionary<string, decimal> _balances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    /// <summary>Creates the simulated adapter.</summary>
    protected SimulatedExchangeAdapterBase(IReadOnlyDictionary<string, MarketRules> rules, ILogger logger)
    {
        Rules = rules;
        _logger = logger;
    }

    /// <summary>Trading rules of every simulated market.</summary>
    protected IReadOnlyDictionary<string, MarketRules> Rules { get; }

    /// <summary>Read-only view of the simulated order table (used by tests).</summary>
    public IReadOnlyCollection<SimulatedOrder> SimulatedOrders
    {
        get
        {
            return _orders.Values.ToList();
        }
    }

    /// <inheritdoc />
    public abstract string ExchangeName { get; }

    /// <summary>Sets the simulated balance of an asset.</summary>
    public void SetBalance(string asset, decimal amount) => _balances[asset.ToUpperInvariant()] = amount;

    /// <inheritdoc />
    public Task<MarketRules> GetMarketRulesAsync(string symbol, CancellationToken cancellationToken)
        => Task.FromResult(Rules[symbol]);

    /// <inheritdoc />
    public virtual Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken)
        => throw new NotSupportedException("Simulated adapters require an external order book feed (see PaperExchangeAdapter).");

    /// <inheritdoc />
    public Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken cancellationToken)
        => Task.FromResult(_balances.TryGetValue(asset.ToUpperInvariant(), out var value) ? value : 0m);

    /// <inheritdoc />
    public Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        var open = _orders.Values
            .Where(o => o.Symbol == symbol && o.Status is OrderLifecycleStatus.Open or OrderLifecycleStatus.PartiallyFilled)
            .Select(o => new OpenOrderInfo(o.OrderId, o.ClientOrderId, o.Symbol, o.Side, o.Price, o.FilledQuantity, o.OriginalQuantity, o.CreatedAt))
            .ToList();

        return Task.FromResult<IReadOnlyList<OpenOrderInfo>>(open);
    }

    /// <inheritdoc />
    public Task<OrderSubmitResult> SubmitLimitOrderAsync(LimitOrderRequest request, CancellationToken cancellationToken)
    {
        // Duplicate protection at the adapter level as well: identical clientOrderId is rejected.
        if (_orders.Values.Any(o => o.ClientOrderId == request.ClientOrderId))
        {
            return Task.FromResult(new OrderSubmitResult(null, request.ClientOrderId, false,
                "DuplicateClientOrderId", $"An order with clientOrderId '{request.ClientOrderId}' already exists."));
        }

        var rules = Rules[request.Symbol];

        if (!rules.MeetsMinimums(request.Price, request.Quantity))
        {
            return Task.FromResult(new OrderSubmitResult(null, request.ClientOrderId, false,
                "SmallOrder", "Order does not satisfy the minimum quantity / notional."));
        }

        var order = new SimulatedOrder(
           Guid.NewGuid().ToString("N")[..12],
            request.ClientOrderId,
             request.Symbol,
            request.Side,
            request.Price,
            request.Quantity,
            DateTimeOffset.UtcNow);

        _orders[order.OrderId] = order;
        _logger.LogInformation("[{Mode}] order accepted {OrderId} {Side} {Qty} @ {Price}",
            ExchangeName, order.OrderId, request.Side, request.Quantity, request.Price);

        return Task.FromResult(new OrderSubmitResult(order.OrderId, request.ClientOrderId, true));
    }

    /// <inheritdoc />
    public Task<OrderStatusResult> GetOrderStatusAsync(string orderId, CancellationToken cancellationToken)
    {
        if (!_orders.TryGetValue(orderId, out var order))
        {
            throw new InvalidOperationException($"Unknown order id '{orderId}'.");
        }

        return Task.FromResult(new OrderStatusResult(
            order.OrderId,
            order.ClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.OriginalQuantity - order.FilledQuantity,
            order.AveragePrice,
            order.Fee,
            order.CreatedAt));
    }

    /// <inheritdoc />
    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        if (_orders.TryGetValue(orderId, out var order) &&
            order.Status is OrderLifecycleStatus.Open or OrderLifecycleStatus.PartiallyFilled)
        {
            order.Status = OrderLifecycleStatus.Cancelled;
            _logger.LogInformation("[{Mode}] order cancelled {OrderId}", ExchangeName, orderId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelOrdersAsync(IReadOnlyCollection<string> orderIds, CancellationToken cancellationToken)
    {
        foreach (var orderId in orderIds)
        {
            _ = CancelOrderAsync(orderId, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TradeFill>> GetFillsAsync(string symbol, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var fills = _orders.Values
            .Where(o => o.Symbol == symbol)
            .SelectMany(o => o.Fills)
            .Where(f => f.Timestamp >= sinceUtc)
            .OrderBy(f => f.Timestamp)
            .ToList();

        return Task.FromResult<IReadOnlyList<TradeFill>>(fills);
    }

    /// <summary>Records a simulated fill and updates balances.</summary>
    protected void RecordFill(SimulatedOrder order, decimal price, decimal quantity, decimal fee, FeeAsset feeAsset)
    {
        var previousValue = order.AveragePrice * order.FilledQuantity;
        var newValue = price * quantity;
        var newFilled = order.FilledQuantity + quantity;

        order.AveragePrice = newFilled == 0 ? 0m : (previousValue + newValue) / newFilled;
        order.FilledQuantity = newFilled;
        order.Fee += fee;
        order.Fills.Add(new TradeFill(order.OrderId, $"{order.OrderId}-{order.Fills.Count}", order.Side, price, quantity, fee, DateTimeOffset.UtcNow));

        var baseAsset = order.Symbol.Split('/')[0];
        var quoteAsset = order.Symbol.Split('/')[1];

        if (order.Side == OrderSide.Buy)
        {
            // Buy: pay quote, receive base minus a base-denominated fee.
            AddBalance(quoteAsset, -(price * quantity));
            AddBalance(baseAsset, quantity - (feeAsset == FeeAsset.Base ? fee : 0m));
        }
        else
        {
            AddBalance(baseAsset, -quantity);
            AddBalance(quoteAsset, price * quantity - (feeAsset == FeeAsset.Quote ? fee : 0m));
        }

        if (order.FilledQuantity >= order.OriginalQuantity)
        {
            order.Status = OrderLifecycleStatus.Filled;
        }
        else
        {
            order.Status = OrderLifecycleStatus.PartiallyFilled;
        }
    }

    private void AddBalance(string asset, decimal delta)
        => _balances.AddOrUpdate(asset.ToUpperInvariant(), delta, (_, current) => current + delta);

    /// <summary>In-memory order used by the simulated adapters.</summary>
    public sealed class SimulatedOrder
    {
        /// <summary>Creates an order.</summary>
        public SimulatedOrder(string orderId, string clientOrderId, string symbol, OrderSide side,
            decimal price, decimal originalQuantity, DateTimeOffset createdAt)
        {
            OrderId = orderId;
            ClientOrderId = clientOrderId;
            Symbol = symbol;
            Side = side;
            Price = price;
            OriginalQuantity = originalQuantity;
            CreatedAt = createdAt;
        }

        /// <summary>Order id.</summary>
        public string OrderId { get; }

        /// <summary>Idempotency key.</summary>
        public string ClientOrderId { get; }

        /// <summary>Unified symbol.</summary>
        public string Symbol { get; }

        /// <summary>Order side.</summary>
        public OrderSide Side { get; }

        /// <summary>Limit price.</summary>
        public decimal Price { get; }

        /// <summary>Original quantity.</summary>
        public decimal OriginalQuantity { get; }

        /// <summary>Creation time (UTC).</summary>
        public DateTimeOffset CreatedAt { get; }

        /// <summary>Quantity executed so far.</summary>
        public decimal FilledQuantity { get; set; }

        /// <summary>Average execution price.</summary>
        public decimal AveragePrice { get; set; }

        /// <summary>Accumulated fee.</summary>
        public decimal Fee { get; set; }

        /// <summary>Current status.</summary>
        public OrderLifecycleStatus Status { get; set; } = OrderLifecycleStatus.Open;

        /// <summary>Real simulated fills of this order.</summary>
        public List<TradeFill> Fills { get; } = new();
    }
}

/// <summary>
/// DryRun adapter: computes every decision and logs it, but never submits anything to an exchange.
/// Order book data still arrives from the real feeds so decisions are meaningful.
/// </summary>
public sealed class DryRunExchangeAdapter : SimulatedExchangeAdapterBase
{
    private readonly Func<string, int, CancellationToken, Task<OrderBookSnapshot>> _bookSource;

    /// <summary>Creates the adapter.</summary>
    /// <param name="rules">Market rules per symbol.</param>
    /// <param name="bookSource">Delegate that provides the real (or simulated) order book.</param>
    /// <param name="logger">Logger.</param>
    public DryRunExchangeAdapter(
        IReadOnlyDictionary<string, MarketRules> rules,
        Func<string, int, CancellationToken, Task<OrderBookSnapshot>> bookSource,
        ILogger logger)
        : base(rules, logger) => _bookSource = bookSource;

    /// <inheritdoc />
    public override string ExchangeName => "DryRun";

    /// <inheritdoc />
    public override Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken)
        => _bookSource(symbol, depth, cancellationToken);
}

/// <summary>
/// PaperTrading adapter: submits orders into a simulated book and fills them when the real market
/// trades through the limit price (maker semantics — a resting order only fills when the market
/// price crosses it, never before).
/// </summary>
public sealed class PaperExchangeAdapter : SimulatedExchangeAdapterBase
{
    private readonly Func<string, int, CancellationToken, Task<OrderBookSnapshot>> _bookSource;
    private readonly FeeAsset _feeAsset;
    private readonly decimal _makerFeeRate;

    /// <summary>Creates the adapter.</summary>
    public PaperExchangeAdapter(
        IReadOnlyDictionary<string, MarketRules> rules,
        Func<string, int, CancellationToken, Task<OrderBookSnapshot>> bookSource,
        FeeAsset feeAsset,
        decimal makerFeeRate,
        ILogger logger)
        : base(rules, logger)
    {
        _bookSource = bookSource;
        _feeAsset = feeAsset;
        _makerFeeRate = makerFeeRate;
    }

    /// <inheritdoc />
    public override string ExchangeName => "PaperTrading";

    /// <inheritdoc />
    public override async Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken)
    {
        var book = await _bookSource(symbol, depth, cancellationToken).ConfigureAwait(false);
        await SimulateFillsAsync(symbol, book).ConfigureAwait(false);
        return book;
    }

    /// <summary>
    /// Applies maker semantics: a resting BUY fills when the book best ask falls to or below our
    /// limit price; a resting SELL fills when the best bid rises to or above our limit price.
    /// </summary>
    private Task SimulateFillsAsync(string symbol, OrderBookSnapshot book)
    {
        foreach (var order in SimulatedOrders)
        {
            if (order.Symbol != symbol || order.Status is not (OrderLifecycleStatus.Open or OrderLifecycleStatus.PartiallyFilled))
            {
                continue;
            }

            var marketPrice = order.Side == OrderSide.Buy ? book.BestAsk : book.BestBid;
            if (marketPrice is null)
            {
                continue;
            }

            var crosses = order.Side == OrderSide.Buy
                ? marketPrice.Value <= order.Price
                : marketPrice.Value >= order.Price;

            if (!crosses)
            {
                continue;
            }

            // Fill at our own limit price (passive execution price), never at the crossing price.
            var remaining = order.OriginalQuantity - order.FilledQuantity;
            var fee = order.Price * remaining * _makerFeeRate;
            RecordFill(order, order.Price, remaining, fee, _feeAsset);
        }

        return Task.CompletedTask;
    }
}