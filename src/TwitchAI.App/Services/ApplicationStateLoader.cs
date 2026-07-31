using System.Globalization;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.App.Services;

public sealed class ApplicationStateLoader
{
    private readonly AppState _state;
    private readonly IModerationCaseRepository _caseRepository;
    private readonly IMessageAvailabilityRegistry _availabilityRegistry;
    private readonly IMessageAvailabilityStore _availabilityStore;
    private readonly IPreviousSessionReviewCleaner _previousSessionReviews;
    private readonly ISqliteConnectionFactory _connectionFactory;

    public ApplicationStateLoader(
        AppState state,
        IModerationCaseRepository caseRepository,
        IMessageAvailabilityRegistry availabilityRegistry,
        IMessageAvailabilityStore availabilityStore,
        IPreviousSessionReviewCleaner previousSessionReviews,
        ISqliteConnectionFactory connectionFactory)
    {
        _state = state;
        _caseRepository = caseRepository;
        _availabilityRegistry = availabilityRegistry;
        _availabilityStore = availabilityStore;
        _previousSessionReviews = previousSessionReviews;
        _connectionFactory = connectionFactory;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // A new app session starts with an empty review queue. Previous pending
        // proposals are closed without Twitch action or learning feedback.
        await _previousSessionReviews.CloseAsync(
            DateTimeOffset.UtcNow,
            cancellationToken);

        var caseIds = await ReadReviewQueueCaseIdsAsync(cancellationToken);
        var queueItems = new List<ModerationCaseItem>(caseIds.Count);
        foreach (var caseId in caseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moderationCase = await _caseRepository.GetAsync(caseId, cancellationToken);
            if (moderationCase is null)
            {
                continue;
            }
            if (!ModerationProposalEligibility.CanBeReviewed(moderationCase))
            {
                continue;
            }

            _availabilityRegistry.Observe(moderationCase.Message);
            var availability = await _availabilityStore.AssessAsync(
                moderationCase.Message.Identity.ChannelId,
                moderationCase.Message.Identity.TwitchMessageId,
                moderationCase.Message.Identity.TwitchUserId,
                moderationCase.Message.Identity.ReceivedAtUtc,
                cancellationToken);
            queueItems.Add(MapQueueItem(moderationCase, availability));
        }

        var auditItems = await ReadAuditItemsAsync(cancellationToken);

        _state.ModerationCases.Clear();
        foreach (var item in queueItems)
        {
            _state.ModerationCases.Add(item);
        }

        _state.AuditItems.Clear();
        foreach (var item in auditItems)
        {
            _state.AuditItems.Add(item);
        }
    }

    private async Task<IReadOnlyList<long>> ReadReviewQueueCaseIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.case_id
            FROM moderation_cases c
            LEFT JOIN moderation_feedback f ON f.case_id = c.case_id
            WHERE NULLIF(TRIM(c.violation_rule_code), '') IS NOT NULL
              AND (
                   c.status = 'pending'
                   OR (
                    c.status = 'externally_completed'
                    AND COALESCE(f.review_completed, 0) = 0
                   )
               )
            ORDER BY c.created_utc DESC, c.case_id DESC;
            """;

        var caseIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            caseIds.Add(reader.GetInt64(0));
        }

        return caseIds;
    }

    private async Task<IReadOnlyList<AuditItem>> ReadAuditItemsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                c.case_id AS audit_id,
                COALESCE(
                    f.decided_utc,
                    a.completed_utc,
                    a.requested_utc,
                    c.resolved_utc,
                    c.created_utc) AS occurred_utc,
                c.chatter_name_snapshot,
                c.message_text_original,
                c.status,
                c.proposed_action,
                c.policy_set,
                COALESCE(c.category_name_snapshot, 'Kategorie unbekannt'),
                f.operator_decision,
                f.actual_action,
                f.twitch_executed,
                f.learning_accepted,
                f.review_completed,
                a.status,
                a.http_status,
                a.error_code,
                COALESCE(f.actual_timeout_seconds, c.proposed_timeout_seconds),
                COALESCE(f.actual_reason, c.reason, '')
            FROM moderation_cases c
            LEFT JOIN moderation_feedback f ON f.case_id = c.case_id
            LEFT JOIN action_attempts a ON a.case_id = c.case_id
            WHERE c.status NOT IN ('pending', 'executing')
              AND NOT (
                    c.status = 'externally_completed'
                    AND COALESCE(f.review_completed, 0) = 0
              )
            ORDER BY occurred_utc DESC, c.case_id DESC
            LIMIT 500;
            """;

        var items = new List<AuditItem>(500);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new AuditRow(
                reader.GetInt64(0),
                ParseTimestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                GetNullableBoolean(reader, 10),
                GetNullableBoolean(reader, 11),
                GetNullableBoolean(reader, 12),
                GetNullableString(reader, 13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                GetNullableString(reader, 15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.GetString(17));
            var decision = GetAuditDecision(row);
            var effectiveAction = row.ActualAction is not null and not "none"
                ? row.ActualAction
                : row.ProposedAction;
            items.Add(
                new AuditItem(
                    row.Id,
                    row.OccurredAt,
                    row.UserName,
                    row.OriginalMessage,
                    decision,
                    GetTwitchResult(row),
                    row.CategoryName,
                    ToUiPolicySet(row.PolicySet),
                    effectiveAction == "timeout" ? row.TimeoutSeconds : null,
                    effectiveAction == "delete" ? string.Empty : row.Reason));
        }

        return items;
    }

    private static ModerationCaseItem MapQueueItem(
        ModerationCase moderationCase,
        MessageAvailabilityAssessment availability)
    {
        var item = new ModerationCaseItem
        {
            CaseId = moderationCase.CaseId,
            ChannelId = moderationCase.Message.Identity.ChannelId,
            UserName = moderationCase.Message.Identity.DisplayName,
            OriginalMessage = moderationCase.Message.Text.OriginalText,
            CategoryName =
                string.IsNullOrWhiteSpace(
                    moderationCase.Message.ChannelContext.CategoryDisplayName)
                    ? "Kategorie unbekannt"
                    : moderationCase.Message.ChannelContext.CategoryDisplayName,
            CategoryId = moderationCase.Message.ChannelContext.CategoryId,
            ReceivedAt = moderationCase.Message.Identity.ReceivedAtUtc,
            MessageId = moderationCase.Message.Identity.TwitchMessageId,
            UserId = moderationCase.Message.Identity.TwitchUserId,
            IdentityFingerprint =
                moderationCase.Message.Identity.IdentityFingerprint,
            RowVersion = moderationCase.RowVersion,
            Action = ToUiAction(moderationCase.ProposedAction),
            TimeoutSeconds = moderationCase.ProposedTimeoutSeconds
                ?? ModerationActionRules.DefaultTimeoutSeconds,
            Reason = ModerationReasonNormalizer.Normalize(
                moderationCase.Reason,
                moderationCase.RuleCode,
                moderationCase.PolicySet),
            PolicySet = ToUiPolicySet(moderationCase.PolicySet),
            RuleCode = moderationCase.RuleCode,
            Confidence = moderationCase.Confidence,
            LocalMatchCodes = moderationCase.LocalMatchCodes,
            AiLatencyMilliseconds = moderationCase.AiLatencyMilliseconds,
            AvailabilityLabel = GetAvailabilityLabel(
                availability.Availability,
                moderationCase.Status),
            Status = moderationCase.Status switch
            {
                CoreModerationCaseStatus.Pending => "Pending",
                CoreModerationCaseStatus.ExternallyCompleted =>
                    "Extern erledigt · Lernentscheidung offen",
                _ => throw new InvalidDataException(
                    $"Status '{moderationCase.Status}' gehört nicht in die Moderationswarteschlange."),
            },
        };

        foreach (var phrase in moderationCase.TriggerPhrases)
        {
            item.TriggerPhrases.Add(new TriggerPhraseItem(phrase));
        }

        return item;
    }

    private static string GetAvailabilityLabel(
        MessageAvailability availability,
        CoreModerationCaseStatus caseStatus)
    {
        return availability switch
        {
            MessageAvailability.ObservedActive
                when caseStatus == CoreModerationCaseStatus.ExternallyCompleted =>
                    "Nicht mehr verfügbar",
            MessageAvailability.ObservedActive => "Nachricht verfügbar",
            MessageAvailability.DeletedExact => "Bereits gelöscht",
            MessageAvailability.ClearedForUser => "Für User entfernt",
            MessageAvailability.RoomClearedHidden =>
                "Durch Chat-Leerung verborgen",
            MessageAvailability.ExpiredNotDeletable =>
                "Älter als sechs Stunden · nicht löschbar",
            MessageAvailability.UnknownAfterEventGap =>
                "Nach EventSub-Lücke nicht sicher",
            MessageAvailability.OutcomeUnknown =>
                "Ergebnis unbekannt · nicht erneut ausführen",
            _ => "Nicht sicher verfügbar",
        };
    }

    private static string ToUiAction(ModerationActionKind action)
    {
        return action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => throw new InvalidDataException(
                $"Aktion '{action}' gehört nicht in die Moderationswarteschlange."),
        };
    }

    private static string ToUiPolicySet(PolicySetCode policySet)
    {
        return policySet switch
        {
            PolicySetCode.General => "General",
            PolicySetCode.Backseatgaming => "Backseatgaming",
            _ => throw new ArgumentOutOfRangeException(nameof(policySet), policySet, null),
        };
    }

    private static string ToUiPolicySet(string policySet)
    {
        return policySet switch
        {
            "general" => "General",
            "backseatgaming" => "Backseatgaming",
            _ => policySet,
        };
    }

    private static string GetAuditDecision(AuditRow row)
    {
        if (row.OperatorDecision == "discarded")
        {
            return "Verworfen";
        }

        if (row.CaseStatus == "discarded")
        {
            return "AI: keine Aktion";
        }

        if (row.CaseStatus == "allowlisted")
        {
            return "Whitelist";
        }

        if (row.CaseStatus is "externally_completed" or "externally_deleted")
        {
            return row.LearningAccepted == true
                ? "Extern bestätigt"
                : "Extern abgeschlossen";
        }

        if (row.CaseStatus == "stale_policy")
        {
            return "Regelstand veraltet";
        }

        if (row.CaseStatus is "integrity_blocked" or "failed")
        {
            return "Nicht ausgeführt";
        }

        if (row.CaseStatus == "outcome_unknown")
        {
            return "Ergebnis unbekannt";
        }

        var action = row.ActualAction is not null and not "none"
            ? row.ActualAction
            : row.ProposedAction;
        return action switch
        {
            "delete" => "Löschen",
            "timeout" => "Timeout",
            "ban" => "Ban",
            _ => "Keine Aktion",
        };
    }

    private static string GetTwitchResult(AuditRow row)
    {
        if (row.TwitchExecuted == true)
        {
            return "Twitch ausgeführt";
        }

        if (row.TwitchExecuted == false
            && row.LearningAccepted == true
            && row.CaseStatus == "executed"
            && row.AttemptStatus is null)
        {
            return "Read-Only · lokal gelernt";
        }

        if (row.TwitchExecuted is null && row.AttemptStatus == "succeeded")
        {
            return "Twitch ausgeführt";
        }

        if (row.CaseStatus == "outcome_unknown"
            || row.AttemptStatus == "outcome_unknown")
        {
            return "Ergebnis unbekannt";
        }

        if (row.AttemptStatus == "failed"
            && row.CaseStatus is not "externally_completed" and not "externally_deleted")
        {
            if (!string.IsNullOrWhiteSpace(row.AttemptErrorCode))
            {
                return $"Nicht ausgeführt · {row.AttemptErrorCode}";
            }

            return row.HttpStatus is { } status
                ? $"Nicht ausgeführt · HTTP {status}"
                : "Twitch-Aktion fehlgeschlagen";
        }

        return "Keine Twitch-Aktion";
    }

    private static string? GetNullableString(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? GetNullableBoolean(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private sealed record AuditRow(
        long Id,
        DateTimeOffset OccurredAt,
        string UserName,
        string OriginalMessage,
        string CaseStatus,
        string ProposedAction,
        string PolicySet,
        string CategoryName,
        string? OperatorDecision,
        string? ActualAction,
        bool? TwitchExecuted,
        bool? LearningAccepted,
        bool? ReviewCompleted,
        string? AttemptStatus,
        int? HttpStatus,
        string? AttemptErrorCode,
        int? TimeoutSeconds,
        string Reason);
}
