using TwitchAI.Core.Models;

namespace TwitchAI.Core.Contracts;

public interface IChatTextNormalizer
{
    int NormalizationVersion { get; }

    ProcessedChatText Process(string originalText);
}

public interface IWhitelistPhraseSource
{
    ValueTask<ActiveWhitelistSnapshot> GetActiveEntriesAsync(
        string channelId,
        CancellationToken cancellationToken = default);

    ValueTask RecordHitAsync(
        string channelId,
        WhitelistEntryKind kind,
        string normalizedValue,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IWhitelistFilterAgent
{
    ValueTask<bool> ShouldSkipAsync(
        ChatMessageSnapshot message,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IEffectivePolicyResolver
{
    EffectivePolicySnapshot Resolve(
        ChannelContextSnapshot channelContext,
        long generalRevision,
        long backseatgamingRevision);
}

public interface IPhraseRuleSource
{
    ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
        string channelId,
        EffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default);
}

public interface IBlacklistMatcher
{
    IReadOnlyList<BlacklistMatch> Match(
        ProcessedChatText text,
        EffectivePolicySnapshot policy,
        IReadOnlyList<PhraseRule> rules);
}

public interface IModerationEnvelopeValidator
{
    EnvelopeValidationResult Validate(
        string rawJson,
        ModerationBatch batch);

    EnvelopeValidationResult ValidateWithMissingRuleFallback(
        string rawJson,
        ModerationBatch batch);

    ModerationValidationReport ValidateDetailed(
        string rawJson,
        ModerationBatch batch);

    ModerationValidationReport ValidateDetailedWithMissingRuleFallback(
        string rawJson,
        ModerationBatch batch);

    ModerationValidationReport ValidateV6(
        string rawJson,
        ModerationBatch batch);

    ModerationValidationReport ValidateV4(
        string rawJson,
        ModerationBatch batch);
}

public interface IModerationPromptCompiler
{
    CompiledModerationPrompt Compile(ModerationRequestEnvelopeV3 envelope);
}

public interface IModerationBatcher
{
    int WaitingMessageCount { get; }

    DateTimeOffset? NextDeadlineUtc { get; }

    BatchIngressResult Enqueue(
        ModerationBatchMessage message,
        ModerationBatchContext context,
        DateTimeOffset nowUtc);

    ModerationBatch? SealIfDue(DateTimeOffset nowUtc);

    ModerationBatch? Flush(DateTimeOffset nowUtc);

    ModerationBatch? FlushChannel(
        string channelId,
        DateTimeOffset nowUtc) => Flush(nowUtc);

    void Clear()
    {
    }

    bool TryDequeue(out ModerationBatch? batch);
}

public interface IModerationQueueRuntimeSettings
{
    bool RemoveIdenticalMessages { get; }
}

public interface ITwitchRuntimeModeSettings
{
    bool IsReadOnly { get; }
}

public interface IMessageAvailabilityRegistry
{
    void Observe(ChatMessageSnapshot message);

    void MarkDeletedExact(
        string eventSubNotificationId,
        string channelId,
        string twitchMessageId,
        string twitchUserId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc);

    void MarkUserCleared(
        string eventSubNotificationId,
        string channelId,
        string twitchUserId,
        DateTimeOffset throughReceivedAtUtc);

    void MarkRoomCleared(
        string eventSubNotificationId,
        string channelId,
        DateTimeOffset throughReceivedAtUtc);

    void MarkUnknownGap(string channelId, DateTimeOffset throughReceivedAtUtc);

    void MarkOutcomeUnknown(string twitchMessageId, DateTimeOffset occurredAtUtc);

    MessageAvailabilityAssessment Assess(
        ChatMessageIdentityV1 identity,
        DateTimeOffset nowUtc);
}

public interface IChannelContextService
{
    ValueTask<ChannelContextSnapshot> GetCurrentAsync(
        string channelId,
        CancellationToken cancellationToken = default);
}

public interface ITwitchEventSubClient
{
    IAsyncEnumerable<TwitchChatEvent> ReadEventsAsync(CancellationToken cancellationToken);
}

public interface ILocalModerationClient
{
    ValueTask<string> ModerateAsync(
        ModerationRequestEnvelopeV3 request,
        CancellationToken cancellationToken = default);

    ValueTask<string> ModerateAsync(
        CompiledModerationPrompt prompt,
        CancellationToken cancellationToken = default) =>
        ModerateAsync(prompt.Envelope, cancellationToken);
}

public interface ILearningTestService
{
    ValueTask<LearningTestResult> AnalyzeAsync(
        LearningTestRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsPolicyCurrentAsync(
        LearningTestResult result,
        CancellationToken cancellationToken = default);

    ValueTask<LearningReasonResult> GenerateReasonAsync(
        LearningReasonRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILearningFeedbackService
{
    ValueTask<LearningFeedbackResult> SaveAsync(
        LearningReviewItem review,
        LearningFeedbackDecision decision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tracks the process-local generation of the effective learning context.
/// Writers advance the generation only after a successful learning commit;
/// inference consumers use it to reject results based on stale examples.
/// </summary>
public interface ILearningContextRevision
{
    long Current { get; }

    long Advance();
}

public interface IAiValidationAttemptStore
{
    ValueTask RecordAsync(
        AiValidationAttempt attempt,
        CancellationToken cancellationToken = default);
}

public interface IModerationCaseRepository
{
    ValueTask<ModerationCase?> GetAsync(
        long caseId,
        CancellationToken cancellationToken = default);

    ValueTask<long> AddAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        CancellationToken cancellationToken = default);

    ValueTask<long> AddAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        ModerationCaseCreationMetadata metadata,
        CancellationToken cancellationToken = default) =>
        AddAsync(
            message,
            decision,
            policyRevisionFingerprint,
            cancellationToken);

    async ValueTask<ModerationCaseAddResult> AddIfNoActiveDuplicateAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        ModerationCaseCreationMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var caseId = await AddAsync(
                message,
                decision,
                policyRevisionFingerprint,
                metadata,
                cancellationToken)
            .ConfigureAwait(false);
        return new ModerationCaseAddResult(caseId, IsCreated: true);
    }

    ValueTask<bool> TryTransitionAsync(
        long caseId,
        long expectedRowVersion,
        ModerationCaseStatus fromStatus,
        ModerationCaseStatus toStatus,
        CancellationToken cancellationToken = default);

    ValueTask<bool> TryCompleteReanalysisWithoutActionAsync(
        long caseId,
        long expectedRowVersion,
        bool wasWhitelisted,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

public interface ITwitchModerationExecutor
{
    ValueTask<ModerationExecutionResult> ExecuteAsync(
        ModerationExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseTableCatalog
{
    IReadOnlyList<DatabaseTableDescriptor> GetTables();

    DatabaseTableDescriptor GetTable(string tableName);
}

public interface IDatabaseEditorService
{
    ValueTask<DatabasePage> QueryAsync(
        string tableName,
        string? searchText,
        string? sortColumn,
        bool descending,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default);

    ValueTask<DatabaseCommitResult> CommitAsync(
        DatabaseChangeSet changeSet,
        CancellationToken cancellationToken = default);
}

public enum TwitchChatEventKind
{
    Message,
    MessageDeleted,
    UserMessagesCleared,
    RoomCleared,
    ChannelUpdated,
    EventGap,
}

public sealed record TwitchChatEvent(
    TwitchChatEventKind Kind,
    string EventSubNotificationId,
    DateTimeOffset ReceivedAtUtc,
    ChatMessageIdentityV1? Message,
    string ChannelId,
    string? TargetMessageId,
    string? TargetUserId,
    ChannelContextSnapshot? ChannelContext)
{
    public int TwitchEmoteCount { get; init; }

    public bool IsTwitchEmoteOnly { get; init; }
}
