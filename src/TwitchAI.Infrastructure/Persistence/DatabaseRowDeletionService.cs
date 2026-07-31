using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;

namespace TwitchAI.Infrastructure.Persistence;

public sealed record DatabaseRowDeletionRequest(string TableName, string RecordId);

public sealed record DatabaseRowDeletionImpact(string TableName, long RowCount);

public sealed record DatabaseRowDeletionPreview(
    DatabaseRowDeletionRequest Request,
    bool CanDelete,
    string? BlockReason,
    string? ConfirmationToken,
    DateTimeOffset? ConfirmationExpiresUtc,
    IReadOnlyList<DatabaseRowDeletionImpact> Impacts);

public sealed record DatabaseRowDeletionResult(
    DatabaseRowDeletionRequest Request,
    IReadOnlyList<DatabaseRowDeletionImpact> DeletedRows,
    long PositiveFeedbackReverted,
    long NegativeFeedbackReverted,
    DateTimeOffset DeletedAtUtc);

public interface IDatabaseRowDeletionService
{
    Task<DatabaseRowDeletionPreview> PreviewAsync(
        DatabaseRowDeletionRequest request,
        CancellationToken cancellationToken = default);

    Task<DatabaseRowDeletionResult> DeleteAsync(
        DatabaseRowDeletionRequest request,
        string confirmationToken,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Physically removes only explicitly allowlisted, closed record packages and
/// bounded operational logs. Configuration and append-only moderation evidence
/// that is not part of a closed case package is deliberately outside this API.
/// </summary>
public sealed class DatabaseRowDeletionService : IDatabaseRowDeletionService
{
    private const string AuditHashDomain = "twitchai.data-deletion-audit.v1";
    private static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationalLogRetention = TimeSpan.FromHours(24);
    private static readonly HashSet<string> ClosedCaseStatuses =
    [
        "executed",
        "discarded",
        "allowlisted",
        "externally_deleted",
        "failed",
        "integrity_blocked",
        "externally_completed",
        "skipped_overload",
    ];

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<IPolicyCacheInvalidator> _cacheInvalidators;
    private readonly ILearningContextRevision _learningContextRevision;
    private readonly object _confirmationGate = new();
    private readonly Dictionary<string, PendingConfirmation> _confirmations =
        new(StringComparer.Ordinal);

    public DatabaseRowDeletionService(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null,
        TimeProvider? timeProvider = null,
        IEnumerable<IPolicyCacheInvalidator>? cacheInvalidators = null,
        ILearningContextRevision? learningContextRevision = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheInvalidators = cacheInvalidators?.ToArray() ?? [];
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
    }

    public async Task<DatabaseRowDeletionPreview> PreviewAsync(
        DatabaseRowDeletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var assessment = await AssessAsync(
                connection,
                transaction: null,
                normalized,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (!assessment.CanDelete)
        {
            return new DatabaseRowDeletionPreview(
                normalized,
                CanDelete: false,
                assessment.BlockReason,
                ConfirmationToken: null,
                ConfirmationExpiresUtc: null,
                assessment.Impacts);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
        var expiresAt = now.Add(ConfirmationLifetime);
        lock (_confirmationGate)
        {
            RemoveExpiredConfirmations(now);
            _confirmations[token] = new PendingConfirmation(
                normalized,
                assessment.Fingerprint,
                now,
                expiresAt);
        }

        return new DatabaseRowDeletionPreview(
            normalized,
            CanDelete: true,
            BlockReason: null,
            token,
            expiresAt,
            assessment.Impacts);
    }

    public async Task<DatabaseRowDeletionResult> DeleteAsync(
        DatabaseRowDeletionRequest request,
        string confirmationToken,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequest(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        var now = _timeProvider.GetUtcNow();
        PendingConfirmation confirmation;
        lock (_confirmationGate)
        {
            RemoveExpiredConfirmations(now);
            if (!_confirmations.Remove(confirmationToken, out confirmation!) ||
                confirmation.ExpiresUtc < now ||
                confirmation.Request != normalized)
            {
                throw new UnauthorizedAccessException(
                    "Die Löschbestätigung ist ungültig oder abgelaufen. Bitte die Vorschau erneut öffnen.");
            }
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await AssessAsync(
                connection,
                transaction,
                normalized,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        if (!current.CanDelete)
        {
            throw new InvalidOperationException(
                current.BlockReason ?? "Der Datensatz darf nicht physisch gelöscht werden.");
        }

        if (!StringComparer.Ordinal.Equals(current.Fingerprint, confirmation.Fingerprint))
        {
            throw new InvalidOperationException(
                "Der Datensatz wurde seit der Vorschau verändert. Bitte die Löschvorschau erneut prüfen.");
        }

        DeletionExecution execution = normalized.TableName switch
        {
            "moderation_cases" => await DeleteCasePackageAsync(
                    connection,
                    transaction,
                    normalized,
                    current,
                    now,
                    cancellationToken)
                .ConfigureAwait(false),
            "ai_run_metrics" or
            "processed_eventsub_events" or
            "message_availability_events" => await DeleteOperationalRowAsync(
                    connection,
                    transaction,
                    normalized,
                    current,
                    now,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw new UnauthorizedAccessException(
                $"Die Tabelle '{normalized.TableName}' ist nicht für physische Löschung freigegeben."),
        };

        var requestId = Guid.NewGuid().ToString("N");
        var countsJson = JsonSerializer.Serialize(
            execution.DeletedRows.ToDictionary(
                item => item.TableName,
                item => item.RowCount,
                StringComparer.Ordinal));
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText =
                """
                INSERT INTO data_deletion_audit(
                    request_id, target_table, target_id_sha256, deletion_kind,
                    deleted_counts_json, positive_feedback_reverted,
                    negative_feedback_reverted, preview_fingerprint,
                    requested_utc, completed_utc)
                VALUES(
                    @request_id, @target_table, @target_id_sha256, @deletion_kind,
                    @deleted_counts_json, @positive_feedback_reverted,
                    @negative_feedback_reverted, @preview_fingerprint,
                    @requested_utc, @completed_utc);
                """;
            audit.Parameters.AddWithValue("@request_id", requestId);
            audit.Parameters.AddWithValue("@target_table", normalized.TableName);
            audit.Parameters.AddWithValue(
                "@target_id_sha256",
                HashTargetIdentifier(normalized));
            audit.Parameters.AddWithValue(
                "@deletion_kind",
                normalized.TableName == "moderation_cases"
                    ? "case_package"
                    : "safe_log");
            audit.Parameters.AddWithValue("@deleted_counts_json", countsJson);
            audit.Parameters.AddWithValue(
                "@positive_feedback_reverted",
                execution.PositiveFeedbackReverted);
            audit.Parameters.AddWithValue(
                "@negative_feedback_reverted",
                execution.NegativeFeedbackReverted);
            audit.Parameters.AddWithValue(
                "@preview_fingerprint",
                confirmation.Fingerprint);
            audit.Parameters.AddWithValue(
                "@requested_utc",
                SqliteData.ToDatabaseTimestamp(confirmation.RequestedUtc));
            audit.Parameters.AddWithValue(
                "@completed_utc",
                SqliteData.ToDatabaseTimestamp(now));
            await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (execution.DeletedRows.Any(item =>
                item.RowCount > 0
                && item.TableName is
                    "manual_learning_examples" or
                    "moderation_feedback" or
                    "moderation_cases" or
                    "chat_message_snapshots" or
                    "learning_example_overrides"))
        {
            _learningContextRevision.Advance();
        }

        if (normalized.TableName == "moderation_cases" &&
            execution.PolicyDataChanged)
        {
            var changeSet = new PolicyChangeSet(
                GlobalPolicyStorage.BroadcasterId,
                ScopeKind: null,
                CategoryId: null,
                AllowlistChanged: false,
                NewRevision: null);
            foreach (var invalidator in _cacheInvalidators)
            {
                invalidator.Invalidate(changeSet);
            }
        }

        return new DatabaseRowDeletionResult(
            normalized,
            execution.DeletedRows,
            execution.PositiveFeedbackReverted,
            execution.NegativeFeedbackReverted,
            now);
    }

    private static async Task<DeletionAssessment> AssessAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseRowDeletionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return request.TableName switch
        {
            "moderation_cases" => await AssessCasePackageAsync(
                    connection,
                    transaction,
                    ParsePositiveInt64(request),
                    cancellationToken)
                .ConfigureAwait(false),
            "ai_run_metrics" => await AssessAiRunMetricAsync(
                    connection,
                    transaction,
                    request,
                    ParsePositiveInt64(request),
                    cancellationToken)
                .ConfigureAwait(false),
            "processed_eventsub_events" => await AssessProcessedEventAsync(
                    connection,
                    transaction,
                    request,
                    now,
                    cancellationToken)
                .ConfigureAwait(false),
            "message_availability_events" => await AssessOrphanAvailabilityAsync(
                    connection,
                    transaction,
                    request,
                    ParsePositiveInt64(request),
                    now,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => Blocked(
                $"Die Tabelle '{request.TableName}' ist nicht für physische Löschung freigegeben."),
        };
    }

    private static async Task<DeletionAssessment> AssessCasePackageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long caseId,
        CancellationToken cancellationToken)
    {
        string twitchMessageId;
        string status;
        long rowVersion;
        string? resolvedUtc;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT twitch_message_id, status, row_version, resolved_utc
                FROM moderation_cases
                WHERE case_id = @case_id;
                """;
            command.Parameters.AddWithValue("@case_id", caseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Blocked("Der Moderationsfall wurde nicht gefunden.");
            }

            twitchMessageId = reader.GetString(0);
            status = reader.GetString(1);
            rowVersion = reader.GetInt64(2);
            resolvedUtc = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        if (!ClosedCaseStatuses.Contains(status) || resolvedUtc is null)
        {
            return Blocked(
                "Nur vollständig abgeschlossene Moderationsfälle dürfen physisch gelöscht werden.");
        }

        var unsafeAttempts = await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM action_attempts
                WHERE case_id = @case_id
                  AND status IN ('intent','outcome_unknown');
                """,
                ("@case_id", caseId),
                cancellationToken)
            .ConfigureAwait(false);
        if (unsafeAttempts > 0)
        {
            return Blocked(
                "Der Fall enthält einen Twitch-Versuch mit unbekanntem oder noch offenem Ergebnis.");
        }

        var pendingAvailability = await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM message_availability_events
                WHERE (case_id = @case_id OR
                       (case_id IS NULL AND twitch_message_id = @message_id))
                  AND learning_state = 'pending_review';
                """,
                ("@case_id", caseId),
                ("@message_id", twitchMessageId),
                cancellationToken)
            .ConfigureAwait(false);
        if (pendingAvailability > 0)
        {
            return Blocked(
                "Der Fall enthält eine noch nicht abgeschlossene Verfügbarkeitsprüfung.");
        }

        var incompleteFeedback = await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM moderation_feedback
                WHERE case_id = @case_id
                  AND review_completed = 0;
                """,
                ("@case_id", caseId),
                cancellationToken)
            .ConfigureAwait(false);
        if (incompleteFeedback > 0)
        {
            return Blocked(
                "Das menschliche Lernfeedback dieses Falls ist noch nicht abgeschlossen.");
        }

        var impacts = new List<DatabaseRowDeletionImpact>();
        await AddImpactAsync(
            impacts,
            connection,
            transaction,
            "moderation_feedback",
            "SELECT COUNT(*) FROM moderation_feedback WHERE case_id = @case_id;",
            caseId,
            cancellationToken).ConfigureAwait(false);
        await AddImpactAsync(
            impacts,
            connection,
            transaction,
            "phrase_feedback",
            "SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id;",
            caseId,
            cancellationToken).ConfigureAwait(false);
        await AddImpactAsync(
            impacts,
            connection,
            transaction,
            "action_attempts",
            "SELECT COUNT(*) FROM action_attempts WHERE case_id = @case_id;",
            caseId,
            cancellationToken).ConfigureAwait(false);
        await AddImpactAsync(
            impacts,
            connection,
            transaction,
            "learning_example_overrides",
            """
            SELECT COUNT(*)
            FROM learning_example_overrides
            WHERE source_kind = 'twitch_moderation'
              AND source_id = @case_id;
            """,
            caseId,
            cancellationToken).ConfigureAwait(false);

        var availabilityCount = await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM message_availability_events
                WHERE case_id = @case_id OR
                      (case_id IS NULL AND twitch_message_id = @message_id);
                """,
                ("@case_id", caseId),
                ("@message_id", twitchMessageId),
                cancellationToken)
            .ConfigureAwait(false);
        AddImpact(impacts, "message_availability_events", availabilityCount);

        var annotationCount = await CountCaseAnnotationsAsync(
                connection,
                transaction,
                caseId,
                twitchMessageId,
                cancellationToken)
            .ConfigureAwait(false);
        AddImpact(impacts, "record_annotations", annotationCount);
        AddImpact(impacts, "moderation_cases", 1);

        var snapshotCount = await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM chat_message_snapshots snapshot
                WHERE snapshot.twitch_message_id = @message_id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM moderation_cases other_case
                      WHERE other_case.twitch_message_id = snapshot.twitch_message_id
                        AND other_case.case_id <> @case_id
                  );
                """,
                ("@message_id", twitchMessageId),
                ("@case_id", caseId),
                cancellationToken)
            .ConfigureAwait(false);
        AddImpact(impacts, "chat_message_snapshots", snapshotCount);

        var feedbackState = await ReadPhraseFeedbackStateAsync(
                connection,
                transaction,
                caseId,
                cancellationToken)
            .ConfigureAwait(false);
        var relatedState = await ReadCaseRelatedStateAsync(
                connection,
                transaction,
                caseId,
                twitchMessageId,
                cancellationToken)
            .ConfigureAwait(false);
        var state = string.Join(
            "|",
            "case",
            caseId.ToString(CultureInfo.InvariantCulture),
            twitchMessageId,
            status,
            rowVersion.ToString(CultureInfo.InvariantCulture),
            resolvedUtc,
            string.Join(",", impacts.Select(item => $"{item.TableName}:{item.RowCount}")),
            feedbackState,
            relatedState);
        return Allowed(impacts, state);
    }

    private static async Task<DeletionAssessment> AssessAiRunMetricAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseRowDeletionRequest request,
        long recordId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                json_object(
                    'request_id', request_id,
                    'correlation_id', correlation_id,
                    'model_id', model_id,
                    'policy_hash', policy_hash,
                    'message_count', message_count,
                    'prompt_tokens', prompt_tokens,
                    'completion_tokens', completion_tokens,
                    'total_tokens', total_tokens,
                    'elapsed_ms', elapsed_ms,
                    'finish_reason', finish_reason,
                    'succeeded', succeeded,
                    'error_code', error_code,
                    'requested_utc', requested_utc)
            FROM ai_run_metrics
            WHERE request_id = @record_id;
            """;
        command.Parameters.AddWithValue("@record_id", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Blocked("Der Betriebslog-Datensatz wurde nicht gefunden.");
        }

        var state = reader.GetString(0);
        return Allowed(
            [new DatabaseRowDeletionImpact(request.TableName, 1)],
            $"{request.TableName}|{state}");
    }

    private static async Task<DeletionAssessment> AssessProcessedEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseRowDeletionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = SqliteData.ToDatabaseTimestamp(now.Subtract(OperationalLogRetention));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT subscription_type, received_utc
            FROM processed_eventsub_events
            WHERE eventsub_message_id = @record_id;
            """;
        command.Parameters.AddWithValue("@record_id", request.RecordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Blocked("Das EventSub-Deduplizierungsereignis wurde nicht gefunden.");
        }

        var subscriptionType = reader.GetString(0);
        var receivedUtc = reader.GetString(1);
        if (string.CompareOrdinal(receivedUtc, cutoff) > 0)
        {
            return Blocked(
                "EventSub-Deduplizierungsdaten dürfen erst nach 24 Stunden gelöscht werden.");
        }

        return Allowed(
            [new DatabaseRowDeletionImpact(request.TableName, 1)],
            $"{request.TableName}|{request.RecordId}|{subscriptionType}|{receivedUtc}");
    }

    private static async Task<DeletionAssessment> AssessOrphanAvailabilityAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DatabaseRowDeletionRequest request,
        long recordId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = SqliteData.ToDatabaseTimestamp(now.Subtract(OperationalLogRetention));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT received_utc, learning_state
            FROM message_availability_events
            WHERE availability_event_id = @record_id
              AND case_id IS NULL;
            """;
        command.Parameters.AddWithValue("@record_id", recordId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Blocked(
                "Nur verwaiste Verfügbarkeitsereignisse dürfen einzeln gelöscht werden.");
        }

        var receivedUtc = reader.GetString(0);
        var learningState = reader.GetString(1);
        if (learningState == "pending_review")
        {
            return Blocked(
                "Eine noch offene Verfügbarkeitsprüfung darf nicht gelöscht werden.");
        }

        if (string.CompareOrdinal(receivedUtc, cutoff) > 0)
        {
            return Blocked(
                "Verwaiste Verfügbarkeitsereignisse dürfen erst nach 24 Stunden gelöscht werden.");
        }

        return Allowed(
            [new DatabaseRowDeletionImpact(request.TableName, 1)],
            $"{request.TableName}|{recordId}|{receivedUtc}|{learningState}");
    }

    private static async Task<DeletionExecution> DeleteCasePackageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseRowDeletionRequest request,
        DeletionAssessment assessment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var caseId = ParsePositiveInt64(request);
        var messageId = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT twitch_message_id FROM moderation_cases WHERE case_id = @id;",
                ("@id", caseId),
                cancellationToken)
            .ConfigureAwait(false);
        var feedback = await ReadFeedbackCounterDeltasAsync(
                connection,
                transaction,
                caseId,
                cancellationToken)
            .ConfigureAwait(false);
        long positiveReverted = 0;
        long negativeReverted = 0;
        var affectedScopes = (await ReadLearningFeedbackScopeIdsAsync(
                connection,
                transaction,
                caseId,
                cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        foreach (var delta in feedback)
        {
            var reverted = await RevertPhraseCounterAsync(
                    connection,
                    transaction,
                    delta,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            positiveReverted += reverted.Positive;
            negativeReverted += reverted.Negative;
            if (reverted.ScopeId is { } scopeId)
            {
                affectedScopes.Add(scopeId);
            }
        }

        foreach (var scopeId in affectedScopes)
        {
            await ExecuteDeleteOrUpdateAsync(
                    connection,
                    transaction,
                    """
                    UPDATE policy_sets
                    SET revision = revision + 1,
                        row_version = row_version + 1,
                        updated_utc = @now
                    WHERE scope_id = @scope_id;
                    """,
                    cancellationToken,
                    ("@now", SqliteData.ToDatabaseTimestamp(now)),
                    ("@scope_id", scopeId))
                .ConfigureAwait(false);
        }

        await DeleteCaseAnnotationsAsync(
                connection,
                transaction,
                caseId,
                messageId,
                cancellationToken)
            .ConfigureAwait(false);
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                "DELETE FROM phrase_feedback WHERE case_id = @case_id;",
                cancellationToken,
                ("@case_id", caseId))
            .ConfigureAwait(false);
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                "DELETE FROM moderation_feedback WHERE case_id = @case_id;",
                cancellationToken,
                ("@case_id", caseId))
            .ConfigureAwait(false);
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                "DELETE FROM action_attempts WHERE case_id = @case_id;",
                cancellationToken,
                ("@case_id", caseId))
            .ConfigureAwait(false);
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                """
                DELETE FROM learning_example_overrides
                WHERE source_kind = 'twitch_moderation'
                  AND source_id = @case_id;
                """,
                cancellationToken,
                ("@case_id", caseId))
            .ConfigureAwait(false);
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                """
                DELETE FROM message_availability_events
                WHERE case_id = @case_id OR
                      (case_id IS NULL AND twitch_message_id = @message_id);
                """,
                cancellationToken,
                ("@case_id", caseId),
                ("@message_id", messageId))
            .ConfigureAwait(false);
        var deletedCase = await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                "DELETE FROM moderation_cases WHERE case_id = @case_id;",
                cancellationToken,
                ("@case_id", caseId))
            .ConfigureAwait(false);
        if (deletedCase != 1)
        {
            throw new InvalidOperationException(
                "Der Moderationsfall wurde während der Löschung verändert.");
        }

        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                """
                DELETE FROM chat_message_snapshots
                WHERE twitch_message_id = @message_id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM moderation_cases
                      WHERE twitch_message_id = @message_id
                  );
                """,
                cancellationToken,
                ("@message_id", messageId))
            .ConfigureAwait(false);

        return new DeletionExecution(
            assessment.Impacts,
            positiveReverted,
            negativeReverted,
            PolicyDataChanged: affectedScopes.Count > 0);
    }

    private static async Task<DeletionExecution> DeleteOperationalRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseRowDeletionRequest request,
        DeletionAssessment assessment,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var cutoff = SqliteData.ToDatabaseTimestamp(now.Subtract(OperationalLogRetention));
        int affected = request.TableName switch
        {
            "ai_run_metrics" => await ExecuteDeleteOrUpdateAsync(
                    connection,
                    transaction,
                    "DELETE FROM ai_run_metrics WHERE request_id = @record_id;",
                    cancellationToken,
                    ("@record_id", ParsePositiveInt64(request)))
                .ConfigureAwait(false),
            "processed_eventsub_events" => await ExecuteDeleteOrUpdateAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM processed_eventsub_events
                    WHERE eventsub_message_id = @record_id
                      AND received_utc <= @cutoff;
                    """,
                    cancellationToken,
                    ("@record_id", request.RecordId),
                    ("@cutoff", cutoff))
                .ConfigureAwait(false),
            "message_availability_events" => await ExecuteDeleteOrUpdateAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM message_availability_events
                    WHERE availability_event_id = @record_id
                      AND case_id IS NULL
                      AND learning_state <> 'pending_review'
                      AND received_utc <= @cutoff;
                    """,
                    cancellationToken,
                    ("@record_id", ParsePositiveInt64(request)),
                    ("@cutoff", cutoff))
                .ConfigureAwait(false),
            _ => 0,
        };
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "Der Betriebslog-Datensatz wurde während der Löschung verändert.");
        }

        return new DeletionExecution(
            assessment.Impacts,
            PositiveFeedbackReverted: 0,
            NegativeFeedbackReverted: 0,
            PolicyDataChanged: false);
    }

    private static async Task<IReadOnlyList<long>> ReadLearningFeedbackScopeIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DISTINCT rule.scope_id
            FROM moderation_feedback feedback
            INNER JOIN moderation_cases moderation_case
                ON moderation_case.case_id = feedback.case_id
            INNER JOIN blacklist_rules rule
                ON rule.rule_code = COALESCE(
                    feedback.rule_code,
                    moderation_case.violation_rule_code)
            INNER JOIN policy_sets policy
                ON policy.scope_id = rule.scope_id
            WHERE feedback.case_id = @case_id
              AND feedback.eligible_for_learning = 1
              AND feedback.review_completed = 1
              AND policy.broadcaster_id = @global_broadcaster_id
              AND policy.archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue(
            "@global_broadcaster_id",
            GlobalPolicyStorage.BroadcasterId);
        var scopeIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopeIds.Add(reader.GetInt64(0));
        }

        return scopeIds;
    }

    private static async Task<IReadOnlyList<FeedbackCounterDelta>>
        ReadFeedbackCounterDeltasAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long caseId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                COALESCE(
                    rule_code,
                    CASE policy_set
                        WHEN 'backseatgaming' THEN 'BACKSEATGAMING'
                        ELSE 'G_OTHER_ABUSE'
                    END),
                trigger_phrase,
                SUM(CASE WHEN is_positive = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN is_positive = 0 THEN 1 ELSE 0 END)
            FROM phrase_feedback
            WHERE case_id = @case_id
              AND eligible = 1
            GROUP BY
                COALESCE(
                    rule_code,
                    CASE policy_set
                        WHEN 'backseatgaming' THEN 'BACKSEATGAMING'
                        ELSE 'G_OTHER_ABUSE'
                    END),
                trigger_phrase;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        var deltas = new List<FeedbackCounterDelta>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            deltas.Add(new FeedbackCounterDelta(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return deltas;
    }

    private static async Task<RevertedCounters> RevertPhraseCounterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FeedbackCounterDelta delta,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long phraseId;
        long scopeId;
        long positive;
        long negative;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT
                    p.phrase_id,
                    r.scope_id,
                    p.positive_confirmations,
                    p.negative_confirmations
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND r.rule_code = @rule_code
                  AND p.normalized_phrase = @trigger_phrase
                ORDER BY
                    CASE
                        WHEN s.archived_utc IS NULL
                         AND r.archived_utc IS NULL
                         AND p.archived_utc IS NULL
                        THEN 0
                        ELSE 1
                    END,
                    p.phrase_id DESC
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("@rule_code", delta.RuleCode);
            select.Parameters.AddWithValue("@trigger_phrase", delta.TriggerPhrase);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new RevertedCounters(0, 0, null);
            }

            phraseId = reader.GetInt64(0);
            scopeId = reader.GetInt64(1);
            positive = reader.GetInt64(2);
            negative = reader.GetInt64(3);
        }

        var revertedPositive = Math.Min(positive, delta.Positive);
        var revertedNegative = Math.Min(negative, delta.Negative);
        if (revertedPositive == 0 && revertedNegative == 0)
        {
            return new RevertedCounters(0, 0, null);
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE phrase_rules
            SET positive_confirmations = MAX(positive_confirmations - @positive, 0),
                negative_confirmations = MAX(negative_confirmations - @negative, 0),
                row_version = row_version + 1,
                updated_utc = @updated_utc
            WHERE phrase_id = @phrase_id;
            """;
        update.Parameters.AddWithValue("@positive", delta.Positive);
        update.Parameters.AddWithValue("@negative", delta.Negative);
        update.Parameters.AddWithValue("@updated_utc", SqliteData.ToDatabaseTimestamp(now));
        update.Parameters.AddWithValue("@phrase_id", phraseId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new RevertedCounters(revertedPositive, revertedNegative, scopeId);
    }

    private static async Task<string> ReadPhraseFeedbackStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                feedback_id,
                COALESCE(rule_code, ''),
                trigger_phrase,
                is_positive,
                eligible
            FROM phrase_feedback
            WHERE case_id = @case_id
            ORDER BY feedback_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        var state = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            state.Append(reader.GetInt64(0))
                .Append(':')
                .Append(reader.GetString(1))
                .Append(':')
                .Append(reader.GetString(2))
                .Append(':')
                .Append(reader.GetInt64(3))
                .Append(':')
                .Append(reader.GetInt64(4))
                .Append(';');
        }

        return state.ToString();
    }

    private static async Task<string> ReadCaseRelatedStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long caseId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT state
            FROM (
                SELECT
                    'snapshot:' || json_object(
                        'eventsub_notification_id', eventsub_notification_id,
                        'identity_fingerprint', identity_fingerprint,
                        'original_sha256', original_sha256,
                        'normalized_sha256', normalized_sha256,
                        'received_utc', received_utc) AS state
                FROM chat_message_snapshots
                WHERE twitch_message_id = @message_id

                UNION ALL

                SELECT
                    'moderation_feedback:' || json_object(
                        'feedback_id', feedback_id,
                        'operator_decision', operator_decision,
                        'actual_action', actual_action,
                        'eligible_for_learning', eligible_for_learning,
                        'human_approved', human_approved,
                        'twitch_executed', twitch_executed,
                        'learning_accepted', learning_accepted,
                        'review_completed', review_completed,
                        'rule_code', rule_code,
                        'decided_utc', decided_utc)
                FROM moderation_feedback
                WHERE case_id = @case_id

                UNION ALL

                SELECT
                    'action_attempt:' || json_object(
                        'execution_id', execution_id,
                        'status', status,
                        'completed_utc', completed_utc,
                        'http_status', http_status,
                        'error_code', error_code,
                        'rule_code', rule_code)
                FROM action_attempts
                WHERE case_id = @case_id

                UNION ALL

                SELECT
                    'availability:' || json_object(
                        'availability_event_id', availability_event_id,
                        'availability', availability,
                        'correlation_kind', correlation_kind,
                        'learning_state', learning_state,
                        'received_utc', received_utc)
                FROM message_availability_events
                WHERE case_id = @case_id OR
                      (case_id IS NULL AND twitch_message_id = @message_id)

                UNION ALL

                SELECT
                    'annotation:' || json_object(
                        'annotation_id', annotation_id,
                        'record_type', record_type,
                        'record_id', record_id,
                        'annotation_kind', annotation_kind,
                        'annotation_text', annotation_text,
                        'created_utc', created_utc)
                FROM record_annotations
                WHERE
                    (record_type = 'moderation_cases'
                     AND record_id = CAST(@case_id AS TEXT))
                    OR
                    (record_type = 'chat_message_snapshots'
                     AND record_id = @message_id)
                    OR
                    (record_type = 'moderation_feedback'
                     AND record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM moderation_feedback
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'phrase_feedback'
                     AND record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM phrase_feedback
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'action_attempts'
                     AND record_id IN (
                         SELECT CAST(execution_id AS TEXT)
                         FROM action_attempts
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'message_availability_events'
                     AND record_id IN (
                         SELECT CAST(availability_event_id AS TEXT)
                         FROM message_availability_events
                         WHERE case_id = @case_id OR
                               (case_id IS NULL AND twitch_message_id = @message_id)))
            )
            ORDER BY state;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@message_id", messageId);
        var state = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            state.Append(reader.GetString(0)).Append(';');
        }

        return state.ToString();
    }

    private static async Task<long> CountCaseAnnotationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long caseId,
        string messageId,
        CancellationToken cancellationToken)
    {
        return await CountAsync(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM record_annotations annotation
                WHERE
                    (annotation.record_type = 'moderation_cases'
                     AND annotation.record_id = CAST(@case_id AS TEXT))
                    OR
                    (annotation.record_type = 'chat_message_snapshots'
                     AND annotation.record_id = @message_id)
                    OR
                    (annotation.record_type = 'moderation_feedback'
                     AND annotation.record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM moderation_feedback
                         WHERE case_id = @case_id))
                    OR
                    (annotation.record_type = 'phrase_feedback'
                     AND annotation.record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM phrase_feedback
                         WHERE case_id = @case_id))
                    OR
                    (annotation.record_type = 'action_attempts'
                     AND annotation.record_id IN (
                         SELECT CAST(execution_id AS TEXT)
                         FROM action_attempts
                         WHERE case_id = @case_id))
                    OR
                    (annotation.record_type = 'message_availability_events'
                     AND annotation.record_id IN (
                         SELECT CAST(availability_event_id AS TEXT)
                         FROM message_availability_events
                         WHERE case_id = @case_id OR
                               (case_id IS NULL AND twitch_message_id = @message_id)));
                """,
                ("@case_id", caseId),
                ("@message_id", messageId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteCaseAnnotationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long caseId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await ExecuteDeleteOrUpdateAsync(
                connection,
                transaction,
                """
                DELETE FROM record_annotations
                WHERE
                    (record_type = 'moderation_cases'
                     AND record_id = CAST(@case_id AS TEXT))
                    OR
                    (record_type = 'chat_message_snapshots'
                     AND record_id = @message_id)
                    OR
                    (record_type = 'moderation_feedback'
                     AND record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM moderation_feedback
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'phrase_feedback'
                     AND record_id IN (
                         SELECT CAST(feedback_id AS TEXT)
                         FROM phrase_feedback
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'action_attempts'
                     AND record_id IN (
                         SELECT CAST(execution_id AS TEXT)
                         FROM action_attempts
                         WHERE case_id = @case_id))
                    OR
                    (record_type = 'message_availability_events'
                     AND record_id IN (
                         SELECT CAST(availability_event_id AS TEXT)
                         FROM message_availability_events
                         WHERE case_id = @case_id OR
                               (case_id IS NULL AND twitch_message_id = @message_id)));
                """,
                cancellationToken,
                ("@case_id", caseId),
                ("@message_id", messageId))
            .ConfigureAwait(false);
    }

    private static async Task AddImpactAsync(
        ICollection<DatabaseRowDeletionImpact> impacts,
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string sql,
        long caseId,
        CancellationToken cancellationToken)
    {
        var count = await CountAsync(
                connection,
                transaction,
                sql,
                ("@case_id", caseId),
                cancellationToken)
            .ConfigureAwait(false);
        AddImpact(impacts, tableName, count);
    }

    private static void AddImpact(
        ICollection<DatabaseRowDeletionImpact> impacts,
        string tableName,
        long count)
    {
        if (count > 0)
        {
            impacts.Add(new DatabaseRowDeletionImpact(tableName, count));
        }
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken)
    {
        return await CountAsync(
                connection,
                transaction,
                sql,
                [parameter],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        (string Name, object Value) first,
        (string Name, object Value) second,
        CancellationToken cancellationToken)
    {
        return await CountAsync(
                connection,
                transaction,
                sql,
                [first, second],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string
            ?? throw new InvalidOperationException("Der Löschdatensatz wurde nicht gefunden.");
    }

    private static async Task<int> ExecuteDeleteOrUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DatabaseRowDeletionRequest NormalizeRequest(
        DatabaseRowDeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecordId);
        return new DatabaseRowDeletionRequest(
            request.TableName.Trim(),
            request.RecordId.Trim());
    }

    private static long ParsePositiveInt64(DatabaseRowDeletionRequest request)
    {
        if (!long.TryParse(
                request.RecordId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var value) ||
            value <= 0)
        {
            throw new ArgumentException(
                $"'{request.RecordId}' ist keine gültige positive ID für '{request.TableName}'.",
                nameof(request));
        }

        return value;
    }

    private static string HashTargetIdentifier(DatabaseRowDeletionRequest request)
    {
        return Hash(
            string.Join(
                "\0",
                AuditHashDomain,
                request.TableName,
                request.RecordId));
    }

    private static DeletionAssessment Allowed(
        IReadOnlyList<DatabaseRowDeletionImpact> impacts,
        string state)
    {
        return new DeletionAssessment(
            CanDelete: true,
            BlockReason: null,
            impacts,
            Hash(state));
    }

    private static DeletionAssessment Blocked(string reason)
    {
        return new DeletionAssessment(
            CanDelete: false,
            reason,
            [],
            Fingerprint: string.Empty);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private void RemoveExpiredConfirmations(DateTimeOffset now)
    {
        foreach (var token in _confirmations
                     .Where(item => item.Value.ExpiresUtc < now)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _confirmations.Remove(token);
        }
    }

    private sealed record PendingConfirmation(
        DatabaseRowDeletionRequest Request,
        string Fingerprint,
        DateTimeOffset RequestedUtc,
        DateTimeOffset ExpiresUtc);

    private sealed record DeletionAssessment(
        bool CanDelete,
        string? BlockReason,
        IReadOnlyList<DatabaseRowDeletionImpact> Impacts,
        string Fingerprint);

    private sealed record DeletionExecution(
        IReadOnlyList<DatabaseRowDeletionImpact> DeletedRows,
        long PositiveFeedbackReverted,
        long NegativeFeedbackReverted,
        bool PolicyDataChanged);

    private sealed record FeedbackCounterDelta(
        string RuleCode,
        string TriggerPhrase,
        long Positive,
        long Negative);

    private sealed record RevertedCounters(long Positive, long Negative, long? ScopeId);
}
