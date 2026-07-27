using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace TwitchAI.Infrastructure.Persistence;

public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseInitializer : IDatabaseInitializer, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public DatabaseInitializer(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var writeLease = await _writeCoordinator
                .AcquireAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var connection = await _connectionFactory
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken)
                .ConfigureAwait(false);

            await EnsureMigrationTableAsync(connection, cancellationToken).ConfigureAwait(false);
            var currentVersion = await GetCurrentVersionAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            foreach (var migration in SchemaMigrations.All.Where(item => item.Version > currentVersion))
            {
                if (migration.RequiresBackup)
                {
                    await CreateVerifiedBackupAsync(connection, migration, cancellationToken)
                        .ConfigureAwait(false);
                }

                await ApplyMigrationAsync(connection, migration, cancellationToken)
                    .ConfigureAwait(false);
            }

            await VerifyDatabaseIntegrityAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await RecoverInterruptedModerationAttemptsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }

    private static async Task EnsureMigrationTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version      INTEGER PRIMARY KEY,
                description  TEXT NOT NULL,
                applied_utc  TEXT NOT NULL
            ) STRICT;
            """;

        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetCurrentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyMigrationAsync(
        SqliteConnection connection,
        SchemaMigration migration,
        CancellationToken cancellationToken)
    {
        var foreignKeysDisabled = false;
        var foreignKeysRestored = true;
        try
        {
            if (migration.RequiresForeignKeysDisabled)
            {
                await ExecuteAsync(
                        connection,
                        "PRAGMA foreign_keys = OFF;",
                        cancellationToken)
                    .ConfigureAwait(false);
                var foreignKeys = await ExecuteScalarLongAsync(
                        connection,
                        "PRAGMA foreign_keys;",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (foreignKeys != 0)
                {
                    throw new InvalidOperationException(
                        $"Migration {migration.Version} requires foreign-key enforcement to be disabled.");
                }

                foreignKeysDisabled = true;
            }

            await using var transaction = connection.BeginTransaction();

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureNoForeignKeyViolationsAsync(
                    connection,
                    transaction,
                    cancellationToken)
                .ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO schema_migrations(version, description, applied_utc)
                    VALUES(@version, @description, @applied_utc);
                    """;
                command.Parameters.AddWithValue("@version", migration.Version);
                command.Parameters.AddWithValue("@description", migration.Description);
                command.Parameters.AddWithValue(
                    "@applied_utc",
                    DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (foreignKeysDisabled)
            {
                try
                {
                    await ExecuteAsync(
                            connection,
                            "PRAGMA foreign_keys = ON;",
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    foreignKeysRestored = await ExecuteScalarLongAsync(
                            connection,
                            "PRAGMA foreign_keys;",
                            CancellationToken.None)
                        .ConfigureAwait(false) == 1;
                }
                catch (SqliteException)
                {
                    foreignKeysRestored = false;
                }
            }
        }

        if (!foreignKeysRestored)
        {
            throw new InvalidOperationException(
                $"Migration {migration.Version} did not restore foreign-key enforcement.");
        }

        await VerifyDatabaseIntegrityAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CreateVerifiedBackupAsync(
        SqliteConnection source,
        SchemaMigration migration,
        CancellationToken cancellationToken)
    {
        var sourcePath = source.DataSource;
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            StringComparer.OrdinalIgnoreCase.Equals(sourcePath, ":memory:") ||
            sourcePath.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException(
                "The SQLite database path has no parent directory.");
        var fileName = Path.GetFileName(sourcePath);
        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMddTHHmmssfffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(
            directory,
            $"{fileName}.pre-schema-v{migration.Version}.{timestamp}.{Guid.NewGuid():N}.bak");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();

        await using var backup = new SqliteConnection(connectionString);
        await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(backup);
        await VerifyDatabaseIntegrityAsync(backup, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyDatabaseIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            var failures = new List<string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = reader.GetString(0);
                if (!StringComparer.OrdinalIgnoreCase.Equals(value, "ok"))
                {
                    failures.Add(value);
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"SQLite integrity check failed: {string.Join("; ", failures)}");
            }
        }

        await EnsureNoForeignKeyViolationsAsync(
                connection,
                transaction: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EnsureNoForeignKeyViolationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var table = reader.GetString(0);
            var rowId = reader.IsDBNull(1)
                ? "unknown"
                : reader.GetInt64(1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"SQLite foreign-key check failed for table '{table}', row '{rowId}'.");
        }
    }

    private static async Task<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(
            result,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task RecoverInterruptedModerationAttemptsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using (var attempts = connection.CreateCommand())
        {
            attempts.Transaction = transaction;
            attempts.CommandText =
                """
                UPDATE action_attempts
                SET status = 'outcome_unknown',
                    completed_utc = @now,
                    error_code = 'process_interrupted_after_intent',
                    response_json = 'The application stopped after persisting the outbound intent; the Twitch result is unknown.'
                WHERE status = 'intent';
                """;
            attempts.Parameters.AddWithValue("@now", now);
            await attempts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var availability = connection.CreateCommand())
        {
            availability.Transaction = transaction;
            availability.CommandText =
                """
                INSERT OR IGNORE INTO message_availability_events(
                    eventsub_message_id, broadcaster_id, twitch_message_id,
                    target_user_id, availability, occurred_utc, received_utc,
                    correlation_kind, learning_state, case_id)
                SELECT
                    'local-recovery:' || execution_id,
                    broadcaster_id,
                    twitch_message_id,
                    target_user_id,
                    'outcome_unknown',
                    requested_utc,
                    @now,
                    'local_or_ambiguous',
                    'unavailable',
                    case_id
                FROM action_attempts
                WHERE status = 'outcome_unknown'
                  AND error_code = 'process_interrupted_after_intent';
                """;
            availability.Parameters.AddWithValue("@now", now);
            await availability.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var cases = connection.CreateCommand())
        {
            cases.Transaction = transaction;
            cases.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'outcome_unknown',
                    row_version = row_version + 1,
                    resolved_utc = @now
                WHERE status = 'executing';
                """;
            cases.Parameters.AddWithValue("@now", now);
            await cases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DatabaseInitializationHostedService : IHostedService
{
    private readonly IDatabaseInitializer _initializer;

    public DatabaseInitializationHostedService(IDatabaseInitializer initializer)
    {
        _initializer = initializer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return _initializer.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
