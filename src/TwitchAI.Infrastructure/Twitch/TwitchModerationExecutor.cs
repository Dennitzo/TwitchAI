using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Infrastructure.Twitch;

public sealed class TwitchModerationExecutor : ITwitchModerationExecutor
{
    private readonly IModerationCaseRepository _caseRepository;
    private readonly IMessageAvailabilityStore _availabilityStore;
    private readonly IChannelContextService _channelContextService;
    private readonly IEffectivePolicyResolver _policyResolver;
    private readonly SqlitePolicyStore _policyStore;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly TwitchAI.Infrastructure.Persistence.IChatTextNormalizer _normalizer;
    private readonly ITwitchHelixClient _helixClient;
    private readonly TwitchApiOptions _options;
    private readonly IEventSubProcessingBarrier? _eventSubBarrier;
    private readonly IWhitelistFilterAgent? _whitelistFilter;
    private readonly ITwitchRuntimeModeSettings _runtimeMode;
    private readonly ILearningContextRevision _learningContextRevision;

    public TwitchModerationExecutor(
        IModerationCaseRepository caseRepository,
        IMessageAvailabilityStore availabilityStore,
        IChannelContextService channelContextService,
        IEffectivePolicyResolver policyResolver,
        SqlitePolicyStore policyStore,
        ISqliteConnectionFactory connectionFactory,
        TwitchAI.Infrastructure.Persistence.IChatTextNormalizer normalizer,
        ITwitchHelixClient helixClient,
        InfrastructureOptions options,
        IEventSubProcessingBarrier? eventSubBarrier = null,
        IWhitelistFilterAgent? whitelistFilter = null,
        ISqliteWriteCoordinator? writeCoordinator = null,
        ITwitchRuntimeModeSettings? runtimeMode = null,
        ILearningContextRevision? learningContextRevision = null)
    {
        _caseRepository = caseRepository;
        _availabilityStore = availabilityStore;
        _channelContextService = channelContextService;
        _policyResolver = policyResolver;
        _policyStore = policyStore;
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
        _normalizer = normalizer;
        _helixClient = helixClient;
        _options = options.TwitchApi;
        _eventSubBarrier = eventSubBarrier;
        _whitelistFilter = whitelistFilter;
        _runtimeMode = runtimeMode ?? new TwitchRuntimeModeSettings();
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
    }

    public async ValueTask<ModerationExecutionResult> ExecuteAsync(
        ModerationExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Rejected("invalid_request", validationError);
        }

        var moderationCase = await _caseRepository.GetAsync(request.CaseId, cancellationToken)
            .ConfigureAwait(false);
        if (moderationCase is null)
        {
            return Rejected("case_not_found", "Der Moderationsfall wurde nicht gefunden.");
        }

        if (moderationCase.Status != CoreModerationCaseStatus.Pending ||
            moderationCase.RowVersion != request.ExpectedRowVersion)
        {
            return Rejected(
                "case_changed",
                "Der Moderationsfall wurde bereits geändert oder verarbeitet.");
        }

        if (!ChatMessageIdentityFingerprint.IsValid(moderationCase.Message.Identity) ||
            !await PersistedIdentityMatchesAsync(moderationCase, cancellationToken)
                .ConfigureAwait(false))
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.IntegrityBlocked,
                ModerationExecutionOutcome.IntegrityBlocked,
                "identity_mismatch",
                "Die unveränderliche Twitch-Nachrichtenidentität stimmt nicht mehr überein.",
                cancellationToken).ConfigureAwait(false);
        }

        if (!AcceptedPhrasesAreBoundToCase(moderationCase, request))
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.IntegrityBlocked,
                ModerationExecutionOutcome.IntegrityBlocked,
                "trigger_phrase_mismatch",
                "Mindestens eine bestätigte Trigger-Phrase gehört nicht zum Moderationsfall.",
                cancellationToken).ConfigureAwait(false);
        }

        if (TwitchChatBadgeRoles.HasAutomaticModerationBypass(
                moderationCase.Message.Identity,
                _options))
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.IntegrityBlocked,
                ModerationExecutionOutcome.IntegrityBlocked,
                "sender_role_whitelisted",
                "Der Absender ist Broadcaster, Moderator, VIP oder erkannter Chat-Bot. Die Twitch-Aktion wurde blockiert.",
                cancellationToken).ConfigureAwait(false);
        }

        if (_whitelistFilter is not null &&
            await _whitelistFilter
                .ShouldSkipAsync(
                    moderationCase.Message,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.IntegrityBlocked,
                ModerationExecutionOutcome.IntegrityBlocked,
                "message_now_whitelisted",
                "Die Nachricht ist inzwischen whitelisted und wurde deshalb nicht an Twitch gesendet.",
                cancellationToken).ConfigureAwait(false);
        }

        var enteredAsReadOnly = _runtimeMode.IsReadOnly;
        if (!enteredAsReadOnly)
        {
            var availability = await _availabilityStore.AssessAsync(
                moderationCase.Message.Identity.ChannelId,
                moderationCase.Message.Identity.TwitchMessageId,
                moderationCase.Message.Identity.TwitchUserId,
                moderationCase.Message.Identity.ReceivedAtUtc,
                cancellationToken).ConfigureAwait(false);
            var unavailableResult = await CompleteIfUnavailableAsync(
                moderationCase,
                request,
                availability,
                cancellationToken).ConfigureAwait(false);
            if (unavailableResult is not null)
            {
                return unavailableResult;
            }

            if (_eventSubBarrier is not null)
            {
                try
                {
                    await _eventSubBarrier
                        .DrainAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return Rejected(
                        "eventsub_barrier_failed",
                        "EventSub ist noch nicht aktiv oder nicht vollständig synchronisiert. "
                        + "Bitte die Analyse starten und die Verbindung abwarten. "
                        + $"Details: {exception.Message}");
                }
            }
        }

        var currentContext = await _channelContextService.GetCurrentAsync(
            moderationCase.Message.Identity.ChannelId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                currentContext.ChannelId,
                moderationCase.Message.Identity.ChannelId,
                StringComparison.Ordinal))
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.IntegrityBlocked,
                ModerationExecutionOutcome.IntegrityBlocked,
                "channel_identity_mismatch",
                "Der aktuelle Twitch-Kanal stimmt nicht mit dem Moderationsfall überein.",
                cancellationToken).ConfigureAwait(false);
        }

        // The category snapshot belongs to the received message and remains immutable.
        // Only configuration revisions are refreshed before execution.
        var currentPolicy = await GetCurrentPolicyAsync(
                moderationCase.Message.ChannelContext,
                cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                currentPolicy.RevisionFingerprint,
                moderationCase.PolicyRevisionFingerprint) ||
            !currentPolicy.Contains(request.PolicySet) ||
            currentPolicy.FindRule(request.RuleCode) is not { } selectedRule ||
            selectedRule.PolicySet != request.PolicySet)
        {
            return await CompleteWithoutMutationAsync(
                moderationCase,
                request,
                CoreModerationCaseStatus.StalePolicy,
                ModerationExecutionOutcome.StalePolicy,
                "policy_changed",
                "Die Moderationsregeln oder der Kanalkontext haben sich geändert.",
                cancellationToken).ConfigureAwait(false);
        }

        var executeReadOnly = enteredAsReadOnly || _runtimeMode.IsReadOnly;
        if (executeReadOnly)
        {
            return await CompleteReadOnlyApprovalAsync(
                moderationCase,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        var currentAvailability = await _availabilityStore.AssessAsync(
            moderationCase.Message.Identity.ChannelId,
            moderationCase.Message.Identity.TwitchMessageId,
            moderationCase.Message.Identity.TwitchUserId,
            moderationCase.Message.Identity.ReceivedAtUtc,
            cancellationToken).ConfigureAwait(false);
        var currentUnavailableResult = await CompleteIfUnavailableAsync(
            moderationCase,
            request,
            currentAvailability,
            cancellationToken).ConfigureAwait(false);
        if (currentUnavailableResult is not null)
        {
            return currentUnavailableResult;
        }

        // Twitch restricts Get Banned Users to a broadcaster token for the
        // requested channel. A moderator token may still execute the mutation,
        // so only perform this additional preflight for the moderator's own
        // channel and otherwise rely on EventSub plus the mutation response.
        if (request.Action is ModerationActionKind.Timeout or ModerationActionKind.Ban
            && string.Equals(
                moderationCase.Message.Identity.ChannelId,
                _options.ModeratorUserId,
                StringComparison.Ordinal))
        {
            TwitchBannedUserInfo? existingBan;
            try
            {
                existingBan = await _helixClient.GetBannedUserAsync(
                    moderationCase.Message.Identity.ChannelId,
                    moderationCase.Message.Identity.TwitchUserId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                return Rejected(
                    "ban_lookup_failed",
                    $"Der bestehende Ban-/Timeoutstatus konnte nicht sicher geprüft werden: {exception.Message}");
            }

            var permanentBanExists = existingBan is not null && existingBan.ExpiresAt is null;
            var activeTimeoutExists = existingBan?.ExpiresAt > DateTimeOffset.UtcNow;
            var actionAlreadyApplied =
                permanentBanExists ||
                (request.Action == ModerationActionKind.Timeout && activeTimeoutExists);
            if (actionAlreadyApplied)
            {
                return await CompleteWithoutMutationAsync(
                    moderationCase,
                    request,
                    CoreModerationCaseStatus.ExternallyCompleted,
                    ModerationExecutionOutcome.AlreadyUnavailable,
                    "user_already_banned",
                    "Der Benutzer ist bereits gebannt oder getimeoutet.",
                cancellationToken).ConfigureAwait(false);
            }
        }

        // Re-check immediately before creating a Twitch execution intent. If
        // the operator enabled Read-Only while the preflight was running, the
        // pending case is completed locally and no remote attempt is opened.
        if (_runtimeMode.IsReadOnly)
        {
            return await CompleteReadOnlyApprovalAsync(
                moderationCase,
                request,
                cancellationToken).ConfigureAwait(false);
        }

        var intent = await TryBeginAttemptAsync(
            moderationCase,
            request,
            cancellationToken).ConfigureAwait(false);
        if (intent is null)
        {
            return Rejected(
                "action_already_started",
                "Für diesen Moderationsfall existiert bereits ein Ausführungsversuch.");
        }

        var moderatorId = _options.ModeratorUserId;
        if (string.IsNullOrWhiteSpace(moderatorId))
        {
            return await CompleteAttemptAsync(
                intent.Value,
                moderationCase,
                request,
                AttemptCompletion.Failed(
                    null,
                    "moderator_id_missing",
                    "Es ist keine Twitch-Moderator-ID konfiguriert."),
                CancellationToken.None).ConfigureAwait(false);
        }

        AttemptCompletion completion;
        try
        {
            switch (request.Action)
            {
                case ModerationActionKind.Delete:
                    await _helixClient.DeleteMessageAsync(
                        moderationCase.Message.Identity.ChannelId,
                        moderatorId,
                        moderationCase.Message.Identity.TwitchMessageId,
                        cancellationToken).ConfigureAwait(false);
                    await TryRecordOwnDeleteAsync(
                        intent.Value,
                        moderationCase,
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                case ModerationActionKind.Timeout:
                    await _helixClient.TimeoutUserAsync(
                        moderationCase.Message.Identity.ChannelId,
                        moderatorId,
                        moderationCase.Message.Identity.TwitchUserId,
                        request.TimeoutSeconds!.Value,
                        request.Reason,
                        cancellationToken).ConfigureAwait(false);
                    await TryRecordOwnUserClearAsync(
                        "timeout",
                        intent.Value,
                        moderationCase,
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                case ModerationActionKind.Ban:
                    await _helixClient.BanUserAsync(
                        moderationCase.Message.Identity.ChannelId,
                        moderatorId,
                        moderationCase.Message.Identity.TwitchUserId,
                        request.Reason,
                        cancellationToken).ConfigureAwait(false);
                    await TryRecordOwnUserClearAsync(
                        "ban",
                        intent.Value,
                        moderationCase,
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported moderation action '{request.Action}'.");
            }

            completion = AttemptCompletion.Succeeded();
        }
        catch (TwitchHelixException exception) when (
            exception.StatusCode == HttpStatusCode.NotFound &&
            request.Action == ModerationActionKind.Delete &&
            DateTimeOffset.UtcNow - moderationCase.Message.Identity.ReceivedAtUtc <
                TimeSpan.FromHours(6))
        {
            await TryRecordOwnDeleteAsync(
                intent.Value,
                moderationCase,
                CancellationToken.None).ConfigureAwait(false);
            completion = AttemptCompletion.AlreadyUnavailable(
                (int)exception.StatusCode.Value,
                "no_longer_available_404",
                exception.ResponseBody ?? exception.Message);
        }
        catch (TwitchHelixException exception)
        {
            completion = AttemptCompletion.Failed(
                (int?)exception.StatusCode,
                $"twitch_http_{(int?)exception.StatusCode}",
                exception.ResponseBody ?? exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            completion = AttemptCompletion.OutcomeUnknown(
                "request_cancelled_after_intent",
                exception.Message);
        }
        catch (HttpRequestException exception)
        {
            completion = AttemptCompletion.OutcomeUnknown(
                "transport_result_unknown",
                exception.Message);
        }
        catch (Exception exception)
        {
            completion = AttemptCompletion.OutcomeUnknown(
                "unexpected_result_unknown",
                exception.Message);
        }

        return await CompleteAttemptAsync(
            intent.Value,
            moderationCase,
            request,
            completion,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string? ValidateRequest(ModerationExecutionRequest request)
    {
        if (request.CaseId <= 0 || request.ExpectedRowVersion <= 0)
        {
            return "Case-ID und Row-Version müssen positiv sein.";
        }

        if (request.Action is not (
            ModerationActionKind.Delete
            or ModerationActionKind.Timeout
            or ModerationActionKind.Ban))
        {
            return "Nur Löschen, Timeout oder Ban können ausgeführt werden.";
        }

        if (string.IsNullOrWhiteSpace(request.RuleCode))
        {
            return "Für die Moderationsaktion muss eine Regelkategorie ausgewählt sein.";
        }

        if (request.Action is ModerationActionKind.Timeout or ModerationActionKind.Ban &&
            (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 120))
        {
            return "Der Twitch-Grund muss 1 bis 120 Zeichen enthalten.";
        }

        if (request.Action == ModerationActionKind.Delete &&
            !string.IsNullOrEmpty(request.Reason))
        {
            return "Eine Löschaktion darf keinen Twitch-Grund enthalten.";
        }

        if (request.Action == ModerationActionKind.Timeout &&
            request.TimeoutSeconds is not (>= 1 and <= 1_209_600))
        {
            return "Die Timeout-Dauer muss zwischen 1 und 1.209.600 Sekunden liegen.";
        }

        if (request.Action == ModerationActionKind.Delete && request.TimeoutSeconds is not null)
        {
            return "Eine Löschaktion darf keine Timeout-Dauer enthalten.";
        }

        if (request.Action == ModerationActionKind.Ban && request.TimeoutSeconds is not null)
        {
            return "Ein Ban darf keine Timeout-Dauer enthalten.";
        }

        return null;
    }

    private async Task<CoreEffectivePolicySnapshot> GetCurrentPolicyAsync(
        ChannelContextSnapshot context,
        CancellationToken cancellationToken)
    {
        var stored = await _policyStore
            .GetEffectivePolicyAsync(
                context.ChannelId,
                context.HasKnownCategory ? context.CategoryId : null,
                cancellationToken)
            .ConfigureAwait(false);
        var policyContext =
            context.HasKnownCategory &&
            string.Equals(
                stored.CategoryId,
                context.CategoryId,
                StringComparison.Ordinal)
                ? context
                : context with { IsVerified = false };
        var resolved = _policyResolver.Resolve(
            policyContext,
            Math.Max(1, stored.GeneralRevision),
            Math.Max(1, stored.CategoryRevision));
        return resolved with
        {
            ActiveRules = stored.Rules
                .Select(rule => new ModerationRuleDefinition(
                    rule.RuleCode,
                    string.Equals(
                        rule.RuleCode,
                        ModerationRuleCodes.Backseatgaming,
                        StringComparison.Ordinal)
                        ? PolicySetCode.Backseatgaming
                        : PolicySetCode.General,
                    rule.Title,
                    rule.PromptInstruction,
                    rule.DefaultAction == ModerationRuleAction.Timeout
                        ? ModerationActionKind.Timeout
                        : ModerationActionKind.Delete,
                    rule.DefaultTimeoutSeconds,
                    rule.PublicReason,
                    rule.Priority,
                    ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode)))
                .Where(rule => resolved.Contains(rule.PolicySet))
                .OrderBy(rule => rule.Priority)
                .ThenBy(rule => rule.RuleCode, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private async Task<bool> PersistedIdentityMatchesAsync(
        ModerationCase moderationCase,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT identity_fingerprint
            FROM chat_message_snapshots
            WHERE twitch_message_id = @message_id
              AND broadcaster_id = @broadcaster_id
              AND chatter_user_id = @user_id;
            """;
        command.Parameters.AddWithValue(
            "@message_id",
            moderationCase.Message.Identity.TwitchMessageId);
        command.Parameters.AddWithValue(
            "@broadcaster_id",
            moderationCase.Message.Identity.ChannelId);
        command.Parameters.AddWithValue(
            "@user_id",
            moderationCase.Message.Identity.TwitchUserId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return StringComparer.Ordinal.Equals(
            result as string,
            moderationCase.Message.Identity.IdentityFingerprint);
    }

    private bool AcceptedPhrasesAreBoundToCase(
        ModerationCase moderationCase,
        ModerationExecutionRequest request)
    {
        var proposed = moderationCase.TriggerPhrases
            .Select(_normalizer.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        return request.AcceptedTriggerPhrases
            .Select(_normalizer.Normalize)
            .All(proposed.Contains);
    }

    private async Task<long?> TryBeginAttemptAsync(
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var transition = connection.CreateCommand())
        {
            transition.Transaction = transaction;
            transition.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'executing',
                    row_version = row_version + 1,
                    proposed_action = @approved_action,
                    proposed_timeout_seconds = @approved_timeout_seconds,
                    reason = @approved_reason,
                    policy_set = @approved_policy_set,
                    violation_rule_code = @approved_rule_code,
                    trigger_phrases_json = @approved_triggers
                WHERE case_id = @case_id
                  AND row_version = @expected_version
                  AND status = 'pending';
                """;
            transition.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            transition.Parameters.AddWithValue("@expected_version", request.ExpectedRowVersion);
            transition.Parameters.AddWithValue(
                "@approved_action",
                ToDatabaseAction(request.Action));
            transition.Parameters.AddWithValue(
                "@approved_timeout_seconds",
                SqliteData.DbValue(request.TimeoutSeconds));
            transition.Parameters.AddWithValue("@approved_reason", request.Reason);
            transition.Parameters.AddWithValue(
                "@approved_policy_set",
                ToDatabasePolicySet(request.PolicySet));
            transition.Parameters.AddWithValue("@approved_rule_code", request.RuleCode!);
            transition.Parameters.AddWithValue(
                "@approved_triggers",
                JsonSerializer.Serialize(request.AcceptedTriggerPhrases));
            if (await transition.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO action_attempts(
                    case_id, broadcaster_id, twitch_message_id, target_user_id,
                    action, timeout_seconds, reason, rule_code, status, requested_utc)
                VALUES(
                    @case_id, @broadcaster_id, @message_id, @target_user_id,
                    @action, @timeout_seconds, @reason, @rule_code, 'intent', @requested_utc);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            insert.Parameters.AddWithValue(
                "@broadcaster_id",
                moderationCase.Message.Identity.ChannelId);
            insert.Parameters.AddWithValue(
                "@message_id",
                moderationCase.Message.Identity.TwitchMessageId);
            insert.Parameters.AddWithValue(
                "@target_user_id",
                moderationCase.Message.Identity.TwitchUserId);
            insert.Parameters.AddWithValue("@action", ToDatabaseAction(request.Action));
            insert.Parameters.AddWithValue(
                "@timeout_seconds",
                SqliteData.DbValue(request.TimeoutSeconds));
            insert.Parameters.AddWithValue(
                "@reason",
                request.Action is ModerationActionKind.Timeout or ModerationActionKind.Ban
                    ? request.Reason
                    : DBNull.Value);
            insert.Parameters.AddWithValue("@rule_code", request.RuleCode!);
            insert.Parameters.AddWithValue(
                "@requested_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            var result = await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var executionId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return executionId;
        }
        catch (SqliteException exception) when (
            exception.SqliteErrorCode == 19 ||
            exception.SqliteExtendedErrorCode == 2067)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<ModerationExecutionResult?> CompleteIfUnavailableAsync(
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        MessageAvailabilityAssessment availability,
        CancellationToken cancellationToken)
    {
        if (availability.CanExecuteTwitchAction)
        {
            return null;
        }

        var isReviewableUnavailable = availability.Availability is
            MessageAvailability.DeletedExact or
            MessageAvailability.ClearedForUser or
            MessageAvailability.RoomClearedHidden or
            MessageAvailability.ExpiredNotDeletable or
            MessageAvailability.UnknownAfterEventGap;
        return await CompleteWithoutMutationAsync(
            moderationCase,
            request,
            isReviewableUnavailable
                ? CoreModerationCaseStatus.ExternallyCompleted
                : CoreModerationCaseStatus.IntegrityBlocked,
            isReviewableUnavailable
                ? ModerationExecutionOutcome.AlreadyUnavailable
                : ModerationExecutionOutcome.IntegrityBlocked,
            availability.EvidenceCode,
            isReviewableUnavailable
                ? "Die Nachricht wurde bereits moderiert oder ist nach einer EventSub-Lücke nicht mehr sicher verfügbar."
                : "Die EventSub-Lage ist unklar; eine Twitch-Aktion wurde blockiert.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModerationExecutionResult> CompleteWithoutMutationAsync(
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        CoreModerationCaseStatus status,
        ModerationExecutionOutcome outcome,
        string statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            var persistsApprovedReview =
                status == CoreModerationCaseStatus.ExternallyCompleted;
            update.CommandText =
                """
                UPDATE moderation_cases
                SET status = @status,
                    row_version = row_version + 1,
                    resolved_utc = @resolved_utc,
                    proposed_action = CASE
                        WHEN @persist_review = 1 THEN @approved_action
                        ELSE proposed_action
                    END,
                    proposed_timeout_seconds = CASE
                        WHEN @persist_review = 1 THEN @approved_timeout_seconds
                        ELSE proposed_timeout_seconds
                    END,
                    reason = CASE
                        WHEN @persist_review = 1 THEN @approved_reason
                        ELSE reason
                    END,
                    policy_set = CASE
                        WHEN @persist_review = 1 THEN @approved_policy_set
                        ELSE policy_set
                    END,
                    violation_rule_code = CASE
                        WHEN @persist_review = 1 THEN @approved_rule_code
                        ELSE violation_rule_code
                    END,
                    trigger_phrases_json = CASE
                        WHEN @persist_review = 1 THEN @approved_triggers
                        ELSE trigger_phrases_json
                    END
                WHERE case_id = @case_id
                  AND row_version = @expected_version
                  AND status = 'pending';
                """;
            update.Parameters.AddWithValue("@status", ToDatabaseStatus(status));
            update.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            update.Parameters.AddWithValue("@expected_version", request.ExpectedRowVersion);
            update.Parameters.AddWithValue(
                "@resolved_utc",
                SqliteData.ToDatabaseTimestamp(now));
            update.Parameters.AddWithValue(
                "@persist_review",
                persistsApprovedReview ? 1 : 0);
            update.Parameters.AddWithValue(
                "@approved_action",
                ToDatabaseAction(request.Action));
            update.Parameters.AddWithValue(
                "@approved_timeout_seconds",
                SqliteData.DbValue(request.TimeoutSeconds));
            update.Parameters.AddWithValue("@approved_reason", request.Reason);
            update.Parameters.AddWithValue(
                "@approved_policy_set",
                ToDatabasePolicySet(request.PolicySet));
            update.Parameters.AddWithValue("@approved_rule_code", request.RuleCode!);
            update.Parameters.AddWithValue(
                "@approved_triggers",
                JsonSerializer.Serialize(request.AcceptedTriggerPhrases));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Rejected(
                    "case_changed",
                    "Der Moderationsfall wurde parallel geändert.");
            }
        }

        // StalePolicy is a temporary re-analysis state, not a completed human
        // decision. Persisting feedback here would both pollute retrieval and
        // collide with the later terminal feedback for the refreshed case.
        if (outcome != ModerationExecutionOutcome.StalePolicy)
        {
            await InsertModerationFeedbackAsync(
                connection,
                transaction,
                moderationCase.CaseId,
                actualAction: "none",
                actualTimeoutSeconds: null,
                request.Reason,
                request.RuleCode!,
                twitchExecuted: false,
                learningAccepted: false,
                eligibleForLearning: false,
                reviewCompleted: outcome != ModerationExecutionOutcome.AlreadyUnavailable,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ModerationExecutionResult(
            outcome,
            HumanApproved: true,
            TwitchExecuted: false,
            LearningAccepted: false,
            statusCode,
            message);
    }

    private async Task<ModerationExecutionResult> CompleteReadOnlyApprovalAsync(
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        await using (var updateCase = connection.CreateCommand())
        {
            updateCase.Transaction = transaction;
            updateCase.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'executed',
                    row_version = row_version + 1,
                    resolved_utc = @resolved_utc,
                    proposed_action = @approved_action,
                    proposed_timeout_seconds = @approved_timeout_seconds,
                    reason = @approved_reason,
                    policy_set = @approved_policy_set,
                    violation_rule_code = @approved_rule_code,
                    trigger_phrases_json = @approved_triggers
                WHERE case_id = @case_id
                  AND row_version = @expected_version
                  AND status = 'pending';
                """;
            updateCase.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            updateCase.Parameters.AddWithValue(
                "@expected_version",
                request.ExpectedRowVersion);
            updateCase.Parameters.AddWithValue(
                "@resolved_utc",
                SqliteData.ToDatabaseTimestamp(now));
            updateCase.Parameters.AddWithValue(
                "@approved_action",
                ToDatabaseAction(request.Action));
            updateCase.Parameters.AddWithValue(
                "@approved_timeout_seconds",
                SqliteData.DbValue(request.TimeoutSeconds));
            updateCase.Parameters.AddWithValue("@approved_reason", request.Reason);
            updateCase.Parameters.AddWithValue(
                "@approved_policy_set",
                ToDatabasePolicySet(request.PolicySet));
            updateCase.Parameters.AddWithValue("@approved_rule_code", request.RuleCode!);
            updateCase.Parameters.AddWithValue(
                "@approved_triggers",
                JsonSerializer.Serialize(request.AcceptedTriggerPhrases));
            if (await updateCase.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Rejected(
                    "case_changed",
                    "Der Moderationsfall wurde parallel geändert.");
            }
        }

        await InsertModerationFeedbackAsync(
            connection,
            transaction,
            moderationCase.CaseId,
            ToDatabaseAction(request.Action),
            request.TimeoutSeconds,
            request.Reason,
            request.RuleCode!,
            twitchExecuted: false,
            learningAccepted: true,
            eligibleForLearning: true,
            reviewCompleted: true,
            now,
            cancellationToken).ConfigureAwait(false);

        var accepted = ModerationRuleCodes.AllowsTriggerLearning(request.RuleCode)
            ? NormalizePhrases(request.AcceptedTriggerPhrases)
            : [];
        await WritePhraseFeedbackAsync(
            connection,
            transaction,
            $"read-only:{moderationCase.CaseId}",
            moderationCase,
            request,
            accepted,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _learningContextRevision.Advance();

        return new ModerationExecutionResult(
            ModerationExecutionOutcome.Succeeded,
            HumanApproved: true,
            TwitchExecuted: false,
            LearningAccepted: true,
            "read_only_learning_saved",
            "Read-Only: Die Entscheidung wurde lokal bestätigt und gelernt; "
            + "es wurde keine Aktion an Twitch gesendet.");
    }

    private async Task TryRecordOwnDeleteAsync(
        long executionId,
        ModerationCase moderationCase,
        CancellationToken cancellationToken)
    {
        try
        {
            await _availabilityStore.RecordDeletedAsync(
                new TwitchMessageDeletedEvent(
                    $"local-delete:{executionId}",
                    moderationCase.Message.Identity.ChannelId,
                    DateTimeOffset.UtcNow,
                    moderationCase.Message.Identity.TwitchMessageId,
                    moderationCase.Message.Identity.TwitchUserId,
                    moderationCase.Message.Identity.Login,
                    moderationCase.Message.Identity.DisplayName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Twitch already confirmed the action. Availability persistence is
            // best-effort here so an audit write problem cannot turn a confirmed
            // remote success into an ambiguous retry candidate.
        }
    }

    private async Task TryRecordOwnUserClearAsync(
        string action,
        long executionId,
        ModerationCase moderationCase,
        CancellationToken cancellationToken)
    {
        try
        {
            await _availabilityStore.RecordUserClearAsync(
                new TwitchUserMessagesClearedEvent(
                    $"local-{action}:{executionId}",
                    moderationCase.Message.Identity.ChannelId,
                    DateTimeOffset.UtcNow,
                    moderationCase.Message.Identity.TwitchUserId,
                    moderationCase.Message.Identity.Login,
                    moderationCase.Message.Identity.DisplayName),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // See TryRecordOwnDeleteAsync.
        }
    }

    private async Task<ModerationExecutionResult> CompleteAttemptAsync(
        long executionId,
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        AttemptCompletion completion,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        await using (var updateAttempt = connection.CreateCommand())
        {
            updateAttempt.Transaction = transaction;
            updateAttempt.CommandText =
                """
                UPDATE action_attempts
                SET status = @status,
                    completed_utc = @completed_utc,
                    http_status = @http_status,
                    error_code = @error_code,
                    response_json = @response_json
                WHERE execution_id = @execution_id
                  AND status = 'intent';
                """;
            updateAttempt.Parameters.AddWithValue("@status", completion.AttemptStatus);
            updateAttempt.Parameters.AddWithValue(
                "@completed_utc",
                SqliteData.ToDatabaseTimestamp(now));
            updateAttempt.Parameters.AddWithValue(
                "@http_status",
                SqliteData.DbValue(completion.HttpStatus));
            updateAttempt.Parameters.AddWithValue(
                "@error_code",
                SqliteData.DbValue(completion.ErrorCode));
            updateAttempt.Parameters.AddWithValue(
                "@response_json",
                SqliteData.DbValue(completion.Diagnostic));
            updateAttempt.Parameters.AddWithValue("@execution_id", executionId);
            if (await updateAttempt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Rejected(
                    "attempt_changed",
                    "Der Ausführungsversuch wurde bereits abgeschlossen.");
            }
        }

        await using (var updateCase = connection.CreateCommand())
        {
            updateCase.Transaction = transaction;
            updateCase.CommandText =
                """
                UPDATE moderation_cases
                SET status = @status,
                    row_version = row_version + 1,
                    resolved_utc = @resolved_utc
                WHERE case_id = @case_id
                  AND status IN ('executing','externally_completed');
                """;
            updateCase.Parameters.AddWithValue("@status", completion.CaseStatus);
            updateCase.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            updateCase.Parameters.AddWithValue(
                "@resolved_utc",
                SqliteData.ToDatabaseTimestamp(now));
            if (await updateCase.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Rejected(
                    "case_completion_conflict",
                    "Der Twitch-Versuch wurde protokolliert, der Fallstatus änderte sich jedoch parallel.");
            }
        }

        var twitchExecuted = completion.Outcome == ModerationExecutionOutcome.Succeeded;
        var accepted = ModerationRuleCodes.AllowsTriggerLearning(request.RuleCode)
            ? NormalizePhrases(request.AcceptedTriggerPhrases)
            : [];
        var learningAccepted = twitchExecuted;
        await InsertModerationFeedbackAsync(
            connection,
            transaction,
            moderationCase.CaseId,
            actualAction: twitchExecuted ? ToDatabaseAction(request.Action) : "none",
            actualTimeoutSeconds: twitchExecuted ? request.TimeoutSeconds : null,
            request.Reason,
            request.RuleCode!,
            twitchExecuted,
            learningAccepted,
            eligibleForLearning: twitchExecuted,
            reviewCompleted:
                completion.Outcome != ModerationExecutionOutcome.AlreadyUnavailable,
            now,
            cancellationToken).ConfigureAwait(false);

        if (twitchExecuted)
        {
            await WritePhraseFeedbackAsync(
                connection,
                transaction,
                $"execution:{executionId}",
                moderationCase,
                request,
                accepted,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (learningAccepted)
        {
            _learningContextRevision.Advance();
        }

        return new ModerationExecutionResult(
            completion.Outcome,
            HumanApproved: true,
            twitchExecuted,
            learningAccepted,
            completion.StatusCode,
            completion.Message);
    }

    private static async Task WritePhraseFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceEventPrefix,
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        HashSet<string> accepted,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        var index = 0;
        foreach (var triggerPhrase in accepted.OrderBy(
                     item => item,
                     StringComparer.Ordinal))
        {
            var eligible = await UpdatePhraseConfirmationAsync(
                connection,
                transaction,
                triggerPhrase,
                request.PolicySet,
                request.RuleCode!,
                isPositive: true,
                cancellationToken).ConfigureAwait(false);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    category_id, message_normalized, eligible, confirmed_utc)
                VALUES(
                    @case_id, 'operator_execute', @source_event_id, @label_action,
                    @rule_code, @trigger_phrase, @policy_set, 1,
                    @category_id, @message_normalized, @eligible, @confirmed_utc);
                """;
            insert.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            insert.Parameters.AddWithValue(
                "@source_event_id",
                $"{sourceEventPrefix}:{index++}");
            insert.Parameters.AddWithValue("@label_action", ToDatabaseAction(request.Action));
            insert.Parameters.AddWithValue("@rule_code", request.RuleCode!);
            insert.Parameters.AddWithValue("@trigger_phrase", triggerPhrase);
            insert.Parameters.AddWithValue(
                "@policy_set",
                ToDatabasePolicySet(request.PolicySet));
            insert.Parameters.AddWithValue(
                "@category_id",
                SqliteData.DbValue(moderationCase.Message.ChannelContext.CategoryId));
            insert.Parameters.AddWithValue(
                "@message_normalized",
                moderationCase.Message.Text.RuleNormalizedText);
            insert.Parameters.AddWithValue("@eligible", eligible ? 1 : 0);
            insert.Parameters.AddWithValue(
                "@confirmed_utc",
                SqliteData.ToDatabaseTimestamp(confirmedAt));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> UpdatePhraseConfirmationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedPhrase,
        PolicySetCode policySet,
        string ruleCode,
        bool isPositive,
        CancellationToken cancellationToken)
    {
        long? phraseId = null;
        long? scopeId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT p.phrase_id, s.scope_id
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = @global_scope
                  AND s.set_code = @policy_set
                  AND r.rule_code = @rule_code
                  AND p.normalized_phrase = @phrase
                  AND p.archived_utc IS NULL
                  AND r.archived_utc IS NULL
                LIMIT 1;
                """;
            read.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            read.Parameters.AddWithValue("@policy_set", ToDatabasePolicySet(policySet));
            read.Parameters.AddWithValue("@rule_code", ruleCode);
            read.Parameters.AddWithValue("@phrase", normalizedPhrase);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                phraseId = reader.GetInt64(0);
                scopeId = reader.GetInt64(1);
            }
        }

        if (!phraseId.HasValue && isPositive)
        {
            long ruleId;
            await using (var readRule = connection.CreateCommand())
            {
                readRule.Transaction = transaction;
                readRule.CommandText =
                    """
                    SELECT r.rule_id, s.scope_id
                    FROM blacklist_rules r
                    INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                    WHERE s.broadcaster_id = @global_scope
                      AND s.set_code = @policy_set
                      AND r.rule_code = @rule_code
                      AND s.enabled = 1
                      AND s.archived_utc IS NULL
                      AND r.enabled = 1
                      AND r.archived_utc IS NULL
                    LIMIT 1;
                    """;
                readRule.Parameters.AddWithValue(
                    "@global_scope",
                    GlobalPolicyStorage.BroadcasterId);
                readRule.Parameters.AddWithValue(
                    "@policy_set",
                    ToDatabasePolicySet(policySet));
                readRule.Parameters.AddWithValue("@rule_code", ruleCode);
                await using var reader = await readRule
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                ruleId = reader.GetInt64(0);
                scopeId = reader.GetInt64(1);
            }

            var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO phrase_rules(
                        rule_id, phrase, normalized_phrase, enabled,
                        manual_override, positive_confirmations, negative_confirmations,
                        row_version, created_utc, updated_utc)
                    VALUES(
                        @rule_id, @phrase, @phrase, 1,
                        'auto', 0, 0,
                        1, @now, @now);
                    """;
                insert.Parameters.AddWithValue("@rule_id", ruleId);
                insert.Parameters.AddWithValue("@phrase", normalizedPhrase);
                insert.Parameters.AddWithValue("@now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var readInserted = connection.CreateCommand())
            {
                readInserted.Transaction = transaction;
                readInserted.CommandText =
                    """
                    SELECT phrase_id
                    FROM phrase_rules
                    WHERE rule_id = @rule_id
                      AND normalized_phrase = @phrase
                      AND archived_utc IS NULL;
                    """;
                readInserted.Parameters.AddWithValue("@rule_id", ruleId);
                readInserted.Parameters.AddWithValue("@phrase", normalizedPhrase);
                var value = await readInserted.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                phraseId = value is null
                    ? null
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        if (phraseId.HasValue)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = isPositive
                ? """
                  UPDATE phrase_rules
                  SET positive_confirmations = positive_confirmations + 1,
                      row_version = row_version + 1,
                      updated_utc = @updated_utc
                  WHERE phrase_id = @phrase_id;
                  """
                : """
                  UPDATE phrase_rules
                  SET negative_confirmations = negative_confirmations + 1,
                      row_version = row_version + 1,
                      updated_utc = @updated_utc
                  WHERE phrase_id = @phrase_id;
                  """;
            update.Parameters.AddWithValue("@phrase_id", phraseId.Value);
            update.Parameters.AddWithValue(
                "@updated_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (scopeId.HasValue)
            {
                await using var bumpRevision = connection.CreateCommand();
                bumpRevision.Transaction = transaction;
                bumpRevision.CommandText =
                    """
                    UPDATE policy_sets
                    SET revision = revision + 1,
                        row_version = row_version + 1,
                        updated_utc = @updated_utc
                    WHERE scope_id = @scope_id;
                    """;
                bumpRevision.Parameters.AddWithValue("@scope_id", scopeId.Value);
                bumpRevision.Parameters.AddWithValue(
                    "@updated_utc",
                    SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
                await bumpRevision.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return phraseId.HasValue;
    }

    private static async Task InsertModerationFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long caseId,
        string actualAction,
        int? actualTimeoutSeconds,
        string actualReason,
        string ruleCode,
        bool twitchExecuted,
        bool learningAccepted,
        bool eligibleForLearning,
        bool reviewCompleted,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO moderation_feedback(
                case_id, operator_decision, actual_action,
                actual_timeout_seconds, actual_reason, discard_reason, rule_code,
                eligible_for_learning, human_approved, twitch_executed,
                learning_accepted, review_completed, decided_utc)
            VALUES(
                @case_id, 'executed', @actual_action,
                @timeout_seconds, @reason, NULL, @rule_code,
                @eligible, 1, @twitch_executed,
                @learning_accepted, @review_completed, @decided_utc);
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@actual_action", actualAction);
        command.Parameters.AddWithValue(
            "@timeout_seconds",
            SqliteData.DbValue(actualTimeoutSeconds));
        command.Parameters.AddWithValue(
            "@reason",
            string.IsNullOrEmpty(actualReason) ? DBNull.Value : actualReason);
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        command.Parameters.AddWithValue("@eligible", eligibleForLearning ? 1 : 0);
        command.Parameters.AddWithValue("@twitch_executed", twitchExecuted ? 1 : 0);
        command.Parameters.AddWithValue("@learning_accepted", learningAccepted ? 1 : 0);
        command.Parameters.AddWithValue("@review_completed", reviewCompleted ? 1 : 0);
        command.Parameters.AddWithValue(
            "@decided_utc",
            SqliteData.ToDatabaseTimestamp(decidedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private HashSet<string> NormalizePhrases(IEnumerable<string> phrases)
    {
        return phrases
            .Select(_normalizer.Normalize)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ToDatabaseAction(ModerationActionKind action)
    {
        return action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static string ToDatabasePolicySet(PolicySetCode policySet)
    {
        return policySet switch
        {
            PolicySetCode.General => "general",
            PolicySetCode.Backseatgaming => "backseatgaming",
            _ => throw new ArgumentOutOfRangeException(nameof(policySet), policySet, null),
        };
    }

    private static string ToDatabaseStatus(CoreModerationCaseStatus status)
    {
        return status switch
        {
            CoreModerationCaseStatus.StalePolicy => "stale_policy",
            CoreModerationCaseStatus.IntegrityBlocked => "integrity_blocked",
            CoreModerationCaseStatus.ExternallyCompleted => "externally_completed",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static ModerationExecutionResult Rejected(string statusCode, string message)
    {
        return new ModerationExecutionResult(
            ModerationExecutionOutcome.Rejected,
            HumanApproved: false,
            TwitchExecuted: false,
            LearningAccepted: false,
            statusCode,
            message);
    }

    private readonly record struct AttemptCompletion(
        string AttemptStatus,
        string CaseStatus,
        ModerationExecutionOutcome Outcome,
        int? HttpStatus,
        string? ErrorCode,
        string? Diagnostic,
        string StatusCode,
        string Message)
    {
        public static AttemptCompletion Succeeded()
        {
            return new AttemptCompletion(
                "succeeded",
                "executed",
                ModerationExecutionOutcome.Succeeded,
                null,
                null,
                null,
                "executed",
                "Die Twitch-Moderationsaktion wurde ausgeführt.");
        }

        public static AttemptCompletion AlreadyUnavailable(
            int? httpStatus,
            string errorCode,
            string diagnostic)
        {
            return new AttemptCompletion(
                "failed",
                "externally_completed",
                ModerationExecutionOutcome.AlreadyUnavailable,
                httpStatus,
                errorCode,
                diagnostic,
                errorCode,
                "Twitch meldet die Nachricht oder den Benutzer bereits als nicht verfügbar.");
        }

        public static AttemptCompletion Failed(
            int? httpStatus,
            string errorCode,
            string diagnostic)
        {
            return new AttemptCompletion(
                "failed",
                "failed",
                ModerationExecutionOutcome.Failed,
                httpStatus,
                errorCode,
                diagnostic,
                errorCode,
                "Twitch hat die Moderationsaktion abgelehnt.");
        }

        public static AttemptCompletion OutcomeUnknown(
            string errorCode,
            string diagnostic)
        {
            return new AttemptCompletion(
                "outcome_unknown",
                "outcome_unknown",
                ModerationExecutionOutcome.OutcomeUnknown,
                null,
                errorCode,
                diagnostic,
                errorCode,
                "Das Twitch-Ergebnis ist unklar; die Aktion wird nicht automatisch wiederholt.");
        }
    }
}
