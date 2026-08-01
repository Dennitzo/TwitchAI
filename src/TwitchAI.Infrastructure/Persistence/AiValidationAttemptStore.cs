using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class SqliteAiValidationAttemptStore : IAiValidationAttemptStore
{
    private const int MaximumCorrelationIdLength = 128;
    private const int MaximumVersionLength = 128;
    private const int MaximumPolicyHashLength = 256;
    private const int MaximumErrorCodeLength = 64;
    private const int MaximumErrorCodeCount = 64;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public SqliteAiValidationAttemptStore(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async ValueTask RecordAsync(
        AiValidationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Validate(attempt);

        var errorCodes = attempt.ValidationErrorCodes
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var errorCodesJson = JsonSerializer.Serialize(errorCodes);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ai_validation_attempts(
                correlation_id, parent_correlation_id,
                attempt_kind, workload_kind,
                contract_version, schema_version,
                message_count, policy_hash,
                validation_error_codes_json, affected_message_count,
                system_prompt_character_count, rules_character_count,
                messages_character_count, examples_character_count,
                templates_character_count, schema_character_count,
                response_character_count, response_sha256,
                outcome, recorded_utc)
            VALUES(
                @correlation_id, @parent_correlation_id,
                @attempt_kind, @workload_kind,
                @contract_version, @schema_version,
                @message_count, @policy_hash,
                @validation_error_codes_json, @affected_message_count,
                @system_prompt_character_count, @rules_character_count,
                @messages_character_count, @examples_character_count,
                @templates_character_count, @schema_character_count,
                @response_character_count, @response_sha256,
                @outcome, @recorded_utc);
            """;
        command.Parameters.AddWithValue("@correlation_id", attempt.CorrelationId);
        command.Parameters.AddWithValue(
            "@parent_correlation_id",
            SqliteData.DbValue(attempt.ParentCorrelationId));
        command.Parameters.AddWithValue(
            "@attempt_kind",
            ToDatabaseValue(attempt.AttemptKind));
        command.Parameters.AddWithValue(
            "@workload_kind",
            ToDatabaseValue(attempt.WorkloadKind));
        command.Parameters.AddWithValue("@contract_version", attempt.ContractVersion);
        command.Parameters.AddWithValue("@schema_version", attempt.SchemaVersion);
        command.Parameters.AddWithValue("@message_count", attempt.MessageCount);
        command.Parameters.AddWithValue("@policy_hash", attempt.PolicyHash);
        command.Parameters.AddWithValue(
            "@validation_error_codes_json",
            errorCodesJson);
        command.Parameters.AddWithValue(
            "@affected_message_count",
            attempt.AffectedMessageCount);
        command.Parameters.AddWithValue(
            "@system_prompt_character_count",
            attempt.PromptCharacters.System);
        command.Parameters.AddWithValue(
            "@rules_character_count",
            attempt.PromptCharacters.Rules);
        command.Parameters.AddWithValue(
            "@messages_character_count",
            attempt.PromptCharacters.Messages);
        command.Parameters.AddWithValue(
            "@examples_character_count",
            attempt.PromptCharacters.Examples);
        command.Parameters.AddWithValue(
            "@templates_character_count",
            attempt.PromptCharacters.Templates);
        command.Parameters.AddWithValue(
            "@schema_character_count",
            attempt.PromptCharacters.Schema);
        command.Parameters.AddWithValue(
            "@response_character_count",
            attempt.Response.CharacterCount);
        command.Parameters.AddWithValue(
            "@response_sha256",
            SqliteData.DbValue(attempt.Response.Sha256));
        command.Parameters.AddWithValue("@outcome", ToDatabaseValue(attempt.Outcome));
        command.Parameters.AddWithValue(
            "@recorded_utc",
            SqliteData.ToDatabaseTimestamp(attempt.RecordedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(AiValidationAttempt attempt)
    {
        ValidateRequiredText(
            attempt.CorrelationId,
            MaximumCorrelationIdLength,
            nameof(attempt.CorrelationId));
        ValidateOptionalText(
            attempt.ParentCorrelationId,
            MaximumCorrelationIdLength,
            nameof(attempt.ParentCorrelationId));
        ValidateRequiredText(
            attempt.ContractVersion,
            MaximumVersionLength,
            nameof(attempt.ContractVersion));
        ValidateRequiredText(
            attempt.SchemaVersion,
            MaximumVersionLength,
            nameof(attempt.SchemaVersion));
        ValidateRequiredText(
            attempt.PolicyHash,
            MaximumPolicyHashLength,
            nameof(attempt.PolicyHash));

        if (attempt.MessageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt.MessageCount,
                "The attempt message count must be positive.");
        }

        if (attempt.AffectedMessageCount < 0 ||
            attempt.AffectedMessageCount > attempt.MessageCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt.AffectedMessageCount,
                "The affected message count must be between zero and message count.");
        }

        ArgumentNullException.ThrowIfNull(attempt.ValidationErrorCodes);
        if (attempt.ValidationErrorCodes.Count > MaximumErrorCodeCount)
        {
            throw new ArgumentException(
                $"At most {MaximumErrorCodeCount} validation error codes may be recorded.",
                nameof(attempt));
        }

        foreach (var errorCode in attempt.ValidationErrorCodes)
        {
            if (string.IsNullOrWhiteSpace(errorCode) ||
                errorCode.Length > MaximumErrorCodeLength ||
                errorCode.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) ||
                      character is '_' or '-' or '.' or ':')))
            {
                throw new ArgumentException(
                    "Validation diagnostics accept bounded machine-readable error codes only.",
                    nameof(attempt));
            }
        }

        ArgumentNullException.ThrowIfNull(attempt.PromptCharacters);
        ValidateNonNegative(
            attempt.PromptCharacters.System,
            nameof(attempt.PromptCharacters.System));
        ValidateNonNegative(
            attempt.PromptCharacters.Rules,
            nameof(attempt.PromptCharacters.Rules));
        ValidateNonNegative(
            attempt.PromptCharacters.Messages,
            nameof(attempt.PromptCharacters.Messages));
        ValidateNonNegative(
            attempt.PromptCharacters.Examples,
            nameof(attempt.PromptCharacters.Examples));
        ValidateNonNegative(
            attempt.PromptCharacters.Templates,
            nameof(attempt.PromptCharacters.Templates));
        ValidateNonNegative(
            attempt.PromptCharacters.Schema,
            nameof(attempt.PromptCharacters.Schema));

        ArgumentNullException.ThrowIfNull(attempt.Response);
        ValidateNonNegative(
            attempt.Response.CharacterCount,
            nameof(attempt.Response.CharacterCount));
        if (attempt.Response.Sha256 is null)
        {
            if (attempt.Response.CharacterCount != 0)
            {
                throw new ArgumentException(
                    "A non-empty response must have a SHA-256 fingerprint.",
                    nameof(attempt));
            }
        }
        else if (attempt.Response.Sha256.Length != 64 ||
                 attempt.Response.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The response fingerprint must be a hexadecimal SHA-256 value.",
                nameof(attempt));
        }
    }

    private static void ValidateRequiredText(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must contain between 1 and {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateOptionalText(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is not null &&
            (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength))
        {
            throw new ArgumentException(
                $"The optional value must contain between 1 and {maximumLength} characters.",
                parameterName);
        }
    }

    private static void ValidateNonNegative(int value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Character counts cannot be negative.");
        }
    }

    private static string ToDatabaseValue(AiValidationAttemptKind kind) =>
        kind switch
        {
            AiValidationAttemptKind.Initial => "initial",
            AiValidationAttemptKind.Repair => "repair",
            AiValidationAttemptKind.LocalFallback => "local_fallback",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static string ToDatabaseValue(AiValidationWorkloadKind kind) =>
        kind switch
        {
            AiValidationWorkloadKind.LiveModeration => "live_moderation",
            AiValidationWorkloadKind.LearningTest => "learning_test",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static string ToDatabaseValue(AiValidationOutcome outcome) =>
        outcome switch
        {
            AiValidationOutcome.Valid => "valid",
            AiValidationOutcome.Repaired => "repaired",
            AiValidationOutcome.Fallback => "fallback",
            AiValidationOutcome.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
}
