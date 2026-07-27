using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public interface IUniversalDatabaseEditor
{
    Task SaveRowAsync(
        string tableName,
        string primaryKeyColumn,
        object? primaryKeyValue,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);

    Task<int> DeleteRowsAsync(
        string tableName,
        string primaryKeyColumn,
        IReadOnlyList<object> primaryKeyValues,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lightweight editor used by the visual database page. SQLite remains the
/// source of truth for types, CHECK constraints and foreign keys; the UI does
/// not maintain a second allowlist of editable business columns.
/// </summary>
public sealed class UniversalDatabaseEditor : IUniversalDatabaseEditor
{
    private static readonly HashSet<string> PolicyTables =
    [
        "blacklist_rules",
        "phrase_rules",
        "policy_sets",
        "policy_category_exclusions",
        "whitelist_phrases",
    ];

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseCatalog _catalog;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IReadOnlyList<IPolicyCacheInvalidator> _cacheInvalidators;
    private readonly ConcurrentDictionary<string, TableSchema> _schemaCache =
        new(StringComparer.Ordinal);

    public UniversalDatabaseEditor(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseCatalog catalog,
        IEnumerable<IPolicyCacheInvalidator> cacheInvalidators,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _catalog = catalog;
        _cacheInvalidators = cacheInvalidators.ToArray();
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task SaveRowAsync(
        string tableName,
        string primaryKeyColumn,
        object? primaryKeyValue,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKeyColumn);
        ArgumentNullException.ThrowIfNull(values);
        _catalog.GetRequired(tableName);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var schema = await GetSchemaAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false);
        EnsurePrimaryKey(schema, primaryKeyColumn);

        if (primaryKeyValue is null)
        {
            await InsertAsync(
                    connection,
                    transaction,
                    schema,
                    values,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await UpdateAsync(
                    connection,
                    transaction,
                    schema,
                    primaryKeyColumn,
                    primaryKeyValue,
                    values,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await TouchPolicyRevisionAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateCaches(tableName);
    }

    public async Task<int> DeleteRowsAsync(
        string tableName,
        string primaryKeyColumn,
        IReadOnlyList<object> primaryKeyValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKeyColumn);
        ArgumentNullException.ThrowIfNull(primaryKeyValues);
        _catalog.GetRequired(tableName);
        var keys = primaryKeyValues
            .Where(value => value is not null)
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
        {
            return 0;
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var schema = await GetSchemaAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false);
        EnsurePrimaryKey(schema, primaryKeyColumn);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var deleted = 0;
        foreach (var key in keys)
        {
            deleted += await DeleteRecursiveAsync(
                    connection,
                    transaction,
                    schema,
                    key,
                    visited,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await TouchPolicyRevisionAsync(
                connection,
                transaction,
                tableName,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        InvalidateCaches(tableName);
        return deleted;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema schema,
        IReadOnlyDictionary<string, object?> rawValues,
        CancellationToken cancellationToken)
    {
        var values = rawValues
            .Where(item =>
                schema.Columns.ContainsKey(item.Key)
                && item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        AddImplicitValues(schema, values);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (values.Count == 0)
        {
            command.CommandText = $"INSERT INTO {Quote(schema.Name)} DEFAULT VALUES;";
        }
        else
        {
            var columnNames = values.Keys.ToArray();
            command.CommandText =
                $"INSERT INTO {Quote(schema.Name)} " +
                $"({string.Join(", ", columnNames.Select(Quote))}) VALUES " +
                $"({string.Join(", ", columnNames.Select((_, index) => $"@v{index}"))});";
            for (var index = 0; index < columnNames.Length; index++)
            {
                command.Parameters.AddWithValue(
                    $"@v{index}",
                    values[columnNames[index]] ?? DBNull.Value);
            }
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema schema,
        string primaryKeyColumn,
        object primaryKeyValue,
        IReadOnlyDictionary<string, object?> rawValues,
        CancellationToken cancellationToken)
    {
        var values = rawValues
            .Where(item =>
                schema.Columns.ContainsKey(item.Key))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return;
        }

        if (schema.Name == "whitelist_phrases"
            && (values.ContainsKey("subject_value")
                || values.ContainsKey("subject_kind")))
        {
            var current = await ReadWhitelistSubjectAsync(
                    connection,
                    transaction,
                    schema,
                    primaryKeyColumn,
                    primaryKeyValue,
                    cancellationToken)
                .ConfigureAwait(false);
            var subjectValue = values.TryGetValue(
                    "subject_value",
                    out var changedSubjectValue)
                ? Convert.ToString(
                    changedSubjectValue,
                    CultureInfo.InvariantCulture) ?? string.Empty
                : current.SubjectValue;
            var subjectKind = values.TryGetValue(
                    "subject_kind",
                    out var changedSubjectKind)
                ? Convert.ToString(
                    changedSubjectKind,
                    CultureInfo.InvariantCulture) ?? string.Empty
                : current.SubjectKind;
            values["normalized_value"] =
                NormalizeWhitelistValue(subjectKind, subjectValue);
        }

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (schema.Columns.ContainsKey("updated_utc")
            && !values.ContainsKey("updated_utc"))
        {
            values["updated_utc"] = now;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var columns = values.Keys.ToArray();
        var assignments = columns
            .Select((column, index) => $"{Quote(column)} = @v{index}")
            .ToList();
        if (schema.Columns.ContainsKey("row_version")
            && !values.ContainsKey("row_version"))
        {
            assignments.Add($"{Quote("row_version")} = {Quote("row_version")} + 1");
        }

        command.CommandText =
            $"UPDATE {Quote(schema.Name)} SET {string.Join(", ", assignments)} " +
            $"WHERE {Quote(primaryKeyColumn)} = @primary_key;";
        for (var index = 0; index < columns.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"@v{index}",
                values[columns[index]] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("@primary_key", primaryKeyValue);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException(
                $"Die Zeile {schema.Name}/{primaryKeyValue} wurde nicht gefunden.");
        }
    }

    private async Task<int> DeleteRecursiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TableSchema schema,
        object primaryKeyValue,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        var identity =
            $"{schema.Name}\0{Convert.ToString(primaryKeyValue, CultureInfo.InvariantCulture)}";
        if (!visited.Add(identity))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var childDefinition in _catalog.Tables)
        {
            var child = await GetSchemaAsync(
                    connection,
                    transaction,
                    childDefinition.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var foreignKey in child.ForeignKeys.Where(item =>
                         StringComparer.Ordinal.Equals(item.TargetTable, schema.Name)
                         && (string.IsNullOrEmpty(item.TargetColumn)
                             || StringComparer.Ordinal.Equals(
                                 item.TargetColumn,
                                 schema.PrimaryKey.Name))))
            {
                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText =
                    $"SELECT {Quote(child.PrimaryKey.Name)} FROM {Quote(child.Name)} " +
                    $"WHERE {Quote(foreignKey.SourceColumn)} = @value;";
                select.Parameters.AddWithValue("@value", primaryKeyValue);
                var childKeys = new List<object>();
                await using (var reader = await select
                                 .ExecuteReaderAsync(cancellationToken)
                                 .ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        childKeys.Add(reader.GetValue(0));
                    }
                }

                foreach (var childKey in childKeys)
                {
                    deleted += await DeleteRecursiveAsync(
                            connection,
                            transaction,
                            child,
                            childKey,
                            visited,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText =
            $"DELETE FROM {Quote(schema.Name)} " +
            $"WHERE {Quote(schema.PrimaryKey.Name)} = @primary_key;";
        delete.Parameters.AddWithValue("@primary_key", primaryKeyValue);
        deleted += await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private async Task<TableSchema> GetSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (_schemaCache.TryGetValue(tableName, out var cached))
        {
            return cached;
        }

        var columns = new Dictionary<string, ColumnSchema>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA table_info({Quote(tableName)});";
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var column = new ColumnSchema(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.GetInt64(3) != 0,
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5) != 0);
                columns[column.Name] = column;
            }
        }

        var primaryKeys = columns.Values.Where(item => item.IsPrimaryKey).ToArray();
        if (primaryKeys.Length != 1)
        {
            throw new InvalidOperationException(
                $"Tabelle '{tableName}' benötigt genau einen Primärschlüssel.");
        }

        var foreignKeys = new List<ForeignKeySchema>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"PRAGMA foreign_key_list({Quote(tableName)});";
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreignKeys.Add(
                    new ForeignKeySchema(
                        reader.GetString(3),
                        reader.GetString(2),
                        reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
            }
        }

        var schema = new TableSchema(
            tableName,
            primaryKeys[0],
            columns,
            foreignKeys);
        return _schemaCache.GetOrAdd(tableName, schema);
    }

    private static void AddImplicitValues(
        TableSchema schema,
        Dictionary<string, object?> values)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        AddIfMissing(schema, values, "created_utc", now);
        AddIfMissing(schema, values, "updated_utc", now);
        AddIfMissing(schema, values, "row_version", 1L);
        AddIfMissing(schema, values, "enabled", 1L);
        AddIfMissing(schema, values, "positive_confirmations", 0L);
        AddIfMissing(schema, values, "negative_confirmations", 0L);
        AddIfMissing(schema, values, "hit_count", 0L);
        AddIfMissing(schema, values, "manual_override", "auto");
        AddIfMissing(schema, values, "broadcaster_id", GlobalPolicyStorage.BroadcasterId);

        if (schema.Name == "phrase_rules"
            && values.TryGetValue("phrase", out var phrase)
            && phrase is string phraseText)
        {
            values["phrase"] = phraseText.ToLowerInvariant();
            values["normalized_phrase"] = phraseText.ToLowerInvariant();
        }

        if (schema.Name == "whitelist_phrases"
            && values.TryGetValue("subject_value", out var whitelist)
            && whitelist is string whitelistText)
        {
            AddIfMissing(schema, values, "subject_kind", "exact_message");
            var subjectKind = Convert.ToString(
                values["subject_kind"],
                CultureInfo.InvariantCulture) ?? string.Empty;
            values["normalized_value"] =
                NormalizeWhitelistValue(subjectKind, whitelistText);
        }
    }

    private static async Task<(string SubjectKind, string SubjectValue)>
        ReadWhitelistSubjectAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            TableSchema schema,
            string primaryKeyColumn,
            object primaryKeyValue,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT {Quote("subject_kind")}, {Quote("subject_value")}
             FROM {Quote(schema.Name)}
             WHERE {Quote(primaryKeyColumn)} = @primary_key;
             """;
        command.Parameters.AddWithValue("@primary_key", primaryKeyValue);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Die Zeile {schema.Name}/{primaryKeyValue} wurde nicht gefunden.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static string NormalizeWhitelistValue(
        string subjectKind,
        string subjectValue) =>
        string.Equals(subjectKind, "user_id", StringComparison.Ordinal)
            ? subjectValue
            : subjectValue.ToLowerInvariant();

    private static void AddIfMissing(
        TableSchema schema,
        Dictionary<string, object?> values,
        string column,
        object value)
    {
        if (schema.Columns.ContainsKey(column)
            && !values.ContainsKey(column))
        {
            values[column] = value;
        }
    }

    private static void EnsurePrimaryKey(TableSchema schema, string primaryKeyColumn)
    {
        if (!StringComparer.Ordinal.Equals(
                schema.PrimaryKey.Name,
                primaryKeyColumn))
        {
            throw new InvalidOperationException(
                $"Ungültiger Primärschlüssel für Tabelle '{schema.Name}'.");
        }
    }

    private static async Task TouchPolicyRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string changedTable,
        CancellationToken cancellationToken)
    {
        if (!PolicyTables.Contains(changedTable))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = @now
            WHERE broadcaster_id = @global_scope
              AND archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "@now",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void InvalidateCaches(string changedTable)
    {
        if (!PolicyTables.Contains(changedTable))
        {
            return;
        }

        var changeSet = new PolicyChangeSet(
            GlobalPolicyStorage.BroadcasterId,
            ScopeKind: null,
            CategoryId: null,
            AllowlistChanged: changedTable == "whitelist_phrases",
            NewRevision: null);
        foreach (var invalidator in _cacheInvalidators)
        {
            invalidator.Invalidate(changeSet);
        }
    }

    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record ColumnSchema(
        string Name,
        string DeclaredType,
        bool IsRequired,
        string? DefaultSql,
        bool IsPrimaryKey);

    private sealed record ForeignKeySchema(
        string SourceColumn,
        string TargetTable,
        string TargetColumn);

    private sealed record TableSchema(
        string Name,
        ColumnSchema PrimaryKey,
        IReadOnlyDictionary<string, ColumnSchema> Columns,
        IReadOnlyList<ForeignKeySchema> ForeignKeys);
}
