using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.MarketData;
using Microsoft.Extensions.Logging;
using Nobitex.Net;
using Nobitex.Net.Authentication;
using Nobitex.Net.Models;
using NobitexSymbol = Nobitex.Net.Internal.NobitexSymbol;

namespace Nobitex.Cross.Bot.ExchangeAdapters;

/// <summary>
/// Production implementation of <see cref="IExchangeAdapter"/> built on the <b>Nobitex.Net</b> library.
/// This class is the only place that knows about Nobitex types, so the strategy engine can be
/// pointed at another exchange by swapping this adapter.
/// </summary>
/// <remarks>
/// <para>Verified members used (no invented APIs):</para>
/// <list type="bullet">
///   <item><description><c>MarketData.GetOrderBookAsync</c>, <c>GetMarketStatsAsync</c>, <c>IsMarketOpenAsync</c></description></item>
///   <item><description><c>Trading.PlaceLimitOrderAsync</c>, <c>GetOrderAsync</c>, <c>GetOpenOrdersAsync</c>, <c>CancelOrderAsync</c>, <c>GetMyTradesAsync</c></description></item>
///   <item><description><c>Wallet.GetBalanceAsync</c>, <c>GetWalletsAsync</c></description></item>
/// </list>
/// </remarks>
public sealed class NobitexAdapter : IExchangeAdapter, IMarketRulesProvider, IAsyncDisposable
{
    private readonly NobitexClient _client;
    private readonly ILogger<NobitexAdapter> _logger;
    private readonly IReadOnlyDictionary<string, MarketOptions> _markets;

    /// <summary>Creates the adapter from an already configured library client.</summary>
    public NobitexAdapter(NobitexClient client, IReadOnlyDictionary<string, MarketOptions> markets, ILogger<NobitexAdapter> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _markets = markets ?? throw new ArgumentNullException(nameof(markets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Creates the adapter using credentials taken from environment variables.</summary>
    public static NobitexAdapter Create(IReadOnlyDictionary<string, MarketOptions> markets, ILogger<NobitexAdapter> logger)
    {
        var options = new NobitexClientOptions
        {
            UserAgent = "Halun/Nobitex.Net",
            EnableClientRateLimiting = true,
        };

        // Preferred: API key pair (public + private base64), exactly the overload you are using.
        var publicKey = Environment.GetEnvironmentVariable("NOBITEX_API_KEY_PUBLIC");
        var privateKey = Environment.GetEnvironmentVariable("NOBITEX_API_KEY_SECRET");

        if (!string.IsNullOrWhiteSpace(publicKey) && !string.IsNullOrWhiteSpace(privateKey))
        {
            options.ApiKey = new NobitexApiKeyCredentials(publicKey, privateKey);
        }
        else
        {
            // Fallback: session token from the Nobitex dashboard.
            options.ApiToken = Environment.GetEnvironmentVariable("NOBITEX_API_TOKEN");
        }

        options.WebSocketAuthParam = Environment.GetEnvironmentVariable("NOBITEX_WS_AUTH_PARAM");

        return new NobitexAdapter(new NobitexClient(options), markets, logger);
    }

    /// <inheritdoc />
    public string ExchangeName => "Nobitex";

    /// <inheritdoc />
    public Task<MarketRules> GetMarketRulesAsync(string symbol, CancellationToken cancellationToken)
        => GetRulesAsync(symbol, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Nobitex does not publish a market-rules endpoint in the official documentation, therefore the
    /// rules come from configuration (see §2 open item #2). They are validated at startup against the
    /// live order book by <c>ReconciliationService</c>.
    /// </remarks>
    public Task<MarketRules> GetRulesAsync(string symbol, CancellationToken cancellationToken)
    {
        var market = GetMarket(symbol);
        var rules = market.MarketRules;

        return Task.FromResult(new MarketRules(
            symbol,
            rules.PriceTick,
            rules.QuantityStep,
            rules.MinQuantity,
            rules.MinNotional,
            rules.MaxQuantity));
    }

    /// <inheritdoc />
    public async Task<OrderBookSnapshot> GetOrderBookAsync(string symbol, int depth, CancellationToken cancellationToken)
    {
        var market = GetMarket(symbol);
        var symbolCode = NobitexSymbol.FromPair(market.Symbol.Base, market.Symbol.Quote);

        // Real library call: GET /v3/orderbook/{SYMBOL}
        var book = await _client.MarketData.GetOrderBookAsync(symbolCode, depth, cancellationToken).ConfigureAwait(false);

        return new OrderBookSnapshot
        {
            Exchange = ExchangeName,
            Symbol = symbol,
            Bids = book.Bids.Select(l => new PriceLevel(l.Price, l.Amount)).ToList(),
            Asks = book.Asks.Select(l => new PriceLevel(l.Price, l.Amount)).ToList(),
            Timestamp = book.LastUpdate == default ? DateTimeOffset.UtcNow : book.LastUpdate,
        };
    }

    /// <inheritdoc />
    public async Task<decimal> GetAvailableBalanceAsync(string asset, CancellationToken cancellationToken)
    {
        // Real library call: POST /users/wallets/balance
        var balance = await _client.Wallet.GetBalanceAsync(asset.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        return balance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OpenOrderInfo>> GetOpenOrdersAsync(string symbol, CancellationToken cancellationToken)
    {
        var market = GetMarket(symbol);

        // Real library call: GET /market/orders/list (status = open, details = 2)
        var response = await _client.Trading.GetOpenOrdersAsync(
            market.Symbol.Base.ToLowerInvariant(),
            market.Symbol.Quote.ToLowerInvariant(),
            cancellationToken).ConfigureAwait(false);

        var result = new List<OpenOrderInfo>(response.Orders.Count);

        foreach (var order in response.Orders)
        {
            result.Add(new OpenOrderInfo(
                OrderId: order.Id.ToString(),
                ClientOrderId: order.ClientOrderId,
                Symbol: symbol,
                Side: order.Type == OrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                Price: order.Price,
                FilledQuantity: order.MatchedAmount,
                OriginalQuantity: order.Amount,
                CreatedAt: order.CreatedAt));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<OrderSubmitResult> SubmitLimitOrderAsync(LimitOrderRequest request, CancellationToken cancellationToken)
    {
        var market = GetMarket(request.Symbol);

        try
        {
            // Real library call: POST /market/orders/add
            var order = await _client.Trading.PlaceLimitOrderAsync(
                request.Side == OrderSide.Buy ? Nobitex.Net.Models.OrderSide.Buy : Nobitex.Net.Models.OrderSide.Sell,
                market.Symbol.Base.ToLowerInvariant(),
                market.Symbol.Quote.ToLowerInvariant(),
                request.Quantity,
                request.Price,
                request.ClientOrderId,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Order accepted id={OrderId} clientOrderId={ClientOrderId} side={Side} price={Price} qty={Qty}",
                order.Id, request.ClientOrderId, request.Side, request.Price, request.Quantity);

            // Acceptance is NOT a fill — the caller must poll GetOrderStatusAsync / GetFillsAsync.
            return new OrderSubmitResult(order.Id.ToString(), request.ClientOrderId, Accepted: true);
        }
        catch (NobitexOrderRejectedException ex)
        {
            _logger.LogWarning("Order rejected code={Code} message={Message}", ex.ErrorCode, ex.ServerMessage);
            return new OrderSubmitResult(null, request.ClientOrderId, Accepted: false, ex.ErrorCode, ex.ServerMessage);
        }
        catch (NobitexRateLimitException ex)
        {
            _logger.LogWarning("Order throttled, backOff={BackOff}s", ex.BackOff.TotalSeconds);
            return new OrderSubmitResult(null, request.ClientOrderId, Accepted: false, "TooManyRequests", ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<OrderStatusResult> GetOrderStatusAsync(string orderId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(orderId, out var id))
        {
            throw new ArgumentException($"Order id '{orderId}' is not a valid Nobitex order id.", nameof(orderId));
        }

        // Real library call: POST /market/orders/status
        var order = await _client.Trading.GetOrderAsync(id, cancellationToken).ConfigureAwait(false);

        return new OrderStatusResult(
            OrderId: order.Id.ToString(),
            ClientOrderId: order.ClientOrderId,
            Status: MapStatus(order),
            FilledQuantity: order.MatchedAmount,
            RemainingQuantity: order.UnmatchedAmount,
            AveragePrice: order.AveragePrice,
            Fee: order.Fee,
            CreatedAt: order.CreatedAt);
    }

    /// <inheritdoc />
    public async Task CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(orderId, out var id))
        {
            throw new ArgumentException($"Order id '{orderId}' is not a valid Nobitex order id.", nameof(orderId));
        }

        // Real library call: POST /market/orders/update-status
        await _client.Trading.CancelOrderAsync(id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Order cancelled id={OrderId}", orderId);
    }

    /// <inheritdoc />
    public async Task CancelOrdersAsync(IReadOnlyCollection<string> orderIds, CancellationToken cancellationToken)
    {
        foreach (var orderId in orderIds)
        {
            try
            {
                await CancelOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Best effort: a shock must clear the book even when one cancel fails.
                _logger.LogError(ex, "Failed to cancel order id={OrderId}", orderId);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TradeFill>> GetFillsAsync(string symbol, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var market = GetMarket(symbol);

        // Real library call: GET /market/trades/list (user trades of the last 3 days)
        var response = await _client.Trading.GetMyTradesAsync(
            market.Symbol.Base.ToLowerInvariant(),
            market.Symbol.Quote.ToLowerInvariant(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var fills = new List<TradeFill>(response.Trades.Count);

        foreach (var trade in response.Trades)
        {
            if (trade.Timestamp < sinceUtc)
            {
                continue;
            }

            fills.Add(new TradeFill(
                OrderId: trade.OrderId.ToString(),
                TradeId: trade.Id.ToString(),
                Side: trade.Type == OrderSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                Price: trade.Price,
                Quantity: trade.Amount,
                Fee: trade.Fee,
                Timestamp: trade.Timestamp));
        }

        return fills;
    }

    /// <summary>Stops the underlying library client.</summary>
    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private MarketOptions GetMarket(string symbol)
        => _markets.TryGetValue(symbol, out var market)
            ? market
            : throw new InvalidOperationException($"Market '{symbol}' is not configured in Bot:Markets.");

    private static OrderLifecycleStatus MapStatus(Order order) => order.Status switch
    {
        Nobitex.Net.Models.OrderStatus.Done => OrderLifecycleStatus.Filled,
        Nobitex.Net.Models.OrderStatus.Canceled => OrderLifecycleStatus.Cancelled,
        Nobitex.Net.Models.OrderStatus.New => OrderLifecycleStatus.Open,
        Nobitex.Net.Models.OrderStatus.Inactive => OrderLifecycleStatus.Open,
        Nobitex.Net.Models.OrderStatus.Active => order.MatchedAmount > 0
            ? OrderLifecycleStatus.PartiallyFilled
            : OrderLifecycleStatus.Open,
        _ => OrderLifecycleStatus.Unknown,
    };
}