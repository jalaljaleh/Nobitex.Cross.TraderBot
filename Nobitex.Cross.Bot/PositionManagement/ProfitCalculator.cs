using Nobitex.Cross.Bot.Configuration;

namespace Nobitex.Cross.Bot.PositionManagement;

/// <summary>
/// Fee-aware profit mathematics. All formulas operate on <see cref="decimal"/>; no floating point
/// is used anywhere in the pricing path.
/// </summary>
/// <remarks>
/// <para>Cost of a position:</para>
/// <code>
/// effectiveQty   = OriginalQuantity − (FeeAsset == Base   ? BuyFee : 0)
/// totalCostQuote = OriginalQuantity × AverageEntryPrice + (FeeAsset == Quote ? BuyFee : 0)
/// costPerUnit    = totalCostQuote ÷ effectiveQty
/// </para>
/// <para>Required sell price for a target NET profit of T% and a slippage allowance of S%:</para>
/// <code>
/// requiredNetQuote = costPerUnit × qtyToSell × (1 + T)
/// rawPrice         = requiredNetQuote ÷ ( qtyToSell × (1 − sellFeeRate) )
/// sellPrice        = rawPrice × (1 + S)
/// </code>
/// The same expression covers both fee currencies on the sell side, because the fee is expressed
/// as a rate on the traded notional either way.
/// </remarks>
public sealed class ProfitCalculator
{
    /// <summary>Quantity effectively owned after the buy fee was charged in base asset.</summary>
    public decimal GetEffectiveQuantity(Position position, FeeOptions fee)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(fee);

        return fee.FeeAsset == FeeAsset.Base
            ? position.OriginalQuantity - position.BuyFee
            : position.OriginalQuantity;
    }

    /// <summary>Total cost of the position in the quote asset.</summary>
    public decimal GetTotalCostQuote(Position position, FeeOptions fee)
    {
        ArgumentNullException.ThrowIfNull(position);

        return fee.FeeAsset == FeeAsset.Quote
            ? position.TotalCostQuote + position.BuyFee
            : position.TotalCostQuote;
    }

    /// <summary>Cost of one unit of base asset, including fees.</summary>
    public decimal GetBreakEvenPrice(Position position, FeeOptions fee)
    {
        var effectiveQuantity = GetEffectiveQuantity(position, fee);

        if (effectiveQuantity <= 0)
        {
            return 0m;
        }

        return GetTotalCostQuote(position, fee) / effectiveQuantity;
    }

    /// <summary>
    /// Sell price required to realize a net profit of <paramref name="targetNetProfitPercent"/> percent
    /// after buy fee, sell fee and the expected slippage.
    /// </summary>
    /// <param name="position">Open position.</param>
    /// <param name="fee">Fee parameters of the market.</param>
    /// <param name="targetNetProfitPercent">Target NET profit in percent (0 = break-even).</param>
    /// <param name="slippageAllowancePercent">Extra price buffer in percent.</param>
    public decimal GetRequiredSellPrice(
        Position position,
        FeeOptions fee,
        decimal targetNetProfitPercent,
        decimal slippageAllowancePercent)
    {
        var costPerUnit = GetBreakEvenPrice(position, fee);

        if (costPerUnit <= 0)
        {
            return 0m;
        }

        var sellFeeRate = fee.MakerRate;
        if (sellFeeRate >= 1m)
        {
            throw new InvalidOperationException("Fee rate must be lower than 1 (100%).");
        }

        var grossUp = 1m + targetNetProfitPercent / 100m;
        var rawPrice = costPerUnit * grossUp / (1m - sellFeeRate);

        return rawPrice * (1m + slippageAllowancePercent / 100m);
    }

    /// <summary>
    /// Realized NET profit of selling <paramref name="sellQuantity"/> units at
    /// <paramref name="sellPrice"/>, after the sell fee.
    /// </summary>
    /// <param name="position">Position being reduced.</param>
    /// <param name="fee">Fee parameters of the market.</param>
    /// <param name="sellPrice">Execution price.</param>
    /// <param name="sellQuantity">Executed quantity.</param>
    /// <param name="sellFee">Fee reported by the exchange for this sale.</param>
    public decimal ComputeNetProfit(
        Position position,
        FeeOptions fee,
        decimal sellPrice,
        decimal sellQuantity,
        decimal sellFee)
    {
        ArgumentNullException.ThrowIfNull(position);

        var costPerUnit = GetBreakEvenPrice(position, fee);
        var proceeds = sellPrice * sellQuantity;

        // NOTE: by convention every fee passed to this method is expressed in the QUOTE asset,
        // whatever currency the exchange charged it in. PositionManager performs the conversion
        // (a base-asset fee is valued at the execution price) before calling this method.
        var netProceeds = proceeds - sellFee;
        var cost = costPerUnit * sellQuantity;

        return netProceeds - cost;
    }

    /// <summary>
    /// Net profit expressed in percent of the cost of the sold quantity.
    /// </summary>
    public decimal ComputeNetProfitPercent(
        Position position,
        FeeOptions fee,
        decimal sellPrice,
        decimal sellQuantity,
        decimal sellFee)
    {
        var costPerUnit = GetBreakEvenPrice(position, fee);
        var cost = costPerUnit * sellQuantity;

        if (cost <= 0)
        {
            return 0m;
        }

        return ComputeNetProfit(position, fee, sellPrice, sellQuantity, sellFee) / cost * 100m;
    }
}