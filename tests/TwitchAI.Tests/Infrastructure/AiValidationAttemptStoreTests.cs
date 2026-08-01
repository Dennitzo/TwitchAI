using System.Text.Json;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class AiValidationAttemptStoreTests
{
    [Fact]
    public async Task Record_PersistsOnlyBoundedDiagnosticMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var writeCoordinator = new SqliteWriteCoordinator();
        var store = new SqliteAiValidationAttemptStore(
            database.ConnectionFactory,
            writeCoordinator);
        const string rawResponse =
            "{\"privateModelOutput\":\"this must never reach sqlite\"}";
        var recordedAt = new DateTimeOffset(
            2026,
            7,
            31,
            20,
            15,
            30,
            TimeSpan.FromHours(2));

        await store.RecordAsync(
            new AiValidationAttempt(
                "batch-42-repair",
                "batch-42",
                AiValidationAttemptKind.Repair,
                AiValidationWorkloadKind.LiveModeration,
                "twitchai.moderation.request.v6",
                "twitchai.moderation.response.v6",
                20,
                "POLICY-HASH",
                ["rule_code_missing", "rule_code_missing", "timeout_invalid"],
                2,
                new ModerationPromptCharacterCounts(
                    System: 420,
                    Rules: 800,
                    Messages: 1_250,
                    Examples: 210,
                    Templates: 0,
                    Schema: 950),
                AiValidationResponseFingerprint.FromRawResponse(rawResponse),
                AiValidationOutcome.Repaired,
                recordedAt));

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM ai_validation_attempts;";
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(reader.GetOrdinal("validation_attempt_id")));
        Assert.Equal("batch-42-repair", reader.GetString(reader.GetOrdinal("correlation_id")));
        Assert.Equal("batch-42", reader.GetString(reader.GetOrdinal("parent_correlation_id")));
        Assert.Equal("repair", reader.GetString(reader.GetOrdinal("attempt_kind")));
        Assert.Equal("live_moderation", reader.GetString(reader.GetOrdinal("workload_kind")));
        Assert.Equal(
            "twitchai.moderation.request.v6",
            reader.GetString(reader.GetOrdinal("contract_version")));
        Assert.Equal(
            "twitchai.moderation.response.v6",
            reader.GetString(reader.GetOrdinal("schema_version")));
        Assert.Equal(20L, reader.GetInt64(reader.GetOrdinal("message_count")));
        Assert.Equal("POLICY-HASH", reader.GetString(reader.GetOrdinal("policy_hash")));
        Assert.Equal(2L, reader.GetInt64(reader.GetOrdinal("affected_message_count")));
        Assert.Equal(420L, reader.GetInt64(reader.GetOrdinal("system_prompt_character_count")));
        Assert.Equal(800L, reader.GetInt64(reader.GetOrdinal("rules_character_count")));
        Assert.Equal(1_250L, reader.GetInt64(reader.GetOrdinal("messages_character_count")));
        Assert.Equal(210L, reader.GetInt64(reader.GetOrdinal("examples_character_count")));
        Assert.Equal(0L, reader.GetInt64(reader.GetOrdinal("templates_character_count")));
        Assert.Equal(950L, reader.GetInt64(reader.GetOrdinal("schema_character_count")));
        Assert.Equal(
            (long)rawResponse.Length,
            reader.GetInt64(reader.GetOrdinal("response_character_count")));
        Assert.Equal(
            AiValidationResponseFingerprint.FromRawResponse(rawResponse).Sha256,
            reader.GetString(reader.GetOrdinal("response_sha256")));
        Assert.Equal("repaired", reader.GetString(reader.GetOrdinal("outcome")));
        Assert.Equal(
            recordedAt.ToUniversalTime().ToString("O"),
            reader.GetString(reader.GetOrdinal("recorded_utc")));
        Assert.False(await reader.ReadAsync());

        var errorCodesJson = await ScalarAsync<string>(
            connection,
            "SELECT validation_error_codes_json FROM ai_validation_attempts;");
        var storedErrorCodes = Assert.IsType<string[]>(
            JsonSerializer.Deserialize<string[]>(errorCodesJson));
        Assert.Equal(
            ["rule_code_missing", "timeout_invalid"],
            storedErrorCodes);

        var persistedText = string.Join(
            "|",
            await ReadTextValuesAsync(connection));
        Assert.DoesNotContain(rawResponse, persistedText, StringComparison.Ordinal);
        Assert.DoesNotContain("privateModelOutput", persistedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Record_RejectsHumanReadableValidationErrorsBeforeWriting()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteAiValidationAttemptStore(database.ConnectionFactory);
        var attempt = CreateAttempt() with
        {
            ValidationErrorCodes = ["The user wrote a secret in this error"],
        };

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.RecordAsync(attempt));

        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                "SELECT COUNT(*) FROM ai_validation_attempts;"));
    }

    [Fact]
    public async Task Record_AllowsAppendOnlyRowsAndPreservesTheirOrder()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteAiValidationAttemptStore(database.ConnectionFactory);

        await store.RecordAsync(CreateAttempt());
        await store.RecordAsync(
            CreateAttempt() with
            {
                CorrelationId = "correlation-2-repair",
                ParentCorrelationId = "correlation-2",
                AttemptKind = AiValidationAttemptKind.Repair,
                Outcome = AiValidationOutcome.Rejected,
            });

        await using var connection = await database.OpenConnectionAsync();
        var rows = await ReadTextColumnAsync(
            connection,
            """
            SELECT validation_attempt_id || '|' || correlation_id
            FROM ai_validation_attempts
            ORDER BY validation_attempt_id;
            """);
        Assert.Equal(
            ["1|correlation-1", "2|correlation-2-repair"],
            rows);
    }

    [Fact]
    public void Catalog_ExposesValidationDiagnosticsAsReadOnlyAudit()
    {
        var definition = new DatabaseCatalog().GetRequired("ai_validation_attempts");

        Assert.Equal(
            DatabaseTableAccessClass.AuditReadOnly,
            definition.AccessClass);
        Assert.Equal("validation_attempt_id", definition.PrimaryKey);
        Assert.Empty(definition.EditableColumns);
        Assert.False(definition.SupportsSoftArchive);
        Assert.False(definition.UsesRowVersion);
    }

    private static AiValidationAttempt CreateAttempt() =>
        new(
            "correlation-1",
            ParentCorrelationId: null,
            AiValidationAttemptKind.Initial,
            AiValidationWorkloadKind.LearningTest,
            "twitchai.learning-test.request.v1",
            "twitchai.learning-test.response.v1",
            1,
            "POLICY-HASH",
            [],
            0,
            new ModerationPromptCharacterCounts(0, 0, 0, 0, 0, 0),
            AiValidationResponseFingerprint.None,
            AiValidationOutcome.Valid,
            DateTimeOffset.UtcNow);

    private static async Task<IReadOnlyList<string>> ReadTextValuesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        return await ReadTextColumnAsync(
            connection,
            """
            SELECT
                correlation_id || '|' ||
                COALESCE(parent_correlation_id, '') || '|' ||
                attempt_kind || '|' || workload_kind || '|' ||
                contract_version || '|' || schema_version || '|' ||
                policy_hash || '|' || validation_error_codes_json || '|' ||
                COALESCE(response_sha256, '') || '|' || outcome || '|' || recorded_utc
            FROM ai_validation_attempts;
            """);
    }

    private static async Task<IReadOnlyList<string>> ReadTextColumnAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<T> ScalarAsync<T>(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)Convert.ChangeType(
            result,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
