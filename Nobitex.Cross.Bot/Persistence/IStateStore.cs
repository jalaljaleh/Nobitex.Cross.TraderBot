using Nobitex.Cross.Bot.Execution;
using Nobitex.Cross.Bot.PositionManagement;

namespace Nobitex.Cross.Bot.Persistence;

/// <summary>Everything that must survive a restart.</summary>
public sealed class StoredState
{
    /// <summary>Locally tracked orders.</summary>
    public List<TrackedOrder> Orders { get; set; } = new();

    /// <summary>Known positions (open and closed).</summary>
    public List<Position> Positions { get; set; } = new();

    /// <summary>UTC time of the last save.</summary>
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// Durable storage for orders and positions. Implementations must be safe to call from any task
/// and must never lose the last successfully saved state.
/// </summary>
public interface IStateStore
{
    /// <summary>Loads the last saved state, or <c>null</c> when nothing was saved yet.</summary>
    Task<StoredState?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Persists the complete state atomically.</summary>
    Task SaveAsync(StoredState state, CancellationToken cancellationToken);

    /// <summary>Persists the order table.</summary>
    Task SaveOrdersAsync(IReadOnlyList<Nobitex.Cross.Bot.Execution.TrackedOrder> orders, CancellationToken cancellationToken);

    /// <summary>Persists the position table.</summary>
    Task SavePositionsAsync(IReadOnlyList<Nobitex.Cross.Bot.PositionManagement.Position> positions, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory store used by DryRun, PaperTrading and unit tests. Data is lost when the process ends,
/// which is exactly what those modes want.
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private StoredState _state = new();

    /// <inheritdoc />
    public Task<StoredState?> LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult<StoredState?>(_state);

    /// <inheritdoc />
    public Task SaveAsync(StoredState state, CancellationToken cancellationToken)
    {
        _state = state;
        _state.SavedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveOrdersAsync(IReadOnlyList<Nobitex.Cross.Bot.Execution.TrackedOrder> orders, CancellationToken cancellationToken)
    {
        _state.Orders = orders.ToList();
        _state.SavedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SavePositionsAsync(IReadOnlyList<Nobitex.Cross.Bot.PositionManagement.Position> positions, CancellationToken cancellationToken)
    {
        _state.Positions = positions.ToList();
        _state.SavedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}