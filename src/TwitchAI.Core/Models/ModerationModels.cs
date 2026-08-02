namespace TwitchAI.Core.Models;

public enum ModerationActionKind
{
    None,
    Delete,
    Timeout,
    Ban,
}

public enum ModerationProposalOrigin
{
    LocalAi,
    DeterministicPhrase,
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

    public IReadOnlyList<Guid> RemovedQueuedBatchIds { get; init; } = [];

    public string? DuplicateOfTemporaryMessageId { get; init; }
}

public sealed record RetrievalExample(
    string LowerText,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode PolicySet,
    string Reason,
    bool IsPositive,
    string? RuleCode = null)
{
    public bool IsExactHumanDecision { get; init; }
}

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
    IReadOnlyList<string> LocalMatchCodes)
{
    public IReadOnlyList<ModerationRequestPhraseMatchV1> LocalPhraseMatches { get; init; } = [];

    public int? TwitchEmoteCount { get; init; }

    public bool? IsTwitchEmoteOnly { get; init; }
}

public sealed record ModerationRequestPhraseMatchV1(
    long PhraseId,
    string RuleCode,
    string Phrase);

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
    double? ReplyConfidence = null)
{
    public string? EvidenceKind { get; init; }

    public string? SensitiveDataType { get; init; }
}

public static class ModerationEvidenceKinds
{
    private static readonly Dictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ModerationRuleCodes.PersonalAttack] = Set(
                "direct_insult",
                "verified_harassment",
                "unwanted_intimate_approach"),
            [ModerationRuleCodes.SexualContent] = Set("sexualization", "sexual_solicitation", "explicit_sexual", "sexist_degradation"),
            [ModerationRuleCodes.Hate] = Set("racist", "lgbtq_hostility", "ableist", "religious_or_other"),
            [ModerationRuleCodes.Harm] = Set("direct_threat", "violence_incitation", "self_harm_incitation"),
            [ModerationRuleCodes.PersonalData] = Set("disclosure", "targeted_request", "threat_to_disclose"),
            [ModerationRuleCodes.Begging] = Set("subscription", "money_or_item", "giveaway_pressure"),
            [ModerationRuleCodes.Spam] = Set("emote_flood", "text_flood", "symbol_flood"),
            [ModerationRuleCodes.Backseatgaming] = Set("instruction", "solution", "spoiler", "hidden_danger"),
            [ModerationRuleCodes.SelfPromotion] = Set("self_channel_promotion", "automated_self_promotion", "malicious_self_promotion"),
            [ModerationRuleCodes.BotScam] = Set("bot_advertising", "scam_service", "phishing"),
        };

    public static IReadOnlyList<string> ForRule(string? ruleCode) =>
        ruleCode is not null && Allowed.TryGetValue(ruleCode, out var values)
            ? values.Order(StringComparer.Ordinal).ToArray()
            : [];

    public static bool IsValid(string? ruleCode, string? evidenceKind) =>
        ruleCode is not null
        && evidenceKind is not null
        && Allowed.TryGetValue(ruleCode, out var values)
        && values.Contains(evidenceKind);

    private static HashSet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.Ordinal);
}

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

    public string? EvidenceKind { get; init; }

    public ModerationProposalOrigin ProposalOrigin { get; init; } =
        ModerationProposalOrigin.LocalAi;

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
    public ModerationProposalOrigin ProposalOrigin { get; init; } =
        ModerationProposalOrigin.LocalAi;

    public IReadOnlyList<BlacklistMatch> PhraseMatches { get; init; } = [];

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
