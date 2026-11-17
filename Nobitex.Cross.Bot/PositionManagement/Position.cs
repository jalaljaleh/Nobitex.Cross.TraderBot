namespace Nobitex.Cross.Bot.PositionManagement;

/// <summary>State of a position.</summary>
public enum PositionStatus
{
    /// <summary>The position is accumulating (buy orders may still be filling).</summary>
    Open,

    /// <summary>Exit orders are resting.</summary>
    Closing,

    /// <summary>Everything that was bought has been sold.</summary>
    Closed,
}

/// <summary>
/// A real position created from confirmed exchange fills. Nothing here is derived from an order
/// acknowledgement — quantities, prices and fees all come from executed trades.
/// </summary>
public sealed class Position
{
    /// <summary>Local identifier (stable across restarts).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Unified market symbol, e.g. <c>BTC/USDT</c>.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Exchange order id that opened (or is opening) the position.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Idempotency key of the opening order.</summary>
    public string? ClientOrderId { get; set; }

    /// <summary>Average entry price including fee adjustment.</summary>
    public decimal AverageEntryPrice { get; set; }

    /// <summary>Total quantity bought.</summary>
    public decimal OriginalQuantity { get; set; }

    /// <summary>Quantity still held.</summary>
    public decimal RemainingQuantity { get; set; }

    /// <summary>Real buy fee paid so far.</summary>
    public decimal BuyFee { get; set; }

    /// <summary>Real sell fee paid so far.</summary>
    public decimal SellFee { get; set; }

    /// <summary>Total cost in the quote asset (price × quantity + quote fees).</summary>
    public decimal TotalCostQuote { get; set; }

    /// <summary>UTC time of the first fill.</summary>
    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Reference (Binance) price at the moment of the first fill.</summary>
    public decimal ReferencePriceAtEntry { get; set; }

    /// <summary>Current status.</summary>
    public PositionStatus Status { get; set; } = PositionStatus.Open;

    /// <summary>Number of take-profit stages already completed.</summary>
    public int StagesCompleted { get; set; }

    /// <summary>Realized profit in the quote asset.</summary>
    public decimal RealizedProfitQuote { get; set; }

    /// <summary>Trade ids already consumed (prevents double counting after a restart).</summary>
    public List<string> ProcessedTradeIds { get; set; } = new();

    /// <summary>Notional value of the remaining inventory at the average entry price.</summary>
    public decimal RemainingValueQuote => RemainingQuantity * AverageEntryPrice;
}

/// <summary>Entry/exit risk decision returned by <c>RiskManager</c>.</summary>
/// <param name="Allowed">Whether the action may proceed.</param>
/// <param name="Reason">Machine readable reason code.</param>
/// <param name="Message">Human readable explanation for the log.</param>
public sealed record RiskDecision(bool Allowed, string Reason, string Message)
{
    /// <summary>Shortcut for an allowed decision.</summary>
    public static RiskDecision Ok() => new(true, "Ok", string.Empty);

    /// <summary>Shortcut for a blocked decision.</summary>
    public static RiskDecision Block(string reason, string message) => new(false, reason, message);
}