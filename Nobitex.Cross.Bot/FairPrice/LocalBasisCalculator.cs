using System.Collections.Concurrent;

namespace Nobitex.Cross.Bot.FairPrice;

/// <summary>
/// Estimates the persistent premium/discount of the local market against the reference venue.
/// </summary>
/// <remarks>
/// <para>
/// LocalBasis = median( NobitexMid ÷ BinanceMid − 1 ) over a rolling window of samples.
/// The median is used instead of the mean so a single manipulated or glitched print cannot move
/// the reference price of the whole ladder.
/// </para>
/// <para>
/// Samples are ignored while <see cref="IsFrozen"/> is <c>true</c> — the estimator is frozen by
/// <c>BinancePriceGuard</c> during shocks and by the deviation check below.
/// </para>
/// </remarks>
public sealed class LocalBasisCalculator
{
    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly int _window;
    private readonly decimal _override;
    private readonly bool _hasOverride;
    private readonly decimal _freezeSigma;

    /// <summary>Creates the estimator.</summary>
    /// <param name="window">Number of samples kept.</param>
    /// <param name="freezeSigma">Freeze when the observation deviates by more than N sigmas.</param>
    /// <param name="overrideValue">Manual basis override; when provided the estimator is bypassed.</param>
    public LocalBasisCalculator(int window, decimal freezeSigma, decimal? overrideValue = null)
    {
        _window = Math.Max(1, window);
        _freezeSigma = freezeSigma;
        _hasOverride = overrideValue.HasValue;
        _override = overrideValue ?? 0m;
    }

    /// <summary>Gets a value indicating whether the estimator currently ignores new samples.</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>Number of samples currently held.</summary>
    public int SampleCount => _samples.Count;

    /// <summary>Forces the frozen state (used by the price guard).</summary>
    public void Freeze() => IsFrozen = true;

    /// <summary>Unfreezes the estimator.</summary>
    public void Unfreeze() => IsFrozen = false;

    /// <summary>
    /// Adds an observation and returns the current basis estimate.
    /// </summary>
    /// <param name="localMid">Nobitex mid price.</param>
    /// <param name="referenceMid">Binance mid price.</param>
    /// <param name="nowUtc">UTC timestamp of the observation.</param>
    public decimal AddSample(decimal localMid, decimal referenceMid, DateTimeOffset nowUtc)
    {
        if (_hasOverride)
        {
            return _override;
        }

        if (referenceMid <= 0 || localMid <= 0)
        {
            return Current;
        }

        var observation = (localMid / referenceMid) - 1m;

        // Deviation control: a wild observation freezes the estimator instead of polluting it.
        if (_samples.Count >= 20 && Math.Abs(observation - Median()) > _freezeSigma * StandardDeviation())
        {
            IsFrozen = true;
            return Current;
        }

        if (!IsFrozen)
        {
            _samples.Enqueue(new Sample(observation, nowUtc));

            while (_samples.Count > _window && _samples.TryDequeue(out _))
            {
                // Trim the window.
            }
        }

        return Current;
    }

    /// <summary>Current basis estimate (0 when no sample has been observed yet).</summary>
    public decimal Current
    {
        get
        {
            if (_hasOverride)
            {
                return _override;
            }

            return _samples.IsEmpty ? 0m : Median();
        }
    }

    private decimal Median()
    {
        var values = _samples.Select(s => s.Value).OrderBy(v => v).ToList();
        if (values.Count == 0)
        {
            return 0m;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2m;
    }

    private decimal StandardDeviation()
    {
        var values = _samples.Select(s => s.Value).ToList();
        if (values.Count < 2)
        {
            return 0m;
        }

        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (decimal)Math.Sqrt((double)variance);
    }

    private readonly record struct Sample(decimal Value, DateTimeOffset Timestamp);
}