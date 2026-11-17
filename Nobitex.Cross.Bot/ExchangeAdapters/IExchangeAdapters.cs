using Nobitex.Cross.Bot.MarketData;

namespace Nobitex.Cross.Bot.ExchangeAdapters;

/// <summary>
/// Exchange agnostic contract used by the strategy engine. The only production implementation is
/// <see cref="NobitexAdapter"/>; tests and paper trading use the simulated implementations.
/// Every member is async, accepts a <see cref="CancellationToken"/> and never blocks a thread.
/// </summary>
public interface IExchangeAdapter
{
    /// <summary>Display name of the exchange, e.g. <c>Nobitex</c>.</summary>
    string ExchangeName { get; }

    /// <summary>Returns the trading rules of a market (tick, step, min notional, ...).</summary>
    Task<MarketRules> GetMarketRulesAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>Returns the order book of a market.</summary>
    /// <param name="symbol">Unified symbol, e.g. <c>BTC/USDT</c>.</param>
    /// <param name="depth">Requested depth per side.</param>
    Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken);

    /// <summary>Returns the available (not blocked) balance of an asset.</summary>
    Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken cancellationToken);

    /// <summary>Returns the orders that are still resting on the exchange.</summary>
    Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>Submits a passive limit order. The result reflects acceptance only — never a fill.</summary>
    Task<OrderSubmitResult> SubmitLimitOrderAsync(LimitOrderRequest request, CancellationToken cancellationToken);

    /// <summary>Returns the current state of an order, including real filled quantity and fees.</summary>
    Task<OrderStatusResult> GetOrderStatusAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>Cancels a resting order.</summary>
    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken);

    /// <summary>Cancels several orders in one call; implementations should be best-effort.</summary>
    Task CancelOrdersAsync(IReadOnlyCollection<string> orderIds, CancellationToken cancellationToken);

    /// <summary>Returns the real fills of a market since the given UTC time (used as the fill source of truth).</summary>
    Task<IReadOnlyList<TradeFill>> GetFillsAsync(string symbol, DateTimeOffset sinceUtc, CancellationToken cancellationToken);
}

/// <summary>Trading rules of a market, used to normalize prices and quantities.</summary>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="PriceTick">Minimum price increment.</param>
/// <param name="QuantityStep">Minimum quantity increment.</param>
/// <param name="MinQuantity">Minimum order quantity.</param>
/// <param name="MinNotional">Minimum order value in the quote asset.</param>
/// <param name="MaxQuantity">Maximum order quantity.</param>
public sealed record MarketRules(
    string Symbol,
    decimal PriceTick,
    decimal QuantityStep,
    decimal MinQuantity,
    decimal MinNotional,
    decimal MaxQuantity)
{
    /// <summary>Rounds a price DOWN to the tick grid (safe for buy limits).</summary>
    public decimal NormalizePrice(decimal price)
    {
        if (PriceTick <= 0)
        {
            return price;
        }

        return Math.Floor(price / PriceTick) * PriceTick;
    }

    /// <summary>Rounds a price UP to the tick grid (safe for sell limits).</summary>
    public decimal NormalizePriceUp(decimal price)
    {
        if (PriceTick <= 0)
        {
            return price;
        }

        return Math.Ceiling(price / PriceTick) * PriceTick;
    }

    /// <summary>Rounds a quantity DOWN to the step grid and clamps it to the allowed range.</summary>
    public decimal NormalizeQuantity(decimal quantity)
    {
        if (QuantityStep <= 0)
        {
            return quantity;
        }

        var normalized = Math.Floor(quantity / QuantityStep) * QuantityStep;
        return Math.Min(normalized, MaxQuantity);
    }

    /// <summary>Returns <c>true</c> when the order satisfies min quantity and min notional.</summary>
    public bool MeetsMinimums(decimal price, decimal quantity)
        => quantity >= MinQuantity && price * quantity >= MinNotional;
}

/// <summary>Provides the trading rules of a market (isolates future API additions).</summary>
public interface IMarketRulesProvider
{
    /// <summary>Returns the rules of a market.</summary>
    Task<MarketRules> GetRulesAsync(string symbol, CancellationToken cancellationToken);
}