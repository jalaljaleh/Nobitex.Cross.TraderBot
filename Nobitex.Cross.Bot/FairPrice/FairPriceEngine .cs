using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.MarketData;

namespace Nobitex.Cross.Bot.FairPrice;

/// <summary>Result of a fair price computation.</summary>
/// <param name="Symbol">Unified symbol.</param>
/// <param name="BinanceMidPrice">Reference mid price.</param>
/// <param name="LocalBasis">Applied basis (premium/discount).</param>
/// <param name="FairPrice">Fair price of the local market.</param>
/// <param name="ObservedAt">UTC timestamp of the reference tick.</param>
/// <param name="IsStale">Whether the underlying data was stale at computation time.</param>
public sealed record FairPriceResult(
    string Symbol,
    decimal BinanceMidPrice,
    decimal LocalBasis,
    decimal FairPrice,
    DateTimeOffset ObservedAt,
    bool IsStale);

/// <summary>Computes the fair price used to anchor the order ladder.</summary>
public interface IFairPriceEngine
{
    /// <summary>Computes the fair price from a market snapshot.</summary>
    FairPriceResult Compute(string symbol, MarketPrice reference, OrderBookSnapshot localBook);

    /// <summary>Current basis estimate.</summary>
    decimal CurrentBasis { get; }
}

/// <summary>
/// Fair price engine:
/// <c>BinanceMidPrice = (BestBid + BestAsk) / 2</c>,
/// <c>FairPrice = BinanceMidPrice × (1 + LocalBasis)</c>.
/// </summary>
/// <remarks>
/// The basis is estimated by <see cref="LocalBasisCalculator"/> (median of a rolling window) and is
/// frozen during market shocks. All arithmetic is performed with <see cref="decimal"/>.
/// </remarks>
public sealed class FairPriceEngine : IFairPriceEngine
{
    private readonly FairPriceOptions _options;
    private readonly LocalBasisCalculator _basis;

    /// <summary>Creates the engine.</summary>
    public FairPriceEngine(FairPriceOptions options, LocalBasisCalculator basis)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _basis = basis ?? throw new ArgumentNullException(nameof(basis));
    }

    /// <inheritdoc />
    public decimal CurrentBasis => _basis.Current;

    /// <inheritdoc />
    public FairPriceResult Compute(string symbol, MarketPrice reference, OrderBookSnapshot localBook)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(localBook);

        // Reference mid: (best bid + best ask) / 2.
        var referenceMid = reference.MidPrice;

        // Local basis: median estimator updated from the local book mid.
        var localMid = localBook.MidPrice ?? referenceMid;
        var basis = _basis.AddSample(localMid, referenceMid, DateTimeOffset.UtcNow);

        var fairPrice = referenceMid * (1m + basis);
        var isStale = reference.IsStale(_options.MaxReferenceAge) || localBook.IsStale(_options.MaxReferenceAge);

        return new FairPriceResult(symbol, referenceMid, basis, fairPrice, reference.Timestamp, isStale);
    }

    /// <summary>
    /// Alternative reference: volume-weighted mid price of the top <paramref name="levels"/> of the
    /// reference book. Kept as a strategy option for markets with a thin top of book.
    /// </summary>
    public static decimal WeightedMidPrice(OrderBookSnapshot book, int levels)
    {
        var bids = book.Bids.Take(levels).ToList();
        var asks = book.Asks.Take(levels).ToList();

        decimal bidWeighted = 0, bidVolume = 0, askWeighted = 0, askVolume = 0;

        foreach (var level in bids)
        {
            bidWeighted += level.Price * level.Quantity;
            bidVolume += level.Quantity;
        }

        foreach (var level in asks)
        {
            askWeighted += level.Price * level.Quantity;
            askVolume += level.Quantity;
        }

        var bid = bidVolume == 0 ? book.BestBid ?? 0m : bidWeighted / bidVolume;
        var ask = askVolume == 0 ? book.BestAsk ?? 0m : askWeighted / askVolume;

        return (bid + ask) / 2m;
    }
}