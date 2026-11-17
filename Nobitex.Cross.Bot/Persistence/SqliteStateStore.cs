using System.Text.Json;
using Nobitex.Cross.Bot.Configuration;
using Nobitex.Cross.Bot.Execution;
using Nobitex.Cross.Bot.PositionManagement;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Nobitex.Cross.Bot.Persistence;

/// <summary>
/// SQLite backed state store. A single row holds the serialized state, which makes every write
/// atomic and keeps restart recovery trivial.
/// </summary>
/// <remarks>
/// Schema:
/// <code>
/// CREATE TABLE IF NOT EXISTS bot_state (
///     id         INTEGER PRIMARY KEY CHECK (id = 1),
///     orders     TEXT    NOT NULL,
///     positions  TEXT    NOT NULL,
///     updated_at TEXT    NOT NULL
/// );
/// </code>
/// </remarks>
public sealed class SqliteStateStore : IStateStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;
    private readonly ILogger<SqliteStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store and initializes the schema.</summary>
    public SqliteStateStore(PersistenceOptions options, ILogger<SqliteStateStore> logger)
    {
        _logger = logger;

        var path = Path.IsPathRooted(options.DatabasePath)
            ? options.DatabasePath
            : Path.Combine(AppContext.BaseDirectory, options.DatabasePath);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    /// <inheritdoc />
    public async Task<StoredState?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT orders, positions, updated_at FROM bot_state WHERE id = 1;";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var orders = JsonSerializer.Deserialize<List<TrackedOrder>>(reader.GetString(0), JsonOptions) ?? new();
            var positions = JsonSerializer.Deserialize<List<Position>>(reader.GetString(1), JsonOptions) ?? new();

            return new StoredState
            {
                Orders = orders,
                Positions = positions,
                SavedAt = DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to load state from SQLite — starting with an empty state");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO bot_state (id, orders, positions, updated_at)
                VALUES (1, $orders, $positions, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    orders = excluded.orders,
                    positions = excluded.positions,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$orders", JsonSerializer.Serialize(state.Orders, JsonOptions));
            command.Parameters.AddWithValue("$positions", JsonSerializer.Serialize(state.Positions, JsonOptions));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveOrdersAsync(IReadOnlyList<TrackedOrder> orders, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? new StoredState();
        current.Orders = orders.ToList();
        await SaveAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SavePositionsAsync(IReadOnlyList<Position> positions, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken).ConfigureAwait(false) ?? new StoredState();
        current.Positions = positions.ToList();
        await SaveAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS bot_state (
                id         INTEGER PRIMARY KEY CHECK (id = 1),
                orders     TEXT    NOT NULL,
                positions  TEXT    NOT NULL,
                updated_at TEXT    NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        _logger.LogInformation("SQLite state store initialized at {Path}", connection.DataSource);
    }
}