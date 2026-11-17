using System.Collections.Concurrent;
using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.MarketData;
using Microsoft.Extensions.Logging;
using Nobitex.Net.Models;

namespace Nobitex.Cross.Bot.PositionManagement;

/// <summary>
/// Creates and maintains positions from <b>confirmed exchange fills</b>. It is the only component
/// that decides whether inventory exists; order acknowledgements never create a position.
/// </summary>
public sealed class PositionManager
{
    private readonly ConcurrentDictionary<string, Position> _positions = new();
    private readonly ProfitCalculator _profit;
    private readonly ILogger<PositionManager> _logger;

    /// <summary>Creates the manager.</summary>
    public PositionManager(ProfitCalculator profit, ILogger<PositionManager> logger)
    {
        _profit = profit;
        _logger = logger;
    }

    /// <summary>Every position known to the engine (open and closed).</summary>
    public IReadOnlyCollection<Position> Positions => _positions.Values.ToList();

    /// <summary>Positions that still hold inventory.</summary>
    public IReadOnlyList<Position> OpenPositions
        => _positions.Values.Where(p => p.Status != PositionStatus.Closed).ToList();

    /// <summary>Restores positions persisted by a previous run.</summary>
    public void Restore(IEnumerable<Position> positions)
    {
        foreach (var position in positions)
        {
            _positions[position.Id] = position;
        }

        _logger.LogInformation("Restored {Count} positions ({Open} open)", _positions.Count, OpenPositions.Count);
    }

    /// <summary>
    /// Ingests a batch of confirmed fills and updates the matching positions.
    /// Trade ids are tracked so a restart can never double count a fill.
    /// </summary>
    /// <param name="symbol">Unified symbol.</param>
    /// <param name="fills">Confirmed fills from the exchange.</param>
    /// <param name="referencePriceAtEntry">Reference (Binance) price used for the entry record.</param>
    /// <param name="fee">Fee parameters of the market.</param>
    public void ApplyFills(string symbol, IReadOnlyList<TradeFill> fills, decimal referencePriceAtEntry, FeeOptions fee)
    {
        foreach (var fill in fills.OrderBy(f => f.Timestamp))
        {
            var existing = _positions.Values.FirstOrDefault(p => p.ProcessedTradeIds.Contains(fill.TradeId));
            if (existing is not null)
            {
                continue;   // already accounted for
            }

            if (fill.Side == OrderSide.Buy)
            {
                ApplyBuy(symbol, fill, referencePriceAtEntry, fee);
            }
            else
            {
                ApplySell(symbol, fill, fee);
            }
        }
    }

    private void ApplyBuy(string symbol, TradeFill fill, decimal referencePriceAtEntry, FeeOptions fee)
    {
        var position = _positions.Values.FirstOrDefault(p =>
            p.Symbol == symbol &&
            p.OrderId == fill.OrderId &&
            p.Status != PositionStatus.Closed);

        if (position is null)
        {
            position = new Position
            {
                Symbol = symbol,
                OrderId = fill.OrderId,
                OpenedAt = fill.Timestamp,
                ReferencePriceAtEntry = referencePriceAtEntry,
            };
            _positions[position.Id] = position;
        }

        var previousValue = position.TotalCostQuote;
        var previousQuantity = position.OriginalQuantity;

        position.OriginalQuantity += fill.Quantity;
        position.RemainingQuantity += fill.Quantity;
        position.BuyFee += fill.Fee;
        position.TotalCostQuote = previousValue + fill.Price * fill.Quantity;

        // Average entry price is the weighted average of the executed prices.
        position.AverageEntryPrice = position.OriginalQuantity == 0
            ? 0m
            : position.TotalCostQuote / position.OriginalQuantity;

        position.ProcessedTradeIds.Add(fill.TradeId);

        _logger.LogInformation(
            "BUY fill {Qty} @ {Price} on {Symbol} -> position {PositionId} (avg entry {Avg}, qty {Total})",
            fill.Quantity, fill.Price, symbol, position.Id, position.AverageEntryPrice, position.OriginalQuantity);

        _ = previousQuantity;
    }

    private void ApplySell(string symbol, TradeFill fill, FeeOptions fee)
    {
        var position = _positions.Values
            .Where(p => p.Symbol == symbol && p.Status != PositionStatus.Closed && p.RemainingQuantity > 0)
            .OrderBy(p => p.OpenedAt)
            .FirstOrDefault();

        if (position is null)
        {
            _logger.LogWarning("Sell fill without an open position on {Symbol}: {Qty} @ {Price}",
                symbol, fill.Quantity, fill.Price);
            return;
        }

        var quantity = Math.Min(fill.Quantity, position.RemainingQuantity);

        // Normalize the fee to the quote asset: a base-asset fee is valued at the execution price.
        var feeQuote = fee.FeeAsset == FeeAsset.Base ? fill.Fee * fill.Price : fill.Fee;
        var profit = _profit.ComputeNetProfit(position, fee, fill.Price, quantity, feeQuote);

        position.RemainingQuantity -= quantity;
        position.SellFee += fill.Fee;
        position.RealizedProfitQuote += profit;
        position.ProcessedTradeIds.Add(fill.TradeId);

        if (position.RemainingQuantity <= 0)
        {
            position.RemainingQuantity = 0m;
            position.Status = PositionStatus.Closed;
        }
        else
        {
            position.Status = PositionStatus.Closing;
        }

        _logger.LogInformation(
            "SELL fill {Qty} @ {Price} on {Symbol} -> position {PositionId} net profit {Profit} ({ProfitPercent:0.000}%) remaining {Remaining}",
            quantity, fill.Price, symbol, position.Id, profit,
            _profit.ComputeNetProfitPercent(position, fee, fill.Price, quantity, feeQuote),
            position.RemainingQuantity);
    }

    /// <summary>Total base-asset inventory held across all open positions of a market.</summary>
    public decimal GetInventory(string symbol)
        => OpenPositions.Where(p => p.Symbol == symbol).Sum(p => p.RemainingQuantity);

    /// <summary>Total quote-asset exposure of a market at average entry prices.</summary>
    public decimal GetExposure(string symbol)
        => OpenPositions.Where(p => p.Symbol == symbol).Sum(p => p.RemainingValueQuote);

    /// <summary>Realized profit of a market.</summary>
    public decimal GetRealizedProfit(string symbol)
        => _positions.Values.Where(p => p.Symbol == symbol).Sum(p => p.RealizedProfitQuote);

    /// <summary>Flags a position for exit (used by the risk manager).</summary>
    public void MarkClosing(string positionId)
    {
        if (_positions.TryGetValue(positionId, out var position) && position.Status == PositionStatus.Open)
        {
            position.Status = PositionStatus.Closing;
        }
    }
}