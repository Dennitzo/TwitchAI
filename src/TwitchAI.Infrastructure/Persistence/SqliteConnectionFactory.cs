using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public interface ISqliteConnectionFactory
{
    ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var databasePath = Path.GetFullPath(options.DatabasePath);
        var directory = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException("The database path must include a directory.", nameof(options));

        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken)
                .ConfigureAwait(false);
            await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
