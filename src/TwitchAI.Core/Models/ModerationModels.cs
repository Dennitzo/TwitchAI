namespace TwitchAI.Core.Models;

public enum ModerationActionKind
{
    None,
    Delete,
    Timeout,
    Ban,
}

public enum ModerationCaseStatus
{
    Pending,
    Executing,
    Executed,
    Discarded,
    Allowlisted,
    StalePolicy,
    IntegrityBlocked,
    ExternallyCompleted,
    OutcomeUnknown,
}

public enum BatchSealReason
{
    MessageLimit,
    CharacterLimit,
    Deadline,
    CategoryChanged,
    PolicyRevisionChanged,
    ManualFlush,
}

public sealed record ModerationBatchMessage(
    string TemporaryMessageId,
    ChatMessageSnapshot Message,
    IReadOnlyList<BlacklistMatch> LocalMatches)
{
    public long? ReanalysisCaseId { get; init; }

    public long? ReanalysisCaseRowVersion { get; init; }

    public int PromptCharacterCount =>
        TemporaryMessageId.Length + Message.Text.LowerText.Length;
}

public sealed record ModerationBatchContext(
    ChannelContextSnapshot Channel,
    EffectivePolicySnapshot EffectivePolicy);

public sealed record ModerationBatch(
    Guid BatchId,
    ModerationBatchContext Context,
    IReadOnlyList<ModerationBatchMessage> Messages,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset SealedAtUtc,
    BatchSealReason SealReason)
{
    public IReadOnlyList<ChatReplyTemplatePrompt> ReplyTemplates { get; init; } = [];
}

public sealed record ModerationBatcherOptions(
    int MaximumMessages,
    TimeSpan MaximumCollectionDelay,
    int MaximumMessageCharacters,
    int MaximumQueuedMessages)
{
    public static ModerationBatcherOptions Default { get; } =
        new(20, TimeSpan.FromSeconds(10), 6_000, 500);
}

public enum BatchIngressDisposition
{
    Accepted,
    AcceptedAndSealed,
    DuplicateSuppressed,
}

public sealed record BatchIngressResult(
    BatchIngressDisposition Disposition,
    IReadOnlyList<ModerationBatch> NewlySealedBatches,
    int DroppedOldestMessages,
    DateTimeOffset? NextDeadlineUtc)
{
    public IReadOnlyList<ModerationBatchMessage> DroppedMessages { get; init; } = [];

    public IReadOnlyList<ModerationBatch> UpdatedQueuedBatches { get; init; } = [];

    public string? DuplicateOfTemporaryMessageId { get; init; }
}

public sealed record RetrievalExample(
    string LowerText,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode PolicySet,
    string Reason,
    bool IsPositive,
    string? RuleCode = null);

public sealed record ChatReplyTemplatePrompt(
    string TemplateId,
    string ExampleLower);

public sealed record ModerationRequestEnvelopeV3(
    string EnvelopeVersion,
    Guid BatchId,
    string? CategoryId,
    string CategoryDisplayName,
    IReadOnlyList<PolicySetRevision> ActivePolicySets,
    IReadOnlyList<ModerationRuleDefinition> ActiveRules,
    IReadOnlyList<ModerationRequestMessageV3> Messages,
    IReadOnlyList<RetrievalExample> LearningExamples,
    IReadOnlyList<ChatReplyTemplatePrompt> ReplyTemplates);

public sealed record ModerationRequestMessageV3(
    string MessageId,
    string LowerText,
    IReadOnlyList<string> LocalMatchCodes);

public sealed record ModerationDecisionCandidateV3(
    string MessageId,
    string Action,
    int? TimeoutSeconds,
    string? PolicySet,
    string? RuleCode,
    string Reason,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    string? ReplyTemplateId = null,
    double? ReplyConfidence = null);

public sealed record ModerationResponseEnvelopeV3(
    string EnvelopeVersion,
    Guid BatchId,
    IReadOnlyList<ModerationDecisionCandidateV3> Decisions);

public sealed record ValidatedModerationDecision(
    string TemporaryMessageId,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode? PolicySet,
    string Reason,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    string? RuleCode = null,
    string? ReplyTemplateId = null,
    double? ReplyConfidence = null);

public sealed record ValidatedModerationEnvelope(
    Guid BatchId,
    IReadOnlyList<ValidatedModerationDecision> Decisions);

public sealed record EnvelopeValidationError(string Code, string Message);

public sealed record EnvelopeValidationResult(
    ValidatedModerationEnvelope? Envelope,
    IReadOnlyList<EnvelopeValidationError> Errors)
{
    public bool IsValid => Envelope is not null && Errors.Count == 0;

    public static EnvelopeValidationResult Invalid(params EnvelopeValidationError[] errors) =>
        new(null, errors);
}

public sealed record ModerationCase(
    long CaseId,
    long RowVersion,
    ChatMessageSnapshot Message,
    ModerationActionKind ProposedAction,
    int? ProposedTimeoutSeconds,
    string Reason,
    PolicySetCode PolicySet,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    string PolicyRevisionFingerprint,
    ModerationCaseStatus Status,
    DateTimeOffset CreatedAtUtc,
    Guid? BatchId,
    IReadOnlyList<string> LocalMatchCodes,
    long? AiLatencyMilliseconds)
{
    public string? RuleCode { get; init; }

    public ModerationCase(
        long CaseId,
        long RowVersion,
        ChatMessageSnapshot Message,
        ModerationActionKind ProposedAction,
        int? ProposedTimeoutSeconds,
        string Reason,
        PolicySetCode PolicySet,
        IReadOnlyList<string> TriggerPhrases,
        double Confidence,
        string PolicyRevisionFingerprint,
        ModerationCaseStatus Status,
        DateTimeOffset CreatedAtUtc)
        : this(
            CaseId,
            RowVersion,
            Message,
            ProposedAction,
            ProposedTimeoutSeconds,
            Reason,
            PolicySet,
            TriggerPhrases,
            Confidence,
            PolicyRevisionFingerprint,
            Status,
            CreatedAtUtc,
            BatchId: null,
            LocalMatchCodes: [],
            AiLatencyMilliseconds: null)
    {
    }
}

public sealed record ModerationCaseCreationMetadata(
    Guid? BatchId,
    IReadOnlyList<string> LocalMatchCodes,
    long? AiLatencyMilliseconds)
{
    public long? ReanalysisCaseId { get; init; }

    public long? ReanalysisCaseRowVersion { get; init; }

    public static ModerationCaseCreationMetadata Empty { get; } =
        new(null, [], null);
}

public sealed record ModerationCaseAddResult(
    long CaseId,
    bool IsCreated);

public sealed record ModerationExecutionRequest(
    long CaseId,
    long ExpectedRowVersion,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    string Reason,
    PolicySetCode PolicySet,
    IReadOnlyList<string> AcceptedTriggerPhrases,
    string? RuleCode = null);

public enum ModerationExecutionOutcome
{
    Succeeded,
    AlreadyUnavailable,
    IntegrityBlocked,
    StalePolicy,
    Rejected,
    Failed,
    OutcomeUnknown,
}

public sealed record ModerationExecutionResult(
    ModerationExecutionOutcome Outcome,
    bool HumanApproved,
    bool TwitchExecuted,
    bool LearningAccepted,
    string StatusCode,
    string Message);
