using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FeedPeek.Core.Notifications;

public readonly record struct NotificationDiskEntry<TKey, TValue>(
    long Sequence, TKey Key, TValue Value);

/// <summary>
/// A small disk backed map for process-local notification state.
///
/// The database is an unnamed temporary SQLite database. Pooling is disabled so
/// closing the connection removes the temporary database. The map is intended
/// for bounded-memory spools and ledgers; it deliberately does not survive a
/// process restart.
/// </summary>
public sealed class NotificationDiskMap<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private const int DefaultBatchSize = 256;

    private readonly object _gate = new();
    private readonly Func<TKey, string>? _getScope;
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _countCommand;
    private readonly SqliteCommand _containsCommand;
    private readonly SqliteCommand _getCommand;
    private readonly SqliteCommand _tryAddCommand;
    private readonly SqliteCommand _setCommand;
    private readonly SqliteCommand _removeCommand;
    private readonly SqliteCommand _removeScopeCommand;
    private readonly SqliteCommand _clearCommand;
    private readonly SqliteCommand _readBatchCommand;
    private readonly SqliteCommand _vacuumCommand;
    private SqliteTransaction? _activeTransaction;
    private bool _vacuumRequested;
    private bool _disposed;

    public NotificationDiskMap(Func<TKey, string>? getScope = null)
    {
        _getScope = getScope;
        SqliteConnection? connection = null;
        var commands = new List<SqliteCommand>();
        var ownershipTransferred = false;
        try
        {
            connection = new SqliteConnection(
                "Data Source=;Mode=ReadWriteCreate;Pooling=False");
            connection.Open();

            ExecuteSchemaPragmas(connection);
            ExecuteSchema(connection);

            var countCommand = Prepare(connection, "SELECT COUNT(*) FROM notification_disk_map_entries;");
            commands.Add(countCommand);
            var containsCommand = Prepare(connection,
                "SELECT 1 FROM notification_disk_map_entries WHERE scope = @scope AND key_json = @key_json LIMIT 1;",
                ("@scope", SqliteType.Text), ("@key_json", SqliteType.Text));
            commands.Add(containsCommand);
            var getCommand = Prepare(connection,
                "SELECT value_json FROM notification_disk_map_entries WHERE scope = @scope AND key_json = @key_json LIMIT 1;",
                ("@scope", SqliteType.Text), ("@key_json", SqliteType.Text));
            commands.Add(getCommand);
            var tryAddCommand = Prepare(connection,
                "INSERT OR IGNORE INTO notification_disk_map_entries (scope, key_json, value_json) VALUES (@scope, @key_json, @value_json);",
                ("@scope", SqliteType.Text), ("@key_json", SqliteType.Text), ("@value_json", SqliteType.Text));
            commands.Add(tryAddCommand);
            var setCommand = Prepare(connection,
                "INSERT INTO notification_disk_map_entries (scope, key_json, value_json) VALUES (@scope, @key_json, @value_json) " +
                "ON CONFLICT (scope, key_json) DO UPDATE SET value_json = excluded.value_json;",
                ("@scope", SqliteType.Text), ("@key_json", SqliteType.Text), ("@value_json", SqliteType.Text));
            commands.Add(setCommand);
            var removeCommand = Prepare(connection,
                "DELETE FROM notification_disk_map_entries WHERE scope = @scope AND key_json = @key_json;",
                ("@scope", SqliteType.Text), ("@key_json", SqliteType.Text));
            commands.Add(removeCommand);
            var removeScopeCommand = Prepare(connection,
                "DELETE FROM notification_disk_map_entries WHERE scope = @scope;",
                ("@scope", SqliteType.Text));
            commands.Add(removeScopeCommand);
            var clearCommand = Prepare(connection, "DELETE FROM notification_disk_map_entries;");
            commands.Add(clearCommand);
            var readBatchCommand = Prepare(connection,
                "SELECT sequence, key_json, value_json FROM notification_disk_map_entries " +
                "WHERE sequence > @after_sequence AND sequence <= @through_sequence ORDER BY sequence LIMIT @limit;",
                ("@after_sequence", SqliteType.Integer), ("@through_sequence", SqliteType.Integer), ("@limit", SqliteType.Integer));
            commands.Add(readBatchCommand);
            var vacuumCommand = Prepare(connection, "PRAGMA incremental_vacuum;");
            commands.Add(vacuumCommand);

            _connection = connection;
            _countCommand = countCommand;
            _containsCommand = containsCommand;
            _getCommand = getCommand;
            _tryAddCommand = tryAddCommand;
            _setCommand = setCommand;
            _removeCommand = removeCommand;
            _removeScopeCommand = removeScopeCommand;
            _clearCommand = clearCommand;
            _readBatchCommand = readBatchCommand;
            _vacuumCommand = vacuumCommand;
            ownershipTransferred = true;
        }
        catch
        {
            if (!ownershipTransferred)
            {
                foreach (var command in commands)
                    command.Dispose();
                connection?.Dispose();
            }

            throw;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureUsable();
                BindTransaction(_countCommand);
                return checked(Convert.ToInt32(_countCommand.ExecuteScalar()));
            }
        }
    }

    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                EnsureUsable();
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM notification_disk_map_entries;";
                command.Transaction = _activeTransaction;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }

    public bool ContainsKey(TKey key)
    {
        lock (_gate)
        {
            EnsureUsable();
            SetKeyParameters(_containsCommand, key);
            BindTransaction(_containsCommand);
            return _containsCommand.ExecuteScalar() is not null;
        }
    }

    public bool TryAdd(TKey key, TValue value)
    {
        lock (_gate)
        {
            EnsureUsable();
            SetKeyValueParameters(_tryAddCommand, key, value);
            BindTransaction(_tryAddCommand);
            return _tryAddCommand.ExecuteNonQuery() == 1;
        }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            EnsureUsable();
            SetKeyValueParameters(_setCommand, key, value);
            BindTransaction(_setCommand);
            _setCommand.ExecuteNonQuery();
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        lock (_gate)
        {
            EnsureUsable();
            SetKeyParameters(_getCommand, key);
            BindTransaction(_getCommand);
            var json = _getCommand.ExecuteScalar() as string;
            if (json is null)
            {
                value = default!;
                return false;
            }

            value = DeserializeValue(json);
            return true;
        }
    }

    public bool Remove(TKey key)
    {
        lock (_gate)
        {
            EnsureUsable();
            SetKeyParameters(_removeCommand, key);
            BindTransaction(_removeCommand);
            var removed = _removeCommand.ExecuteNonQuery() == 1;
            if (removed)
                RequestVacuum();
            return removed;
        }
    }

    public void RemoveScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            EnsureUsable();
            _removeScopeCommand.Parameters["@scope"].Value = scope;
            BindTransaction(_removeScopeCommand);
            if (_removeScopeCommand.ExecuteNonQuery() > 0)
                RequestVacuum();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            EnsureUsable();
            BindTransaction(_clearCommand);
            if (_clearCommand.ExecuteNonQuery() > 0)
                RequestVacuum();
        }
    }

    public IReadOnlyList<NotificationDiskEntry<TKey, TValue>> ReadBatch(
        int limit = DefaultBatchSize, long afterSequence = 0, long throughSequence = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(throughSequence);
        lock (_gate)
        {
            EnsureUsable();
            _readBatchCommand.Parameters["@after_sequence"].Value = afterSequence;
            _readBatchCommand.Parameters["@through_sequence"].Value = throughSequence;
            _readBatchCommand.Parameters["@limit"].Value = limit;
            BindTransaction(_readBatchCommand);

            var entries = new List<NotificationDiskEntry<TKey, TValue>>(Math.Min(limit, DefaultBatchSize));
            using var reader = _readBatchCommand.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new(
                    reader.GetInt64(0),
                    DeserializeKey(reader.GetString(1)),
                    DeserializeValue(reader.GetString(2))));
            }

            return entries;
        }
    }

    /// <summary>
    /// Serializes operations under one SQLite transaction. The transaction is
    /// committed when the returned scope is disposed. Nested batches are not
    /// supported; a failed operation may leave earlier records committed when
    /// the scope closes, which is safe for a spool/ledger. This is a synchronous,
    /// thread-affine scope: do not await while it is held or carry the returned
    /// scope across an await; finish and dispose it before yielding.
    /// </summary>
    public IDisposable BeginBatch()
    {
        Monitor.Enter(_gate);
        try
        {
            EnsureUsable();
            if (_activeTransaction is not null)
                throw new InvalidOperationException("Notification disk map batches cannot be nested.");
            _activeTransaction = _connection.BeginTransaction();
            _vacuumRequested = false;
            return new BatchScope(this);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_activeTransaction is not null)
                throw new InvalidOperationException("Dispose the active notification disk map batch first.");

            _disposed = true;
            _countCommand.Dispose();
            _containsCommand.Dispose();
            _getCommand.Dispose();
            _tryAddCommand.Dispose();
            _setCommand.Dispose();
            _removeCommand.Dispose();
            _removeScopeCommand.Dispose();
            _clearCommand.Dispose();
            _readBatchCommand.Dispose();
            _vacuumCommand.Dispose();
            _connection.Dispose();
        }
    }

    private static void ExecuteSchemaPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA auto_vacuum = INCREMENTAL; PRAGMA cache_size = -256; PRAGMA temp_store = FILE;";
        command.ExecuteNonQuery();
    }

    private static void ExecuteSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS notification_disk_map_entries (" +
            "sequence INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "scope TEXT NOT NULL, key_json TEXT NOT NULL, value_json TEXT NOT NULL, " +
            "UNIQUE (scope, key_json));" +
            "CREATE INDEX IF NOT EXISTS ix_notification_disk_map_scope " +
            "ON notification_disk_map_entries (scope, sequence);";
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string sql,
        params (string Name, SqliteType Type)[] parameters)
    {
        var command = connection.CreateCommand();
        try
        {
            command.CommandText = sql;
            foreach (var (name, type) in parameters)
                command.Parameters.Add(name, type);
            command.Prepare();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private void SetKeyParameters(SqliteCommand command, TKey key)
    {
        command.Parameters["@scope"].Value = ScopeFor(key);
        command.Parameters["@key_json"].Value = SerializeKey(key);
    }

    private void SetKeyValueParameters(SqliteCommand command, TKey key, TValue value)
    {
        SetKeyParameters(command, key);
        command.Parameters["@value_json"].Value = SerializeValue(value);
    }

    private string ScopeFor(TKey key)
        => _getScope?.Invoke(key) ?? string.Empty;

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json)!;

    private static string SerializeKey(TKey value)
        => JsonSerializer.Serialize(value);

    private static string SerializeValue(TValue value)
        => JsonSerializer.Serialize(value);

    private static TKey DeserializeKey(string json)
        => Deserialize<TKey>(json);

    private static TValue DeserializeValue(string json)
        => Deserialize<TValue>(json);

    private void BindTransaction(SqliteCommand command)
        => command.Transaction = _activeTransaction;

    private void RequestVacuum()
    {
        _vacuumRequested = true;
        if (_activeTransaction is null)
            IncrementalVacuum();
    }

    private void IncrementalVacuum()
    {
        BindTransaction(_vacuumCommand);
        _vacuumCommand.ExecuteNonQuery();
        _vacuumRequested = false;
    }

    private void EndBatch()
    {
        try
        {
            _activeTransaction!.Commit();
            _activeTransaction.Dispose();
            _activeTransaction = null;
            if (_vacuumRequested)
                IncrementalVacuum();
        }
        catch
        {
            _activeTransaction?.Dispose();
            _activeTransaction = null;
            throw;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private void EnsureUsable()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class BatchScope(NotificationDiskMap<TKey, TValue> owner) : IDisposable
    {
        private NotificationDiskMap<TKey, TValue>? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndBatch();
    }
}
