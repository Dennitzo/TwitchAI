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

        _state.ModerationCases.Clear();
        foreach (var item in queueItems)
        {
            _state.ModerationCases.Add(item);
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
            ORDER BY c.created_utc ASC, c.case_id ASC;
            """;

        var caseIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            caseIds.Add(reader.GetInt64(0));
        }

        return caseIds;
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
            Reason = moderationCase.Reason,
            ReasonGenerationStatus = moderationCase.ProposedAction is ModerationActionKind.Timeout or ModerationActionKind.Ban
                && string.IsNullOrWhiteSpace(moderationCase.Reason)
                    ? "Begründung erforderlich · bitte manuell ergänzen."
                    : string.Empty,
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

}
