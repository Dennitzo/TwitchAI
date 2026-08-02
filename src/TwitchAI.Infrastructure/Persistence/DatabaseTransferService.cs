using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

/// <summary>Transfers the complete SQLite data state between installations.</summary>
public interface IDatabaseTransferService
{
    Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default);
    Task ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public sealed class DatabaseTransferService : IDatabaseTransferService
{
    private readonly InfrastructureOptions _options;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IReadOnlyList<IPolicyCacheInvalidator> _cacheInvalidators;

    public DatabaseTransferService(
        InfrastructureOptions options,
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator writeCoordinator,
        IEnumerable<IPolicyCacheInvalidator> cacheInvalidators)
    {
        _options = options;
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator;
        _cacheInvalidators = cacheInvalidators.ToArray();
    }

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var databasePath = GetDatabasePath();
        var target = ValidateTransferPath(destinationPath, databasePath, mustExist: false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);

        await using var lease = await _writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, $"VACUUM INTO '{EscapeLiteral(target)}';", cancellationToken).ConfigureAwait(false);
        await VerifyFileAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var databasePath = GetDatabasePath();
        var source = ValidateTransferPath(sourcePath, databasePath, mustExist: true);
        await VerifyFileAsync(source, cancellationToken).ConfigureAwait(false);

        await using var lease = await _writeCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var backup = databasePath + ".pre-import-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
        if (File.Exists(databasePath))
        {
            await using var current = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(current, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(current, $"VACUUM INTO '{EscapeLiteral(backup)}';", cancellationToken).ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
        TryDelete(databasePath + "-wal");
        TryDelete(databasePath + "-shm");
        var temp = databasePath + ".importing-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, databasePath, overwrite: true);
            SqliteConnection.ClearAllPools();
            await using var imported = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ActivateConfigurationAsync(imported, cancellationToken).ConfigureAwait(false);
            await VerifyConnectionAsync(imported, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }

        var change = new PolicyChangeSet("*", null, null, AllowlistChanged: true, NewRevision: null);
        foreach (var invalidator in _cacheInvalidators) invalidator.Invalidate(change);
    }

    private string GetDatabasePath() => Path.GetFullPath(_options.DatabasePath);

    private static string ValidateTransferPath(string path, string databasePath, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (string.Equals(full, databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Transferdatei darf nicht die aktive Datenbank sein.");
        if (mustExist && !File.Exists(full)) throw new FileNotFoundException("Transferdatei nicht gefunden.", full);
        return full;
    }

    private static async Task VerifyFileAsync(string path, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await VerifyConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';";
        if (Convert.ToInt64(await schemaCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Die Datei ist keine TwitchAI-Datenbank (schema_migrations fehlt).");
    }

    private static async Task VerifyConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die SQLite-Datei ist nicht konsistent: " + result);
    }

    private static async Task ActivateConfigurationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in new[] { "policy_sets", "blacklist_rules", "phrase_rules", "whitelist_phrases" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"UPDATE {table} SET enabled = 1, archived_utc = NULL, row_version = row_version + 1;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var templateCommand = connection.CreateCommand())
        {
            templateCommand.Transaction = transaction;
            templateCommand.CommandText = "UPDATE chat_reply_templates SET enabled = 1, row_version = row_version + 1;";
            await templateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE phrase_rules SET manual_override = 'active';";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
