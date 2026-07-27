using Microsoft.Data.Sqlite;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests;

internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TestDatabase(
        string directory,
        string path,
        SqliteConnectionFactory connectionFactory,
        DatabaseInitializer initializer)
    {
        _directory = directory;
        Path = path;
        ConnectionFactory = connectionFactory;
        Initializer = initializer;
    }

    public string Path { get; }

    public SqliteConnectionFactory ConnectionFactory { get; }

    public DatabaseInitializer Initializer { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TwitchAI.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "test.db");
        var factory = new SqliteConnectionFactory(new InfrastructureOptions
        {
            DatabasePath = path,
        });
        var initializer = new DatabaseInitializer(factory);
        var database = new TestDatabase(directory, path, factory, initializer);

        try
        {
            await initializer.InitializeAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public static async Task<TestDatabase> CreateAtSchemaVersionAsync(long schemaVersion)
    {
        if (schemaVersion < 1 ||
            schemaVersion > SchemaMigrations.All.Max(item => item.Version))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TwitchAI.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "test.db");
        var factory = new SqliteConnectionFactory(new InfrastructureOptions
        {
            DatabasePath = path,
        });
        var initializer = new DatabaseInitializer(factory);
        var database = new TestDatabase(directory, path, factory, initializer);

        try
        {
            await using var connection = await factory.OpenConnectionAsync();
            await using (var createMigrations = connection.CreateCommand())
            {
                createMigrations.CommandText =
                    """
                    CREATE TABLE schema_migrations (
                        version      INTEGER PRIMARY KEY,
                        description  TEXT NOT NULL,
                        applied_utc  TEXT NOT NULL
                    ) STRICT;
                    """;
                await createMigrations.ExecuteNonQueryAsync();
            }

            foreach (var migration in SchemaMigrations.All.Where(
                         item => item.Version <= schemaVersion))
            {
                if (migration.RequiresForeignKeysDisabled)
                {
                    await using var disableForeignKeys = connection.CreateCommand();
                    disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
                    await disableForeignKeys.ExecuteNonQueryAsync();
                }

                await using (var transaction = connection.BeginTransaction())
                {
                    await using (var apply = connection.CreateCommand())
                    {
                        apply.Transaction = transaction;
                        apply.CommandText = migration.Sql;
                        await apply.ExecuteNonQueryAsync();
                    }

                    await using (var record = connection.CreateCommand())
                    {
                        record.Transaction = transaction;
                        record.CommandText =
                            """
                            INSERT INTO schema_migrations(
                                version, description, applied_utc)
                            VALUES(@version, @description, @applied_utc);
                            """;
                        record.Parameters.AddWithValue("@version", migration.Version);
                        record.Parameters.AddWithValue("@description", migration.Description);
                        record.Parameters.AddWithValue(
                            "@applied_utc",
                            DateTimeOffset.UtcNow.ToString("O"));
                        await record.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }

                if (migration.RequiresForeignKeysDisabled)
                {
                    await using var enableForeignKeys = connection.CreateCommand();
                    enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                    await enableForeignKeys.ExecuteNonQueryAsync();
                }
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        return ConnectionFactory.OpenConnectionAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Initializer.Dispose();
        await using (var connection = await ConnectionFactory.OpenConnectionAsync())
        {
            // Every test has a unique data source. Clearing only this pool avoids
            // disposing handles that a parallel test is currently using.
            SqliteConnection.ClearPool(connection);
        }

        if (!Directory.Exists(_directory))
        {
            return;
        }

        await Task.Run(() => Directory.Delete(_directory, recursive: true));
    }
}
