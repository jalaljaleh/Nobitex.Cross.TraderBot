using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.PositionManagement;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.RiskManagement;

/// <summary>
/// Enforces every configured risk limit before an entry is placed. Each market is evaluated only
/// against its own budget and inventory — capital is never shared between markets.
/// </summary>
public sealed class RiskManager
{
    private readonly RiskOptions _risk;
    private readonly ILogger<RiskManager> _logger;
    private readonly ConcurrentBudget _budgets = new();

    /// <summary>Creates the manager.</summary>
    public RiskManager(RiskOptions risk, ILogger<RiskManager> logger)
    {
        _risk = risk;
        _logger = logger;
    }

    /// <summary>Budget ledger of a market (allocated, deployed, realized PnL).</summary>
    public MarketBudget GetBudget(string symbol) => _budgets.Get(symbol);

    /// <summary>
    /// Evaluates whether a new buy order may be placed for a market.
    /// </summary>
    /// <param name="symbol">Unified symbol.</param>
    /// <param name="market">Market configuration (own budget).</param>
    /// <param name="positions">Position manager (inventory + exposure of this market).</param>
    /// <param name="openOrderCount">Number of resting orders of this market.</param>
    /// <param name="orderValueQuote">Notional of the order being evaluated.</param>
    /// <param name="guardTripped">Whether the Binance price guard is currently tripped.</param>
    public RiskDecision EvaluateEntry(
        string symbol,
        MarketOptions market,
        PositionManager positions,
        int openOrderCount,
        decimal orderValueQuote,
        bool guardTripped)
    {
        if (_risk.EmergencyStop)
        {
            return RiskDecision.Block("EmergencyStop", "Emergency stop is active — no new entries.");
        }

        if (guardTripped)
        {
            return RiskDecision.Block("GuardTripped", "Binance price guard is tripped — no new entries.");
        }

        if (!market.Enabled)
        {
            return RiskDecision.Block("MarketDisabled", $"Market {symbol} is disabled in configuration.");
        }

        var budget = _budgets.Get(symbol);

        if (orderValueQuote > _risk.MaxOrderSizeUsdt)
        {
            return RiskDecision.Block("MaxOrderSize",
                $"Order value {orderValueQuote} exceeds MaxOrderSizeUsdt {_risk.MaxOrderSizeUsdt}.");
        }

        if (budget.Deployed + orderValueQuote > market.BudgetUsdt * (1m - market.ReservePercent / 100m))
        {
            return RiskDecision.Block("MarketBudgetExhausted",
                $"Market budget of {symbol} would be exceeded (deployed {budget.Deployed} + {orderValueQuote}).");
        }

        if (positions.GetExposure(symbol) + orderValueQuote > _risk.MaxExposureUsdt)
        {
            return RiskDecision.Block("MaxExposure",
                $"Exposure of {symbol} would exceed {_risk.MaxExposureUsdt}.");
        }

        if (positions.GetInventory(symbol) > _risk.MaxInventoryBase)
        {
            return RiskDecision.Block("MaxInventory",
                $"Inventory of {symbol} exceeds {_risk.MaxInventoryBase}.");
        }

        if (openOrderCount >= _risk.MaxOpenOrders)
        {
            return RiskDecision.Block("MaxOpenOrders",
                $"{openOrderCount} open orders already rest on {symbol}.");
        }

        var drawdownPercent = ComputeDrawdownPercent(symbol, market, positions);
        if (drawdownPercent > _risk.MaxDrawdownPercent)
        {
            return RiskDecision.Block("MaxDrawdown",
                $"Drawdown of {symbol} is {drawdownPercent:0.00}% (limit {_risk.MaxDrawdownPercent}%).");
        }

        return RiskDecision.Ok();
    }

    /// <summary>Registers a newly deployed amount against the market budget.</summary>
    public void ReserveBudget(string symbol, decimal valueQuote) => _budgets.Get(symbol).Deployed += valueQuote;

    /// <summary>Releases budget when an order is cancelled.</summary>
    public void ReleaseBudget(string symbol, decimal valueQuote) => _budgets.Get(symbol).Deployed -= valueQuote;

    /// <summary>Drawdown of the market budget in percent (realized losses only).</summary>
    public decimal ComputeDrawdownPercent(string symbol, MarketOptions market, PositionManager positions)
    {
        var realized = positions.GetRealizedProfit(symbol);

        if (realized >= 0 || market.BudgetUsdt <= 0)
        {
            return 0m;
        }

        return Math.Abs(realized) / market.BudgetUsdt * 100m;
    }

    /// <summary>Budget ledger of one market.</summary>
    public sealed class MarketBudget
    {
        /// <summary>Allocated budget (from configuration).</summary>
        public decimal Allocated { get; set; }

        /// <summary>Currently deployed quote asset (resting orders + inventory).</summary>
        public decimal Deployed { get; set; }

        /// <summary>Realized profit/loss of the market.</summary>
        public decimal RealizedPnl { get; set; }
    }

    private sealed class ConcurrentBudget
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MarketBudget> _map = new();

        public MarketBudget Get(string symbol) => _map.GetOrAdd(symbol, _ => new MarketBudget());
    }
}