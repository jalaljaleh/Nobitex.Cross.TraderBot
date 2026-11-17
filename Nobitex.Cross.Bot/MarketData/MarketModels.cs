using Nobitex.Net.Models;

namespace Nobitex.Cross.Bot.MarketData;



/// <summary>Lifecycle state of an order as understood by the engine (exchange agnostic).</summary>
public enum OrderLifecycleStatus
{
    /// <summary>The exchange did not return a recognised state.</summary>
    Unknown,

    /// <summary>Accepted and resting, nothing filled yet.</summary>
    Open,

    /// <summary>Partially filled and still resting.</summary>
    PartiallyFilled,

    /// <summary>Completely filled.</summary>
    Filled,

    /// <summary>Cancelled (possibly after a partial fill).</summary>
    Cancelled,

    /// <summary>Rejected by the exchange or by a local guard.</summary>
    Rejected,
}

/// <summary>A single price level of an order book.</summary>
/// <param name="Price">Price of the level.</param>
/// <param name="Quantity">Quantity offered at that price.</param>
public readonly record struct PriceLevel(decimal Price, decimal Quantity);

/// <summary>
/// Immutable order book snapshot. Timestamps are UTC; every monetary value is <see cref="decimal"/>.
/// </summary>
public sealed record OrderBookSnapshot
{
    /// <summary>Exchange the snapshot came from.</summary>
    public required string Exchange { get; init; }

    /// <summary>Unified market symbol, e.g. <c>BTC/USDT</c>.</summary>
    public required string Symbol { get; init; }

    /// <summary>Bid levels, best first.</summary>
    public required IReadOnlyList<PriceLevel> Bids { get; init; }

    /// <summary>Ask levels, best first.</summary>
    public required IReadOnlyList<PriceLevel> Asks { get; init; }

    /// <summary>UTC timestamp of the snapshot.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Best bid price, or <c>null</c> when the book is empty.</summary>
    public decimal? BestBid => Bids.Count == 0 ? null : Bids[0].Price;

    /// <summary>Best ask price, or <c>null</c> when the book is empty.</summary>
    public decimal? BestAsk => Asks.Count == 0 ? null : Asks[0].Price;

    /// <summary>Mid price of the book.</summary>
    public decimal? MidPrice => BestBid is null || BestAsk is null ? null : (BestBid.Value + BestAsk.Value) / 2m;

    /// <summary>Absolute spread.</summary>
    public decimal Spread => BestBid is null || BestAsk is null ? 0m : BestAsk.Value - BestBid.Value;

    /// <summary>Relative spread in percent.</summary>
    public decimal SpreadPercent => BestBid is null or 0 ? 0m : Spread / BestBid.Value * 100m;

    /// <summary>Returns <c>true</c> when the snapshot is older than <paramref name="maxAge"/>.</summary>
    public bool IsStale(TimeSpan maxAge) => DateTimeOffset.UtcNow - Timestamp > maxAge;
}

/// <summary>Reference market price (used for the Binance side of the calculation).</summary>
public sealed record MarketPrice
{
    /// <summary>Exchange name.</summary>
    public required string Exchange { get; init; }

    /// <summary>Unified market symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Best bid.</summary>
    public required decimal BidPrice { get; init; }

    /// <summary>Best ask.</summary>
    public required decimal AskPrice { get; init; }

    /// <summary>Last traded price.</summary>
    public required decimal LastPrice { get; init; }

    /// <summary>UTC timestamp of the tick.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Mid price: (bid + ask) / 2.</summary>
    public decimal MidPrice => (BidPrice + AskPrice) / 2m;

    /// <summary>Relative spread in percent.</summary>
    public decimal SpreadPercent => BidPrice == 0 ? 0m : (AskPrice - BidPrice) / BidPrice * 100m;

    /// <summary>Returns <c>true</c> when the tick is older than <paramref name="maxAge"/>.</summary>
    public bool IsStale(TimeSpan maxAge) => DateTimeOffset.UtcNow - Timestamp > maxAge;
}

/// <summary>Complete picture of one market used by every decision.</summary>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="LocalBook">Nobitex order book.</param>
/// <param name="Reference">Binance reference price.</param>
/// <param name="FairPrice">Fair price computed by <c>FairPriceEngine</c>.</param>
/// <param name="LocalBasis">Basis applied to the reference price.</param>
/// <param name="ObservedAt">UTC time of the snapshot.</param>
public sealed record MarketSnapshot(
    string Symbol,
    OrderBookSnapshot LocalBook,
    MarketPrice Reference,
    decimal FairPrice,
    decimal LocalBasis,
    DateTimeOffset ObservedAt);

/// <summary>A real fill reported by the exchange.</summary>
/// <param name="OrderId">Id of the order that produced the fill.</param>
/// <param name="TradeId">Exchange trade id (unique, used for de-duplication).</param>
/// <param name="Side">Side of the order.</param>
/// <param name="Price">Execution price.</param>
/// <param name="Quantity">Executed quantity in base asset.</param>
/// <param name="Fee">Fee amount as reported by the exchange.</param>
/// <param name="Timestamp">UTC execution time.</param>
public sealed record TradeFill(
    string OrderId,
    string TradeId,
    OrderSide Side,
    decimal Price,
    decimal Quantity,
    decimal Fee,
    DateTimeOffset Timestamp);

/// <summary>Request to submit a passive limit order.</summary>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="Side">Order side.</param>
/// <param name="Price">Limit price (already normalized).</param>
/// <param name="Quantity">Quantity in base asset (already normalized).</param>
/// <param name="ClientOrderId">Idempotency key supplied by the engine.</param>
public sealed record LimitOrderRequest(
    string Symbol,
    OrderSide Side,
    decimal Price,
    decimal Quantity,
    string ClientOrderId);

/// <summary>Result of an order submission.</summary>
/// <param name="OrderId">Exchange order id, when accepted.</param>
/// <param name="ClientOrderId">Echo of the idempotency key.</param>
/// <param name="Accepted">Whether the exchange accepted the order.</param>
/// <param name="RejectCode">Machine readable reject reason.</param>
/// <param name="RejectMessage">Human readable reject reason.</param>
public sealed record OrderSubmitResult(
    string? OrderId,
    string ClientOrderId,
    bool Accepted,
    string? RejectCode = null,
    string? RejectMessage = null);

/// <summary>Order state as reported by the exchange, mapped to the engine vocabulary.</summary>
/// <param name="OrderId">Exchange order id.</param>
/// <param name="ClientOrderId">Idempotency key.</param>
/// <param name="Status">Lifecycle status.</param>
/// <param name="FilledQuantity">Quantity already executed.</param>
/// <param name="RemainingQuantity">Quantity still unfilled.</param>
/// <param name="AveragePrice">Average execution price.</param>
/// <param name="Fee">Accumulated fee.</param>
/// <param name="CreatedAt">UTC creation time.</param>
public sealed record OrderStatusResult(
    string OrderId,
    string? ClientOrderId,
    OrderLifecycleStatus Status,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal AveragePrice,
    decimal Fee,
    DateTimeOffset CreatedAt);

/// <summary>Minimal view of an order that is still resting on the exchange.</summary>
/// <param name="OrderId">Exchange order id.</param>
/// <param name="ClientOrderId">Idempotency key.</param>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="Side">Order side.</param>
/// <param name="Price">Limit price.</param>
/// <param name="FilledQuantity">Quantity already executed.</param>
/// <param name="OriginalQuantity">Original quantity.</param>
/// <param name="CreatedAt">UTC creation time.</param>
public sealed record OpenOrderInfo(
    string OrderId,
    string? ClientOrderId,
    string Symbol,
    OrderSide Side,
    decimal Price,
    decimal FilledQuantity,
    decimal OriginalQuantity,
    DateTimeOffset CreatedAt);