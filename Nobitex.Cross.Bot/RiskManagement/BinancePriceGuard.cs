using System.Collections.Concurrent;
using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.MarketData;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.RiskManagement;

/// <summary>Severity of a guard evaluation.</summary>
public enum GuardState
{
    /// <summary>Everything is normal, quoting is allowed.</summary>
    Normal,

    /// <summary>Something is unusual but within tolerance; quoting continues with caution.</summary>
    Watch,

    /// <summary>Hard limit breached: cancel resting buys and freeze quoting.</summary>
    Tripped,
}

/// <summary>Result of one guard evaluation.</summary>
/// <param name="State">Guard state.</param>
/// <param name="Reason">Reason code (e.g. <c>ReferenceVelocity</c>).</param>
/// <param name="Message">Human readable message for the log.</param>
/// <param name="CooldownUntil">UTC time until which quoting stays blocked.</param>
public sealed record GuardVerdict(GuardState State, string? Reason, string? Message, DateTimeOffset? CooldownUntil)
{
    /// <summary>Normal operation.</summary>
    public static GuardVerdict Ok() => new(GuardState.Normal, null, null, null);

    /// <summary>Watch state.</summary>
    public static GuardVerdict Watch(string reason, string message) => new(GuardState.Watch, reason, message, null);

    /// <summary>Tripped state with a cooldown.</summary>
    public static GuardVerdict Trip(string reason, string message, DateTimeOffset cooldownUntil)
        => new(GuardState.Tripped, reason, message, cooldownUntil);
}

/// <summary>
/// Binance price guard. Watches the reference venue for crashes, volatility spikes, stale data,
/// feed disconnection and spread widening, and blocks quoting for a cooldown period after a trip.
/// </summary>
/// <remarks>
/// The guard is evaluated on every engine tick. After a trip the engine cancels resting buy orders
/// and <c>LocalBasisCalculator</c> is frozen, so a corrupted sample cannot contaminate the fair price.
/// </remarks>
public sealed class BinancePriceGuard
{
    private readonly GuardOptions _options;
    private readonly ILogger<BinancePriceGuard> _logger;
    private readonly ConcurrentQueue<decimal> _midSamples = new();
    private readonly ConcurrentQueue<decimal> _spreadSamples = new();
    private DateTimeOffset? _trippedUntil;
    private decimal _lastMidPrice;
    private decimal _lastFairPrice;
    private DateTimeOffset _lastSampleAt;
    private decimal _consecutiveFailures;

    /// <summary>Creates the guard.</summary>
    public BinancePriceGuard(GuardOptions options, ILogger<BinancePriceGuard> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Current state of the guard.</summary>
    public GuardState State { get; private set; } = GuardState.Normal;

    /// <summary>Evaluates the current market snapshot.</summary>
    /// <param name="reference">Binance reference tick.</param>
    /// <param name="fairPrice">Currently computed fair price.</param>
    /// <param name="adapterHealthy">Whether the Nobitex adapter answered the last call.</param>
    public GuardVerdict Evaluate(MarketPrice reference, decimal fairPrice, bool adapterHealthy)
    {
        var now = DateTimeOffset.UtcNow;

        // 0) cooldown still open?
        if (_trippedUntil is { } until && now < until)
        {
            return GuardVerdict.Trip("Cooldown", "Guard cooldown is still active.", until);
        }

        // 1) stale data — the reference tick must be younger than 5 seconds
        if (reference.IsStale(TimeSpan.FromSeconds(5)))
        {
            return Trip("ReferenceDataStale", $"Reference tick is {now - reference.Timestamp} old.", now);
        }

        // 2) exchange unreachable
        if (!adapterHealthy)
        {
            _consecutiveFailures++;
        }
        else
        {
            _consecutiveFailures = 0;
        }

        if (_consecutiveFailures >= 3)
        {
            return Trip("ExchangeUnreachable", "Nobitex adapter failed repeatedly.", now);
        }

        // 3) crash velocity
        if (_lastSampleAt != default && now > _lastSampleAt && _lastMidPrice > 0)
        {
            var elapsedSeconds = (decimal)(now - _lastSampleAt).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                var velocity = Math.Abs(reference.MidPrice - _lastMidPrice) / _lastMidPrice * 100m / elapsedSeconds;
                if (velocity > _options.MaxDropPerSecondPercent)
                {
                    return Trip("ReferenceVelocity", $"Reference moved {velocity:0.000}%/s.", now);
                }
            }
        }

        // 4) volatility spike
        var volatility = ComputeVolatility(reference.MidPrice);
        if (volatility > _options.MaxVolatilityPercent)
        {
            return Trip("VolatilitySpike", $"Rolling volatility is {volatility:0.00}%.", now);
        }

        // 5) spread widening
        var baselineSpread = _spreadSamples.Count == 0 ? reference.SpreadPercent : _spreadSamples.Average();
        if (baselineSpread > 0 && reference.SpreadPercent > baselineSpread * _options.MaxSpreadWidenFactor)
        {
            return Trip("SpreadWidening",
                $"Spread {reference.SpreadPercent:0.000}% vs baseline {baselineSpread:0.000}%.", now);
        }

        // 6) abnormal fair price jump
        if (_lastFairPrice > 0)
        {
            var jump = Math.Abs(fairPrice - _lastFairPrice) / _lastFairPrice * 100m;
            if (jump > _options.MaxFairPriceJumpPercent)
            {
                return Trip("FairPriceJump", $"Fair price jumped {jump:0.000}%.", now);
            }
        }

        // sample bookkeeping
        _lastMidPrice = reference.MidPrice;
        _lastFairPrice = fairPrice;
        _lastSampleAt = now;
        _midSamples.Enqueue(reference.MidPrice);
        _spreadSamples.Enqueue(reference.SpreadPercent);
        while (_midSamples.Count > 120 && _midSamples.TryDequeue(out _)) { }
        while (_spreadSamples.Count > 120 && _spreadSamples.TryDequeue(out _)) { }

        if (volatility > _options.MaxVolatilityPercent / 2m)
        {
            State = GuardState.Watch;
            return GuardVerdict.Watch("VolatilityWatch", $"Volatility {volatility:0.00}% is elevated.");
        }

        State = GuardState.Normal;
        return GuardVerdict.Ok();
    }

    /// <summary>Registers an external failure (used for connection errors).</summary>
    public void ReportFailure() => _consecutiveFailures++;

    private GuardVerdict Trip(string reason, string message, DateTimeOffset now)
    {
        _trippedUntil = now + _options.Cooldown;
        State = GuardState.Tripped;
        _logger.LogWarning("GUARD TRIPPED [{Reason}]: {Message} (cooldown {Cooldown}s)", reason, message, _options.Cooldown.TotalSeconds);
        return GuardVerdict.Trip(reason, message, _trippedUntil.Value);
    }

    private decimal ComputeVolatility(decimal currentMid)
    {
        if (_midSamples.Count < 10)
        {
            return 0m;
        }

        var samples = _midSamples.ToList();
        var returns = new List<decimal>(samples.Count);

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i - 1] > 0)
            {
                returns.Add((samples[i] - samples[i - 1]) / samples[i - 1] * 100m);
            }
        }

        if (returns.Count < 2)
        {
            return 0m;
        }

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / returns.Count;

        return (decimal)Math.Sqrt((double)variance) * 10m;   // scaled to a comparable percent figure
    }
}