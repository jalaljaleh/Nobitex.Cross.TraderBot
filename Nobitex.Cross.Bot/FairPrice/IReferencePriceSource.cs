using Nobitex.Cross.Bot.MarketData;
using MarketFeed.Net.Core;
using MarketFeed.Net.Models;
using MarketFeed.Net.Providers;
using Microsoft.Extensions.Logging;
using MfOrderBookLevel = MarketFeed.Net.Models.OrderBookLevel;

namespace Nobitex.Cross.Bot.FairPrice;

/// <summary>
/// Source of the global reference price (Binance). Keeping this behind an interface means the
/// strategy can consume Binance data from any provider — including data you supply separately —
/// without touching the pricing logic.
/// </summary>
public interface IReferencePriceSource
{
    /// <summary>Exchange name used in logs and models.</summary>
    string ExchangeName { get; }

    /// <summary>Returns the current best bid/ask of the reference market.</summary>
    Task<MarketPrice> GetPriceAsync(string symbol, CancellationToken cancellationToken);

    /// <summary>Returns the reference order book (used for the weighted mid and spread metrics).</summary>
    Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken);
}

/// <summary>
/// Reference price source backed by <b>MarketFeed.Net</b>'s public Binance provider.
/// No API key is required and nothing is stored.
/// </summary>
public sealed class BinanceReferencePriceSource : IReferencePriceSource, IAsyncDisposable
{
    private readonly BinanceProvider _provider;
    private readonly ILogger<BinanceReferencePriceSource> _logger;

    /// <summary>Creates the source.</summary>
    public BinanceReferencePriceSource(ILogger<BinanceReferencePriceSource> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _provider = new BinanceProvider();
    }

    /// <inheritdoc />
    public string ExchangeName => "Binance";

    /// <inheritdoc />
    public async Task<MarketPrice> GetPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        // Real call: MarketFeed.Net → GET https://api.binance.com/api/v3/ticker/24hr
        var ticker = await _provider.GetTickerAsync(symbol, cancellationToken).ConfigureAwait(false);

        return new MarketPrice
        {
            Exchange = ExchangeName,
            Symbol = symbol,
            BidPrice = ticker.BidPrice,
            AskPrice = ticker.AskPrice,
            LastPrice = ticker.LastPrice,
            Timestamp = ticker.Timestamp == default ? DateTimeOffset.UtcNow : ticker.Timestamp,
        };
    }

    /// <inheritdoc />
    public async Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken)
    {
        // Real call: MarketFeed.Net → GET https://api.binance.com/api/v3/depth
        var book = await _provider.GetOrderBookAsync(symbol, depth, cancellationToken).ConfigureAwait(false);

        return new OrderBookSnapshot
        {
            Exchange = ExchangeName,
            Symbol = symbol,
            Bids = book.Bids.Select((MfOrderBookLevel l) => new PriceLevel(l.Price, l.Quantity)).ToList(),
            Asks = book.Asks.Select((MfOrderBookLevel l) => new PriceLevel(l.Price, l.Quantity)).ToList(),
            Timestamp = book.Timestamp == default ? DateTimeOffset.UtcNow : book.Timestamp,
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}