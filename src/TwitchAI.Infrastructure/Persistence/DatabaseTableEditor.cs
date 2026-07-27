using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public enum StagedDatabaseOperation
{
    Insert,
    Update,
    Archive,
    Restore,
}

public sealed record StagedDatabaseChange(
    string TableName,
    StagedDatabaseOperation Operation,
    long? EntityId,
    long? ExpectedRowVersion,
    IReadOnlyDictionary<string, object?> Values);

public sealed record AppliedDatabaseChange(
    string TableName,
    long EntityId,
    StagedDatabaseOperation Operation,
    long RowVersion);

public sealed record DatabaseEditResult(
    IReadOnlyList<AppliedDatabaseChange> Changes,
    IReadOnlyList<PolicyChangeSet> PolicyChanges);

public sealed class DatabaseConcurrencyException : InvalidOperationException
{
    public DatabaseConcurrencyException(string tableName, long entityId)
        : base($"The row '{tableName}/{entityId}' was changed by another operation.")
    {
    }
}

public interface IDatabaseTableEditor
{
    Task<DatabaseEditResult> ApplyAsync(
        IReadOnlyList<StagedDatabaseChange> changes,
        string changeReason,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseTableEditor : IDatabaseTableEditor, IDisposable
{
    private const int MaximumPolicyPromptCharacters = 16_384;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseCatalog _catalog;
    private readonly IChatTextNormalizer _normalizer;
    private readonly IWhitelistTextNormalizer _whitelistNormalizer;
    private readonly IReadOnlyList<IPolicyCacheInvalidator> _cacheInvalidators;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private bool _disposed;

    public DatabaseTableEditor(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseCatalog catalog,
        IChatTextNormalizer normalizer,
        IWhitelistTextNormalizer whitelistNormalizer,
        IEnumerable<IPolicyCacheInvalidator> cacheInvalidators,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _catalog = catalog;
        _normalizer = normalizer;
        _whitelistNormalizer = whitelistNormalizer;
        _cacheInvalidators = cacheInvalidators.ToArray();
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<DatabaseEditResult> ApplyAsync(
        IReadOnlyList<StagedDatabaseChange> changes,
        string changeReason,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeReason);
        changeReason = changeReason.Trim();
        if (changeReason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changeReason),
                changeReason.Length,
                "The configuration change reason may contain at most 500 characters.");
        }

        if (changes.Count == 0)
        {
            return new DatabaseEditResult([], []);
        }

        if (changes.Count > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changes),
                changes.Count,
                "At most 200 staged changes may be committed together.");
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ApplyCoreAsync(changes, changeReason, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private async Task<DatabaseEditResult> ApplyCoreAsync(
        IReadOnlyList<StagedDatabaseChange> changes,
        string changeReason,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var applied = new List<AppliedDatabaseChange>(changes.Count);
        var affectedScopes = new HashSet<long>();
        var affectedAllowlistBroadcasters = new HashSet<string>(StringComparer.Ordinal);

        foreach (var staged in changes)
        {
            var definition = _catalog.GetRequired(staged.TableName);
            EnsureEditable(definition, staged.Operation);
            var values = NormalizeAndValidate(definition, staged.Values, staged.Operation);
            await EnsureProtectedPolicySetAsync(
                connection,
                transaction,
                definition,
                staged,
                values,
                cancellationToken).ConfigureAwait(false);
            await EnsureProtectedBaselineRuleAsync(
                connection,
                transaction,
                definition,
                staged,
                values,
                cancellationToken).ConfigureAwait(false);

            var appliedChange = staged.Operation switch
            {
                StagedDatabaseOperation.Insert => await InsertAsync(
                    connection,
                    transaction,
                    definition,
                    values,
                    changeReason,
                    cancellationToken).ConfigureAwait(false),
                StagedDatabaseOperation.Update => await UpdateAsync(
                    connection,
                    transaction,
                    definition,
                    staged,
                    values,
                    changeReason,
                    cancellationToken).ConfigureAwait(false),
                StagedDatabaseOperation.Archive => await SetArchivedAsync(
                    connection,
                    transaction,
                    definition,
                    staged,
                    archive: true,
                    changeReason,
                    cancellationToken).ConfigureAwait(false),
                StagedDatabaseOperation.Restore => await SetArchivedAsync(
                    connection,
                    transaction,
                    definition,
                    staged,
                    archive: false,
                    changeReason,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(changes),
                    staged.Operation,
                    "Unsupported staged database operation."),
            };

            applied.Add(appliedChange);
            await CollectAffectedConfigurationAsync(
                connection,
                transaction,
                definition,
                appliedChange.EntityId,
                affectedScopes,
                affectedAllowlistBroadcasters,
                cancellationToken).ConfigureAwait(false);
        }

        var policyChanges = await IncrementAndValidateScopeRevisionsAsync(
            connection,
            transaction,
            affectedScopes,
            cancellationToken).ConfigureAwait(false);

        policyChanges.AddRange(affectedAllowlistBroadcasters.Select(
            broadcasterId => new PolicyChangeSet(
                broadcasterId,
                null,
                null,
                AllowlistChanged: true,
                NewRevision: null)));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var policyChange in policyChanges)
        {
            foreach (var invalidator in _cacheInvalidators)
            {
                invalidator.Invalidate(policyChange);
            }
        }

        return new DatabaseEditResult(applied, policyChanges);
    }

    private IReadOnlyDictionary<string, object?> NormalizeAndValidate(
        DatabaseTableDefinition table,
        IReadOnlyDictionary<string, object?> rawValues,
        StagedDatabaseOperation operation)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        if (operation is StagedDatabaseOperation.Archive or StagedDatabaseOperation.Restore)
        {
            if (rawValues.Count != 0)
            {
                throw new ArgumentException(
                    "Archive and restore operations may not include column values.",
                    nameof(rawValues));
            }

            return rawValues;
        }

        if (rawValues.Count == 0)
        {
            throw new ArgumentException("An insert or update must contain values.", nameof(rawValues));
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var item in rawValues)
        {
            if (!table.EditableColumns.TryGetValue(item.Key, out var column))
            {
                throw new UnauthorizedAccessException(
                    $"Column '{table.Name}.{item.Key}' is not editable.");
            }

            values[item.Key] =
                table.Name == "whitelist_phrases" &&
                item.Key == "subject_value" &&
                item.Value is string exactWhitelistValue
                    ? ValidateExactWhitelistValue(column, exactWhitelistValue)
                    : ValidateValue(column, item.Value);
        }

        if (operation == StagedDatabaseOperation.Insert)
        {
            foreach (var column in table.EditableColumns.Values.Where(item => item.Required))
            {
                if (!values.TryGetValue(column.Name, out var value) || value is null)
                {
                    throw new ArgumentException(
                        $"Required column '{table.Name}.{column.Name}' is missing.",
                        nameof(rawValues));
                }
            }
        }

        if (table.Name == "phrase_rules" && values.TryGetValue("phrase", out var phraseValue))
        {
            var phrase = (string)phraseValue!;
            var normalized = _normalizer.Normalize(phrase);
            if (normalized.Length is < 2 or > 500)
            {
                throw new ArgumentException(
                    "The normalized blacklist phrase must contain 2 to 500 characters.",
                    nameof(rawValues));
            }

            values.Add("normalized_phrase", normalized);
        }

        if (table.Name == "whitelist_phrases" &&
            values.TryGetValue("subject_value", out var subjectValue))
        {
            var subjectKind = values.TryGetValue("subject_kind", out var kind)
                ? (string?)kind
                : null;
            var text = (string)subjectValue!;
            values.Add(
                "normalized_value",
                subjectKind == "user_id" ? text : _whitelistNormalizer.Normalize(text));
        }

        return values;
    }

    private static object? ValidateValue(DatabaseColumnDefinition column, object? rawValue)
    {
        if (rawValue is JsonElement element)
        {
            rawValue = ConvertJsonElement(element);
        }

        if (rawValue is null)
        {
            if (column.Required)
            {
                throw new ArgumentException($"Column '{column.Name}' is required.");
            }

            return null;
        }

        object value = column.Kind switch
        {
            DatabaseColumnKind.Text => Convert.ToString(
                rawValue,
                CultureInfo.InvariantCulture) ?? string.Empty,
            DatabaseColumnKind.WholeNumber => Convert.ToInt64(rawValue, CultureInfo.InvariantCulture),
            DatabaseColumnKind.Real => Convert.ToDouble(rawValue, CultureInfo.InvariantCulture),
            DatabaseColumnKind.Boolean => Convert.ToBoolean(
                rawValue,
                CultureInfo.InvariantCulture) ? 1L : 0L,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column.Kind, null),
        };

        if (value is string text)
        {
            text = text.Trim();
            if (column.Required && text.Length == 0)
            {
                throw new ArgumentException($"Column '{column.Name}' may not be empty.");
            }

            if (column.MaximumLength is { } maximumLength && text.Length > maximumLength)
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' may contain at most {maximumLength} characters.");
            }

            if (column.AllowedValues is not null && !column.AllowedValues.Contains(text))
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' contains unsupported value '{text}'.");
            }

            value = text;
        }

        return value;
    }

    private static string ValidateExactWhitelistValue(
        DatabaseColumnDefinition column,
        string value)
    {
        if (value.Length == 0)
        {
            throw new ArgumentException($"Column '{column.Name}' may not be empty.");
        }

        if (column.MaximumLength is { } maximumLength && value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Column '{column.Name}' may contain at most {maximumLength} characters.");
        }

        return value;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new ArgumentException(
                $"JSON value kind '{element.ValueKind}' is not supported by the table editor."),
        };
    }

    private static async Task<AppliedDatabaseChange> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition table,
        IReadOnlyDictionary<string, object?> values,
        string changeReason,
        CancellationToken cancellationToken)
    {
        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        var insertValues = new Dictionary<string, object?>(values, StringComparer.Ordinal)
        {
            ["created_utc"] = now,
        };
        if (table.UsesRowVersion)
        {
            insertValues["updated_utc"] = now;
            insertValues["row_version"] = 1L;
        }

        var names = insertValues.Keys.ToArray();
        var parameters = names.Select((_, index) => $"@value_{index}").ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             INSERT INTO "{table.Name}"({string.Join(",", names.Select(Quote))})
             VALUES({string.Join(",", parameters)});
             SELECT last_insert_rowid();
             """;
        for (var index = 0; index < names.Length; index++)
        {
            command.Parameters.AddWithValue(
                parameters[index],
                insertValues[names[index]] ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var entityId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        var after = await ReadRowJsonAsync(
            connection,
            transaction,
            table,
            entityId,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            connection,
            transaction,
            table.Name,
            entityId,
            "insert",
            null,
            after,
            changeReason,
            cancellationToken).ConfigureAwait(false);
        return new AppliedDatabaseChange(table.Name, entityId, StagedDatabaseOperation.Insert, 1);
    }

    private static async Task<AppliedDatabaseChange> UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition table,
        StagedDatabaseChange staged,
        IReadOnlyDictionary<string, object?> values,
        string changeReason,
        CancellationToken cancellationToken)
    {
        var entityId = RequireEntityId(staged);
        var expectedVersion = RequireRowVersion(staged);
        var before = await ReadRowJsonAsync(
            connection,
            transaction,
            table,
            entityId,
            cancellationToken).ConfigureAwait(false);

        var names = values.Keys.ToArray();
        var assignments = names.Select((name, index) => $"{Quote(name)} = @value_{index}");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             UPDATE "{table.Name}"
             SET {string.Join(",", assignments)},
                 updated_utc = @updated_utc,
                 row_version = row_version + 1
             WHERE "{table.PrimaryKey}" = @entity_id
               AND row_version = @expected_version
               AND archived_utc IS NULL;
             """;
        for (var index = 0; index < names.Length; index++)
        {
            command.Parameters.AddWithValue($"@value_{index}", values[names[index]] ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("@updated_utc", SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@entity_id", entityId);
        command.Parameters.AddWithValue("@expected_version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DatabaseConcurrencyException(table.Name, entityId);
        }

        var after = await ReadRowJsonAsync(
            connection,
            transaction,
            table,
            entityId,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            connection,
            transaction,
            table.Name,
            entityId,
            "update",
            before,
            after,
            changeReason,
            cancellationToken).ConfigureAwait(false);
        return new AppliedDatabaseChange(
            table.Name,
            entityId,
            StagedDatabaseOperation.Update,
            expectedVersion + 1);
    }

    private static async Task<AppliedDatabaseChange> SetArchivedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition table,
        StagedDatabaseChange staged,
        bool archive,
        string changeReason,
        CancellationToken cancellationToken)
    {
        if (!table.SupportsSoftArchive)
        {
            throw new UnauthorizedAccessException($"Table '{table.Name}' does not support archiving.");
        }

        var entityId = RequireEntityId(staged);
        var expectedVersion = RequireRowVersion(staged);
        var before = await ReadRowJsonAsync(
            connection,
            transaction,
            table,
            entityId,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             UPDATE "{table.Name}"
             SET archived_utc = @archived_utc,
                 enabled = @enabled,
                 updated_utc = @updated_utc,
                 row_version = row_version + 1
             WHERE "{table.PrimaryKey}" = @entity_id
               AND row_version = @expected_version;
             """;
        command.Parameters.AddWithValue(
            "@archived_utc",
            archive
                ? SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow)
                : DBNull.Value);
        command.Parameters.AddWithValue("@enabled", archive ? 0 : 1);
        command.Parameters.AddWithValue("@updated_utc", SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@entity_id", entityId);
        command.Parameters.AddWithValue("@expected_version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DatabaseConcurrencyException(table.Name, entityId);
        }

        var after = await ReadRowJsonAsync(
            connection,
            transaction,
            table,
            entityId,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            connection,
            transaction,
            table.Name,
            entityId,
            archive ? "archive" : "restore",
            before,
            after,
            changeReason,
            cancellationToken).ConfigureAwait(false);
        return new AppliedDatabaseChange(
            table.Name,
            entityId,
            archive ? StagedDatabaseOperation.Archive : StagedDatabaseOperation.Restore,
            expectedVersion + 1);
    }

    private static async Task CollectAffectedConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition table,
        long entityId,
        HashSet<long> affectedScopes,
        HashSet<string> affectedAllowlistBroadcasters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        switch (table.Name)
        {
            case "policy_sets":
                affectedScopes.Add(entityId);
                return;
            case "blacklist_rules":
                command.CommandText =
                    "SELECT scope_id FROM blacklist_rules WHERE rule_id = @entity_id;";
                break;
            case "policy_category_exclusions":
                command.CommandText =
                    """
                    SELECT scope_id
                    FROM policy_category_exclusions
                    WHERE exclusion_id = @entity_id;
                    """;
                break;
            case "phrase_rules":
                command.CommandText =
                    """
                    SELECT r.scope_id
                    FROM phrase_rules p
                    INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                    WHERE p.phrase_id = @entity_id;
                    """;
                break;
            case "whitelist_phrases":
                command.CommandText =
                    """
                    SELECT broadcaster_id
                    FROM whitelist_phrases
                    WHERE allowlist_id = @entity_id;
                    """;
                break;
            default:
                return;
        }

        command.Parameters.AddWithValue("@entity_id", entityId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                $"Unable to resolve configuration ownership for '{table.Name}/{entityId}'.");
        }

        if (table.Name == "whitelist_phrases")
        {
            affectedAllowlistBroadcasters.Add((string)result);
        }
        else
        {
            affectedScopes.Add(Convert.ToInt64(result, CultureInfo.InvariantCulture));
        }
    }

    private static async Task<List<PolicyChangeSet>> IncrementAndValidateScopeRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<long> affectedScopes,
        CancellationToken cancellationToken)
    {
        var changes = new List<PolicyChangeSet>(affectedScopes.Count);
        foreach (var scopeId in affectedScopes)
        {
            await ValidatePolicySizeAsync(connection, transaction, scopeId, cancellationToken)
                .ConfigureAwait(false);

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE policy_sets
                    SET revision = revision + 1,
                        updated_utc = @updated_utc
                    WHERE scope_id = @scope_id;
                    """;
                update.Parameters.AddWithValue("@updated_utc", SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("@scope_id", scopeId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT broadcaster_id, scope_kind, category_id, revision
                FROM policy_sets
                WHERE scope_id = @scope_id;
                """;
            read.Parameters.AddWithValue("@scope_id", scopeId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Policy scope '{scopeId}' no longer exists.");
            }

            changes.Add(new PolicyChangeSet(
                reader.GetString(0),
                reader.GetString(1) == "general"
                    ? PolicyScopeKind.General
                    : PolicyScopeKind.Category,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                AllowlistChanged: false,
                NewRevision: reader.GetInt64(3)));
        }

        return changes;
    }

    private static async Task ValidatePolicySizeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COALESCE(
                SUM(length(r.rule_code) + length(r.prompt_instruction) +
                    length(r.public_reason) + COALESCE(p.phrase_length, 0)),
                0)
            FROM blacklist_rules r
            LEFT JOIN (
                SELECT rule_id, SUM(length(phrase) + 1) AS phrase_length
                FROM phrase_rules
                WHERE enabled = 1 AND archived_utc IS NULL
                GROUP BY rule_id
            ) p ON p.rule_id = r.rule_id
            WHERE r.scope_id = @scope_id
              AND r.enabled = 1
              AND r.archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue("@scope_id", scopeId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var characters = Convert.ToInt32(result, CultureInfo.InvariantCulture);
        if (characters > MaximumPolicyPromptCharacters)
        {
            throw new InvalidOperationException(
                $"Policy scope '{scopeId}' exceeds the {MaximumPolicyPromptCharacters} character prompt limit.");
        }
    }

    private static async Task<string?> ReadRowJsonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition table,
        long entityId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT *
             FROM "{table.Name}"
             WHERE "{table.PrimaryKey}" = @entity_id;
             """;
        command.Parameters.AddWithValue("@entity_id", entityId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
        }

        return JsonSerializer.Serialize(values);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        long entityId,
        string operation,
        string? beforeJson,
        string? afterJson,
        string changeReason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO configuration_audit(
                entity_type, entity_id, operation,
                before_json, after_json, scope_id, change_reason, committed_utc)
            VALUES(
                @entity_type, @entity_id, @operation,
                @before_json, @after_json, NULL, @change_reason, @committed_utc);
            """;
        command.Parameters.AddWithValue("@entity_type", tableName);
        command.Parameters.AddWithValue("@entity_id", entityId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@before_json", SqliteData.DbValue(beforeJson));
        command.Parameters.AddWithValue("@after_json", SqliteData.DbValue(afterJson));
        command.Parameters.AddWithValue("@change_reason", changeReason);
        command.Parameters.AddWithValue("@committed_utc", SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static long RequireEntityId(StagedDatabaseChange staged)
    {
        return staged.EntityId
            ?? throw new ArgumentException(
                $"Operation '{staged.Operation}' requires an entity ID.",
                nameof(staged));
    }

    private static async Task EnsureProtectedPolicySetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition definition,
        StagedDatabaseChange staged,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        if (definition.Name != "policy_sets")
        {
            return;
        }

        if (staged.Operation == StagedDatabaseOperation.Insert)
        {
            var scopeKind = values["scope_kind"] as string;
            var setCode = values["set_code"] as string;
            var categoryId = values.GetValueOrDefault("category_id");
            var isValid = scopeKind == "general" &&
                          setCode == "general" &&
                          categoryId is null ||
                          scopeKind == "category" &&
                          setCode == "backseatgaming" &&
                          categoryId is null;
            if (!isValid)
            {
                throw new InvalidOperationException(
                    "Only the General and Backseatgaming policy-set shapes are supported.");
            }

            return;
        }

        var entityId = RequireEntityId(staged);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT set_code FROM policy_sets WHERE scope_id = @scope_id;";
        command.Parameters.AddWithValue("@scope_id", entityId);
        var setCodeValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (setCodeValue is null or DBNull)
        {
            throw new DatabaseConcurrencyException(definition.Name, entityId);
        }

        if ((string)setCodeValue is "general" or "backseatgaming")
        {
            throw new UnauthorizedAccessException(
                "Mandatory General and Backseatgaming policy sets may not be changed or archived.");
        }
    }

    private static async Task EnsureProtectedBaselineRuleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition definition,
        StagedDatabaseChange staged,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        if (definition.Name != "blacklist_rules" ||
            staged.Operation == StagedDatabaseOperation.Insert)
        {
            return;
        }

        var entityId = RequireEntityId(staged);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                r.scope_id,
                r.rule_code,
                r.archived_utc,
                s.broadcaster_id,
                s.set_code
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE r.rule_id = @rule_id;
            """;
        command.Parameters.AddWithValue("@rule_id", entityId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DatabaseConcurrencyException(definition.Name, entityId);
        }

        var scopeId = reader.GetInt64(0);
        var ruleCode = reader.GetString(1);
        var archived = !reader.IsDBNull(2);
        var broadcasterId = reader.GetString(3);
        var setCode = reader.GetString(4);
        var expectedBaseline = setCode switch
        {
            "general" => GlobalPolicyStorage.GeneralRuleCode,
            "backseatgaming" => GlobalPolicyStorage.BackseatgamingRuleCode,
            _ => null,
        };

        var isActiveGlobalBaseline =
            StringComparer.Ordinal.Equals(
                broadcasterId,
                GlobalPolicyStorage.BroadcasterId) &&
            StringComparer.Ordinal.Equals(ruleCode, expectedBaseline) &&
            !archived;
        if (!isActiveGlobalBaseline)
        {
            return;
        }

        if (staged.Operation == StagedDatabaseOperation.Archive)
        {
            throw new UnauthorizedAccessException(
                $"The mandatory baseline rule '{ruleCode}' may not be archived.");
        }

        if (staged.Operation != StagedDatabaseOperation.Update)
        {
            return;
        }

        var disablesRule =
            values.TryGetValue("enabled", out var enabledValue) &&
            Convert.ToInt64(enabledValue, CultureInfo.InvariantCulture) == 0;
        var recodesRule =
            values.TryGetValue("rule_code", out var ruleCodeValue) &&
            !StringComparer.Ordinal.Equals(ruleCodeValue as string, ruleCode);
        var movesRule =
            values.TryGetValue("scope_id", out var scopeValue) &&
            Convert.ToInt64(scopeValue, CultureInfo.InvariantCulture) != scopeId;
        if (disablesRule || recodesRule || movesRule)
        {
            throw new UnauthorizedAccessException(
                $"The mandatory baseline rule '{ruleCode}' must remain active in its policy set.");
        }
    }

    private static long RequireRowVersion(StagedDatabaseChange staged)
    {
        return staged.ExpectedRowVersion
            ?? throw new ArgumentException(
                $"Operation '{staged.Operation}' requires an expected row version.",
                nameof(staged));
    }

    private static void EnsureEditable(
        DatabaseTableDefinition definition,
        StagedDatabaseOperation operation)
    {
        var permitted = definition.AccessClass == DatabaseTableAccessClass.UserEditable ||
                        (definition.AccessClass == DatabaseTableAccessClass.AppendOnly &&
                         operation == StagedDatabaseOperation.Insert);
        if (!permitted)
        {
            throw new UnauthorizedAccessException(
                $"Table '{definition.Name}' has protected access class '{definition.AccessClass}'.");
        }
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
