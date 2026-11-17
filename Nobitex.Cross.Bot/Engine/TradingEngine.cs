using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.Execution;
using Nobitex.Cross.Bot.ExchangeAdapters;
using Nobitex.Cross.Bot.FairPrice;
using Nobitex.Cross.Bot.MarketData;
using Nobitex.Cross.Bot.PositionManagement;
using Nobitex.Cross.Bot.RiskManagement;
using Nobitex.Cross.Bot.Strategy;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.Engine;

/// <summary>
/// The main trading loop. Each tick it:
/// <list type="number">
///   <item><description>reads the local order book and the Binance reference price,</description></item>
///   <item><description>computes the fair price and the local basis,</description></item>
///   <item><description>evaluates the Binance price guard and the risk limits,</description></item>
///   <item><description>ingests confirmed fills into positions,</description></item>
///   <item><description>places/repairs the passive buy ladder and the take-profit orders.</description></item>
/// </list>
/// </summary>
public sealed class TradingEngine
{
    private readonly IReadOnlyDictionary<string, MarketOptions> _markets;
    private readonly IExchangeAdapter _adapter;
    private readonly IReferencePriceSource _reference;
    private readonly IFairPriceEngine _fairPrice;
    private readonly LocalBasisCalculator _basis;
    private readonly EntryEngine _entry;
    private readonly ExitEngine _exit;
    private readonly OrderManager _orders;
    private readonly ExecutionEngine _execution;
    private readonly PositionManager _positions;
    private readonly RiskManager _risk;
    private readonly BinancePriceGuard _guard;
    private readonly ReconciliationService _reconciliation;
    private readonly BotOptions _options;
    private readonly ILogger<TradingEngine> _logger;

    /// <summary>Creates the engine.</summary>
    public TradingEngine(
        BotOptions options,
        IReadOnlyDictionary<string, MarketOptions> markets,
        IExchangeAdapter adapter,
        IReferencePriceSource reference,
        IFairPriceEngine fairPrice,
        LocalBasisCalculator basis,
        EntryEngine entry,
        ExitEngine exit,
        OrderManager orders,
        ExecutionEngine execution,
        PositionManager positions,
        RiskManager risk,
        BinancePriceGuard guard,
        ReconciliationService reconciliation,
        ILogger<TradingEngine> logger)
    {
        _options = options;
        _markets = markets;
        _adapter = adapter;
        _reference = reference;
        _fairPrice = fairPrice;
        _basis = basis;
        _entry = entry;
        _exit = exit;
        _orders = orders;
        _execution = execution;
        _positions = positions;
        _risk = risk;
        _guard = guard;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the exchange state has been reconciled.
    /// No order is placed while this is <c>false</c>.
    /// </summary>
    public bool IsReconciled { get; private set; }

    /// <summary>Runs the engine until the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Startup reconciliation — the bot must know the truth before it acts.
        IsReconciled = await _reconciliation.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        if (!IsReconciled)
        {
            _logger.LogWarning("Trading is blocked until reconciliation succeeds — running in observation mode");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var (symbol, market) in _markets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await TickAsync(symbol, market, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _guard.ReportFailure();
                _logger.LogError(ex, "Unhandled error in the engine tick");
            }

            try
            {
                await Task.Delay(_options.Execution.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TickAsync(string symbol, MarketOptions market, CancellationToken cancellationToken)
    {
        if (!market.Enabled)
        {
            return;
        }

        // ---- 1) market data -------------------------------------------------
        var localBook = await _adapter.GetOrderBookAsync(symbol, depth: 50, cancellationToken).ConfigureAwait(false);
        var reference = await _reference.GetPriceAsync(_options.FairPrice.ReferenceSymbol, cancellationToken).ConfigureAwait(false);

        // ---- 2) fair price --------------------------------------------------
        var fair = _fairPrice.Compute(symbol, reference, localBook);

        if (fair.IsStale)
        {
            _logger.LogWarning("{Symbol}: market data is stale — skipping tick", symbol);
            return;
        }

        var snapshot = new MarketSnapshot(symbol, localBook, reference, fair.FairPrice, fair.LocalBasis, DateTimeOffset.UtcNow);

        // ---- 3) guard + risk ------------------------------------------------
        var verdict = _guard.Evaluate(reference, fair.FairPrice, adapterHealthy: true);

        if (verdict.State == GuardState.Tripped)
        {
            _basis.Freeze();
            await _orders.CancelAllAsync(_adapter, symbol, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("{Symbol}: guard tripped ({Reason}) — resting buys cancelled", symbol, verdict.Reason);
            return;
        }

        if (verdict.State == GuardState.Normal)
        {
            _basis.Unfreeze();
        }

        if (_risk.GetBudget(symbol).Allocated == 0)
        {
            _risk.GetBudget(symbol).Allocated = market.BudgetUsdt;
        }

        // ---- 4) fills -> positions -----------------------------------------
        var fills = await _adapter.GetFillsAsync(symbol, DateTimeOffset.UtcNow.AddHours(-6), cancellationToken).ConfigureAwait(false);
        _positions.ApplyFills(symbol, fills, fair.BinanceMidPrice, market.Fee);

        // ---- 5) exits first (never let a profit turn into a loss) -----------
        await ManageExitsAsync(symbol, market, cancellationToken).ConfigureAwait(false);

        // ---- 6) entries -----------------------------------------------------
        await ManageEntriesAsync(symbol, market, snapshot, verdict.State == GuardState.Tripped, cancellationToken).ConfigureAwait(false);

        await _orders.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ManageEntriesAsync(
        string symbol, MarketOptions market, MarketSnapshot snapshot, bool guardTripped, CancellationToken cancellationToken)
    {
        if (!IsReconciled || guardTripped)
        {
            return;
        }

        var rules = await _adapter.GetMarketRulesAsync(symbol, cancellationToken).ConfigureAwait(false);
        var plans = _entry.BuildPlans(
            symbol,
            market,
            rules,
            snapshot,
            _options.Risk.MaxOrderSizeUsdt,
            _options.Risk.MaxOpenOrders);

        foreach (var plan in plans)
        {
            var decision = _risk.EvaluateEntry(
                symbol,
                market,
                _positions,
                _orders.ActiveOrderCount,
                plan.LimitPrice * plan.Quantity,
                guardTripped);

            if (!decision.Allowed)
            {
                _logger.LogDebug("{Symbol}: entry blocked [{Reason}] {Message}", symbol, decision.Reason, decision.Message);
                continue;
            }

            // Re-quote policy: only touch steps whose price drifted beyond the band.
            var existing = _orders.Orders.FirstOrDefault(o => o.ClientOrderId == plan.ClientOrderId && o.IsActive);
            if (existing is not null)
            {
                if (_orders.ShouldRequote(existing, plan.LimitPrice, DateTimeOffset.UtcNow))
                {
                    await _orders.CancelAsync(_adapter, existing, cancellationToken).ConfigureAwait(false);
                    _risk.ReleaseBudget(symbol, existing.Price * existing.Quantity);
                    await _execution.ExecuteEntryAsync(plan, symbol, cancellationToken).ConfigureAwait(false);
                    _risk.ReserveBudget(symbol, plan.LimitPrice * plan.Quantity);
                }

                continue;
            }

            if (await _execution.ExecuteEntryAsync(plan, symbol, cancellationToken).ConfigureAwait(false))
            {
                _risk.ReserveBudget(symbol, plan.LimitPrice * plan.Quantity);
            }
        }
    }

    private async Task ManageExitsAsync(string symbol, MarketOptions market, CancellationToken cancellationToken)
    {
        var rules = await _adapter.GetMarketRulesAsync(symbol, cancellationToken).ConfigureAwait(false);

        foreach (var position in _positions.OpenPositions.Where(p => p.Symbol == symbol))
        {
            var plans = _exit.BuildPlans(position, market, rules);

            foreach (var plan in plans)
            {
                await _execution.ExecuteExitAsync(plan, symbol, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Engine shutting down (CancelAllOnExit={CancelAll})", _options.Execution.CancelAllOnExit);

        if (_options.Execution.CancelAllOnExit)
        {
            foreach (var symbol in _markets.Keys)
            {
                await _orders.CancelAllAsync(_adapter, symbol, cancellationToken).ConfigureAwait(false);
            }
        }

        await _orders.SaveAsync(cancellationToken).ConfigureAwait(false);
    }
}