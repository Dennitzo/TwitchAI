using System.Diagnostics;
using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;
using CoreTextNormalizer = TwitchAI.Core.Contracts.IChatTextNormalizer;
using StoredEffectivePolicySnapshot =
    TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Infrastructure.Pipeline;

public sealed class LearningTestService : ILearningTestService
{
    public const int MaximumInputCharacters = 500;

    private readonly IPolicyStore _policyStore;
    private readonly CoreTextNormalizer _normalizer;
    private readonly IPhraseRuleSource _phraseRuleSource;
    private readonly IBlacklistMatcher _blacklistMatcher;
    private readonly ILmStudioClient _lmStudioClient;
    private readonly ILocalAiRunActivityMonitor? _activityMonitor;
    private readonly IRetrievalExampleStore? _retrievalExampleStore;
    private readonly ILearningContextRevision _learningContextRevision;
    private readonly IAiValidationAttemptStore? _validationAttemptStore;
    private readonly LmStudioOptions _lmStudioOptions;
    private readonly TimeProvider _timeProvider;
    private int _analysisInProgress;

    public LearningTestService(
        IPolicyStore policyStore,
        CoreTextNormalizer normalizer,
        IPhraseRuleSource phraseRuleSource,
        IBlacklistMatcher blacklistMatcher,
        ILmStudioClient lmStudioClient,
        InfrastructureOptions options,
        ILocalAiRunActivityMonitor? activityMonitor = null,
        TimeProvider? timeProvider = null,
        IRetrievalExampleStore? retrievalExampleStore = null,
        ILearningContextRevision? learningContextRevision = null,
        IAiValidationAttemptStore? validationAttemptStore = null)
    {
        _policyStore = policyStore;
        _normalizer = normalizer;
        _phraseRuleSource = phraseRuleSource;
        _blacklistMatcher = blacklistMatcher;
        _lmStudioClient = lmStudioClient;
        _activityMonitor = activityMonitor;
        _retrievalExampleStore = retrievalExampleStore;
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
        _validationAttemptStore = validationAttemptStore;
        _lmStudioOptions = options.LmStudio;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<LearningTestResult> AnalyzeAsync(
        LearningTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OriginalText);
        if (request.OriginalText.Length > MaximumInputCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.OriginalText.Length,
                $"A learning-test message may contain at most {MaximumInputCharacters} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.OriginalText))
        {
            throw new ArgumentException(
                "A learning-test message cannot be empty.",
                nameof(request));
        }

        if (Interlocked.CompareExchange(ref _analysisInProgress, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Only one learning test can be analyzed at a time.");
        }

        try
        {
            var storedPolicy = await _policyStore
                .GetLearningPolicyAsync(cancellationToken)
                .ConfigureAwait(false);
            var learningRevision = _learningContextRevision.Current;
            var policy = MapLearningPolicy(storedPolicy);
            var processed = _normalizer.Process(request.OriginalText);
            var phraseRules = await _phraseRuleSource
                .GetRulesAsync(
                    GlobalPolicyStorage.BroadcasterId,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
            var localMatches = _blacklistMatcher.Match(
                processed,
                policy,
                phraseRules);
            var retrievalExamples = _retrievalExampleStore is null
                ? []
                : await _retrievalExampleStore
                    .GetRelevantForLearningTestAsync(
                        policy,
                        processed.LowerText,
                        cancellationToken)
                    .ConfigureAwait(false);
            ThrowIfLearningChanged(learningRevision);
            var testId = Guid.NewGuid();
            var correlationId = $"learning-test:{testId:D}";
            var prompt = LearningTestPromptFactory.Create(
                testId,
                processed,
                policy,
                localMatches,
                retrievalExamples);
            var promptCharacters = prompt.PromptCharacters with
            {
                Schema = LmStudioClient.GetLearningTestResponseFormatCharacterCount(
                    policy.ActiveRules
                        .Where(item => ModerationRuleCodes.IsSemanticAiRule(item.RuleCode))
                        .Select(item => item.RuleCode)),
            };
            PublishQueued(correlationId);

            var completion = await CompleteAsync(
                    correlationId,
                    storedPolicy.PolicyHash,
                    prompt.SystemPrompt,
                    prompt.UserPrompt,
                    policy,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfLearningChanged(learningRevision);
            var validation = LearningEnvelopeValidator.Validate(
                completion.RawEnvelope,
                prompt.Envelope,
                policy,
                allowMissingRuleFallback: false);
            var initialErrors = validation.Errors;
            await RecordValidationAttemptAsync(
                    correlationId,
                    parentCorrelationId: null,
                    AiValidationAttemptKind.Initial,
                    storedPolicy.PolicyHash,
                    promptCharacters,
                    completion.RawEnvelope,
                    initialErrors,
                    validation.IsValid
                        ? AiValidationOutcome.Valid
                        : AiValidationOutcome.Rejected,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!validation.IsValid && CanApplyMissingRuleFallback(validation.Errors))
            {
                var fallbackValidation = LearningEnvelopeValidator.Validate(
                    completion.RawEnvelope,
                    prompt.Envelope,
                    policy,
                    allowMissingRuleFallback: false);
                if (fallbackValidation.IsValid)
                {
                    validation = fallbackValidation;
                    await RecordLocalFallbackAsync(
                            correlationId,
                            storedPolicy.PolicyHash,
                            initialErrors,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!validation.IsValid)
            {
                var repaired = await RepairAsync(
                        correlationId,
                        storedPolicy.PolicyHash,
                        prompt,
                        validation.Errors,
                        policy,
                        cancellationToken)
                    .ConfigureAwait(false);
                completion = repaired.Completion;
                ThrowIfLearningChanged(learningRevision);
                validation = LearningEnvelopeValidator.Validate(
                    completion.RawEnvelope,
                    prompt.Envelope,
                    policy,
                    allowMissingRuleFallback: false);
                var repairErrors = validation.Errors;
                var usedRepairFallback = false;
                if (!validation.IsValid && CanApplyMissingRuleFallback(validation.Errors))
                {
                    var fallbackValidation = LearningEnvelopeValidator.Validate(
                        completion.RawEnvelope,
                        prompt.Envelope,
                        policy,
                        allowMissingRuleFallback: false);
                    if (fallbackValidation.IsValid)
                    {
                        validation = fallbackValidation;
                        usedRepairFallback = true;
                    }
                }

                await RecordValidationAttemptAsync(
                        repaired.CorrelationId,
                        correlationId,
                        AiValidationAttemptKind.Repair,
                        storedPolicy.PolicyHash,
                        repaired.PromptCharacters,
                        completion.RawEnvelope,
                        validation.IsValid ? repaired.SourceErrors : repairErrors,
                        validation.IsValid
                            ? AiValidationOutcome.Repaired
                            : AiValidationOutcome.Rejected,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (usedRepairFallback)
                {
                    await RecordLocalFallbackAsync(
                            repaired.CorrelationId,
                            storedPolicy.PolicyHash,
                            repairErrors,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (!validation.IsValid)
            {
                throw new InvalidDataException(
                    "LM Studio returned an invalid learning-test envelope: "
                    + string.Join(
                        "; ",
                        validation.Errors.Select(
                            item => $"{item.Code}: {item.Message}")));
            }

            var latestPolicy = await _policyStore
                .GetLearningPolicyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!HasSameRevision(storedPolicy, latestPolicy))
            {
                throw new InvalidOperationException(
                    "Die Regeln wurden während des Lerntests geändert. Bitte den Test erneut starten.");
            }

            ThrowIfLearningChanged(learningRevision);

            var decision = validation.Decision!;
            var selectedRule = policy.FindRule(decision.RuleCode)
                ?? throw new InvalidDataException(
                    $"The validated learning rule '{decision.RuleCode}' is no longer active.");
            var action = decision.Action;
            var timeoutSeconds = decision.TimeoutSeconds;
            var reason = decision.Verdict == LearningVerdict.Allow
                ? string.Empty
                : ModerationReasonNormalizer.Normalize(
                    decision.Reason,
                    selectedRule,
                    selectedRule.PolicySet);
            var triggers = decision.Verdict == LearningVerdict.Allow
                ? []
                : decision.TriggerPhrases;

            return new LearningTestResult(
                testId,
                processed.OriginalText,
                processed.LowerText,
                decision.Verdict,
                action,
                timeoutSeconds,
                selectedRule.PolicySet,
                selectedRule.RuleCode,
                reason,
                triggers,
                decision.Confidence,
                localMatches
                    .Select(item => item.RuleCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                completion.Usage.ElapsedMilliseconds,
                LearningPolicyRevisionFingerprint.Create(storedPolicy),
                policy.ActiveRules,
                completion.Usage.Model,
                completion.Usage.PromptTokens,
                completion.Usage.CompletionTokens,
                completion.Usage.TotalTokens,
                _timeProvider.GetUtcNow(),
                decision.UsedRuleFallback)
            {
                LearningContextRevision = learningRevision,
                EvidenceKind = decision.EvidenceKind,
                SensitiveDataType = decision.SensitiveDataType,
            };
        }
        finally
        {
            Volatile.Write(ref _analysisInProgress, 0);
        }
    }

    public async ValueTask<bool> IsPolicyCurrentAsync(
        LearningTestResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var current = await _policyStore
            .GetLearningPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(
                   result.PolicyRevisionFingerprint,
                   LearningPolicyRevisionFingerprint.Create(current),
                   StringComparison.Ordinal)
               && (result.LearningContextRevision == 0
                   || result.LearningContextRevision ==
                   _learningContextRevision.Current);
    }

    private void ThrowIfLearningChanged(long expectedRevision)
    {
        if (_learningContextRevision.Current != expectedRevision)
        {
            throw new InvalidOperationException(
                "Der Learning-Kontext wurde während des Lerntests geändert. Bitte den Test erneut starten.");
        }
    }

    private Task<LmStudioModerationCompletion> CompleteAsync(
        string correlationId,
        string policyHash,
        string systemPrompt,
        string userPrompt,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        return _lmStudioClient.CompleteModerationAsync(
            new LmStudioModerationRequest(
                correlationId,
                policyHash,
                MessageCount: 1,
                systemPrompt,
                userPrompt,
                policy.ActiveRules
                    .Where(item => ModerationRuleCodes.IsSemanticAiRule(item.RuleCode))
                    .Select(item => item.RuleCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                ReplyTemplateIds: [],
                LmStudioResponseSchemaKind.LearningTest,
                LocalAiRunKind.LearningTest),
            cancellationToken);
    }

    private async Task<LearningRepairAttempt> RepairAsync(
        string correlationId,
        string policyHash,
        LearningTestPrompt prompt,
        IReadOnlyList<LearningEnvelopeValidationError> errors,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var repairCorrelationId = $"{correlationId}-repair";
        var errorCodes = errors
            .Select(item => item.Code)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        using var requestDocument = JsonDocument.Parse(prompt.UserPrompt);
        var payload = ModerationPromptCompiler.SerializeWire(
            new
            {
                request = requestDocument.RootElement.Clone(),
                validationErrorCodes = errorCodes,
            });
        var repairSystemPrompt = prompt.SystemPrompt
            + "\nRepair the compact request once. Correct only the listed validation error codes; "
            + "preserve the exact testId and return only a twitchai.learning-test.response.v1 object. "
            + "Do not repeat or discuss the errors.";
        var completion = await CompleteAsync(
                repairCorrelationId,
                policyHash,
                repairSystemPrompt,
                payload,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return new LearningRepairAttempt(
            repairCorrelationId,
            completion,
            new ModerationPromptCharacterCounts(
                repairSystemPrompt.Length,
                prompt.PromptCharacters.Rules,
                prompt.PromptCharacters.Messages
                    + ModerationPromptCompiler.SerializeWire(errorCodes).Length,
                prompt.PromptCharacters.Examples,
                Templates: 0,
                Schema: LmStudioClient.GetLearningTestResponseFormatCharacterCount(
                    policy.ActiveRules
                        .Where(item => ModerationRuleCodes.IsSemanticAiRule(item.RuleCode))
                        .Select(item => item.RuleCode))),
            errors);
    }

    private static bool CanApplyMissingRuleFallback(
        IReadOnlyList<LearningEnvelopeValidationError> errors) =>
        errors.Count > 0
        && errors.All(item => item.Code is "rule_missing" or "policy_set_missing");

    private async ValueTask RecordValidationAttemptAsync(
        string correlationId,
        string? parentCorrelationId,
        AiValidationAttemptKind attemptKind,
        string policyHash,
        ModerationPromptCharacterCounts promptCharacters,
        string? rawResponse,
        IReadOnlyList<LearningEnvelopeValidationError> errors,
        AiValidationOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (_validationAttemptStore is null)
        {
            return;
        }

        try
        {
            await _validationAttemptStore.RecordAsync(
                    new AiValidationAttempt(
                        correlationId,
                        parentCorrelationId,
                        attemptKind,
                        AiValidationWorkloadKind.LearningTest,
                        LearningTestPromptFactory.RequestEnvelopeVersion,
                        LearningTestPromptFactory.ResponseEnvelopeVersion,
                        MessageCount: 1,
                        policyHash,
                        errors
                            .Select(item => item.Code)
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(item => item, StringComparer.Ordinal)
                            .ToArray(),
                        AffectedMessageCount: errors.Count == 0 ? 0 : 1,
                        promptCharacters,
                        rawResponse is null
                            ? AiValidationResponseFingerprint.None
                            : AiValidationResponseFingerprint.FromRawResponse(rawResponse),
                        outcome,
                        _timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Learning validation diagnostics could not be recorded: {exception}");
        }
    }

    private ValueTask RecordLocalFallbackAsync(
        string parentCorrelationId,
        string policyHash,
        IReadOnlyList<LearningEnvelopeValidationError> sourceErrors,
        CancellationToken cancellationToken) =>
        RecordValidationAttemptAsync(
            $"{parentCorrelationId}-fallback",
            parentCorrelationId,
            AiValidationAttemptKind.LocalFallback,
            policyHash,
            new ModerationPromptCharacterCounts(0, 0, 0, 0, 0, 0),
            rawResponse: null,
            sourceErrors,
            AiValidationOutcome.Fallback,
            cancellationToken);

    private sealed record LearningRepairAttempt(
        string CorrelationId,
        LmStudioModerationCompletion Completion,
        ModerationPromptCharacterCounts PromptCharacters,
        IReadOnlyList<LearningEnvelopeValidationError> SourceErrors);

    private void PublishQueued(string correlationId)
    {
        _activityMonitor?.Publish(
            new LocalAiRunActivity(
                correlationId,
                LocalAiRunState.Queued,
                MessageCount: 1,
                _lmStudioOptions.Model,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ElapsedMilliseconds: null,
                _timeProvider.GetUtcNow(),
                ErrorCode: null,
                LocalAiRunKind.LearningTest));
    }

    private static bool HasSameRevision(
        StoredEffectivePolicySnapshot original,
        StoredEffectivePolicySnapshot current) =>
        original.GeneralRevision == current.GeneralRevision
        && original.CategoryRevision == current.CategoryRevision
        && string.Equals(
            original.PolicyHash,
            current.PolicyHash,
            StringComparison.Ordinal);

    private static CoreEffectivePolicySnapshot MapLearningPolicy(
        StoredEffectivePolicySnapshot policy)
    {
        var result = new CoreEffectivePolicySnapshot(
            [
                new(
                    PolicySetCode.General,
                    Math.Max(1, policy.GeneralRevision)),
                new(
                    PolicySetCode.Backseatgaming,
                    Math.Max(1, policy.CategoryRevision)),
            ],
            policy.PolicyHash)
        {
            ActiveRules = policy.Rules
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
                    rule.DefaultAction switch
                    {
                        _ when string.Equals(rule.RuleCode, ModerationRuleCodes.LocalBlacklist, StringComparison.Ordinal) => ModerationActionKind.Ban,
                        _ when string.Equals(rule.RuleCode, ModerationRuleCodes.BotScam, StringComparison.Ordinal) => ModerationActionKind.Ban,
                        ModerationRuleAction.Timeout => ModerationActionKind.Timeout,
                        ModerationRuleAction.Ban => ModerationActionKind.Ban,
                        _ => ModerationActionKind.Delete,
                    },
                    rule.DefaultTimeoutSeconds,
                    rule.PublicReason,
                    rule.Priority,
                    ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode))
                {
                    EvaluationMode = rule.EvaluationMode switch
                    {
                        RuleEvaluationMode.DeterministicPhrase =>
                            ModerationRuleEvaluationMode.DeterministicPhrase,
                        RuleEvaluationMode.HumanOnly => ModerationRuleEvaluationMode.HumanOnly,
                        _ => ModerationRuleEvaluationMode.SemanticAi,
                    },
                })
                .OrderBy(rule => rule.Priority)
                .ThenBy(rule => rule.RuleCode, StringComparer.Ordinal)
                .ToArray(),
        };
        if (!result.ActiveRules.Any(rule =>
                rule.PolicySet == PolicySetCode.General
                && ModerationRuleCodes.IsSemanticAiRule(rule.RuleCode))
            || result.FindRule(ModerationRuleCodes.Backseatgaming) is null)
        {
            throw new InvalidOperationException(
                "The learning policy is missing a required base rule.");
        }

        return result;
    }

}

internal sealed record LearningEnvelopeValidationError(
    string Code,
    string Message);

internal sealed record ValidatedLearningTestDecision(
    LearningVerdict Verdict,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode PolicySet,
    string RuleCode,
    string Reason,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    bool UsedRuleFallback,
    string? EvidenceKind,
    string? SensitiveDataType);

internal sealed record LearningEnvelopeValidationResult(
    ValidatedLearningTestDecision? Decision,
    IReadOnlyList<LearningEnvelopeValidationError> Errors)
{
    public bool IsValid => Decision is not null && Errors.Count == 0;
}

internal static class LearningEnvelopeValidator
{
    private const int MaximumResponseCharacters = 64 * 1024;
    private static readonly HashSet<string> RootProperties =
        ["envelopeVersion", "testId", "decision"];
    private static readonly HashSet<string> DecisionProperties =
    [
        "verdict",
        "action",
        "timeoutSeconds",
        "policySet",
        "ruleCode",
        "evidenceKind",
        "sensitiveDataType",
        "reason",
        "triggerPhrases",
        "confidence",
    ];

    public static LearningEnvelopeValidationResult Validate(
        string rawJson,
        LearningTestRequestEnvelopeV1 request,
        CoreEffectivePolicySnapshot policy,
        bool allowMissingRuleFallback)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (rawJson.Length > MaximumResponseCharacters)
        {
            return Invalid("response_too_large", "The response exceeds the safety limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 12,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("root_not_object", "The response root must be an object.");
            }

            if (!HasExactProperties(root, RootProperties))
            {
                return Invalid("root_shape_invalid", "The response root has missing or unknown properties.");
            }

            if (!TryString(root, "envelopeVersion", out var version)
                || version != LearningTestPromptFactory.ResponseEnvelopeVersion)
            {
                return Invalid("version_invalid", "The learning response version is invalid.");
            }

            if (!TryString(root, "testId", out var testIdValue)
                || !Guid.TryParse(testIdValue, out var testId)
                || testId != request.TestId)
            {
                return Invalid("test_id_invalid", "The learning response test ID does not match.");
            }

            var element = root.GetProperty("decision");
            if (element.ValueKind != JsonValueKind.Object
                || !HasExactProperties(element, DecisionProperties))
            {
                return Invalid(
                    "decision_shape_invalid",
                    "The learning decision has missing or unknown properties.");
            }

            if (!TryString(element, "verdict", out var verdictValue)
                || !TryParseVerdict(verdictValue, out var verdict))
            {
                return Invalid("verdict_invalid", "The verdict must be allow or moderate.");
            }

            if (!TryString(element, "action", out var actionValue)
                || !TryParseAction(actionValue, out var action))
            {
                return Invalid("action_invalid", "The action is invalid.");
            }

            if (!TryNullableInt32(element, "timeoutSeconds", out var timeoutSeconds))
            {
                return Invalid("timeout_invalid", "The timeout value is invalid.");
            }

            if (!TryNullableString(element, "policySet", out var policySetValue))
            {
                return Invalid(
                    "policy_set_invalid",
                    "The closest policy set must be G, B, or missing for bounded fallback.");
            }

            var policySet = default(PolicySetCode);
            var policySetIsMissing = string.IsNullOrWhiteSpace(policySetValue);
            var hasPolicySet = !policySetIsMissing
                && TryParsePolicySet(policySetValue!, out policySet);
            if (!policySetIsMissing && !hasPolicySet)
            {
                return Invalid(
                    "policy_set_invalid",
                    "The selected policy set is unknown.");
            }
            var hasRule = TryNullableString(element, "ruleCode", out var ruleCode)
                && !string.IsNullOrWhiteSpace(ruleCode);
            var usedRuleFallback = false;
            ModerationRuleDefinition? rule;
            if (hasRule)
            {
                rule = policy.FindRule(ruleCode);
                if (rule is null)
                {
                    return Invalid("rule_unknown", "The selected rule is not active.");
                }

                if (hasPolicySet && rule.PolicySet != policySet)
                {
                    return Invalid(
                        "rule_policy_mismatch",
                        "The selected rule does not belong to the selected policy set.");
                }

                if (!hasPolicySet && !allowMissingRuleFallback)
                {
                    return Invalid(
                        "policy_set_missing",
                        "The closest policy set is missing.");
                }

                policySet = rule.PolicySet;
                hasPolicySet = true;
            }
            else if (allowMissingRuleFallback)
            {
                if (!hasPolicySet)
                {
                    var matchedRule = request.Message.LocalMatchCodes
                        .Select(policy.FindRule)
                        .Where(item => item is not null)
                        .OrderBy(item => item!.Priority)
                        .ThenBy(item => item!.RuleCode, StringComparer.Ordinal)
                        .FirstOrDefault();
                    policySet = matchedRule?.PolicySet ?? PolicySetCode.General;
                    hasPolicySet = true;
                }

                rule = ModerationRuleFallbackResolver.Resolve(
                    policy,
                    policySet,
                    localMatchCodes: request.Message.LocalMatchCodes);
                if (rule is null)
                {
                    return Invalid(
                        "rule_missing",
                        "No active fallback rule is available.");
                }

                ruleCode = rule.RuleCode;
                usedRuleFallback = true;
            }
            else
            {
                return Invalid("rule_missing", "The closest active rule is missing.");
            }

            if (!hasPolicySet)
            {
                return Invalid("policy_set_missing", "The closest policy set is missing.");
            }

            if (!TryString(element, "reason", out var reason))
            {
                return Invalid("reason_invalid", "The reason must be a string.");
            }

            var evidenceKind = element.TryGetProperty("evidenceKind", out var evidenceElement)
                && evidenceElement.ValueKind == JsonValueKind.String
                ? evidenceElement.GetString()
                : null;
            var sensitiveDataType = element.TryGetProperty(
                    "sensitiveDataType", out var sensitiveElement)
                && sensitiveElement.ValueKind == JsonValueKind.String
                ? sensitiveElement.GetString()
                : null;
            if (verdict == LearningVerdict.Moderate
                && !ModerationEvidenceKinds.IsValid(rule!.RuleCode, evidenceKind))
            {
                return Invalid(
                    "evidence_kind_invalid",
                    "A moderate verdict requires a rule-compatible evidenceKind.");
            }

            if (verdict == LearningVerdict.Moderate
                && string.Equals(rule!.RuleCode, ModerationRuleCodes.PersonalData, StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(sensitiveDataType))
            {
                return Invalid("sensitive_data_type_missing", "Personal data requires sensitiveDataType.");
            }

            if (!TryTriggers(
                    element,
                    request.Message.LowerText,
                    rule!.RuleCode,
                    verdict,
                    out var triggers,
                    out var triggerError))
            {
                return Invalid(triggerError!);
            }

            if (!element.TryGetProperty("confidence", out var confidenceElement)
                || confidenceElement.ValueKind != JsonValueKind.Number
                || !confidenceElement.TryGetDouble(out var confidence)
                || !double.IsFinite(confidence)
                || confidence is < 0 or > 1)
            {
                return Invalid("confidence_invalid", "Confidence must be between zero and one.");
            }

            (action, timeoutSeconds) =
                ModerationActionRules.NormalizeAiSuggestionForRule(
                    action,
                    timeoutSeconds,
                    rule.RuleCode);

            if (verdict == LearningVerdict.Moderate
                && string.Equals(
                    rule.RuleCode,
                    ModerationRuleCodes.Insult,
                    StringComparison.Ordinal)
                && triggers.Count == 0)
            {
                // Missing insult evidence is a precision failure, not a reason
                // for another inference. Keep the closest category for manual
                // learning, but present the model verdict as allow.
                verdict = LearningVerdict.Allow;
                action = ModerationActionKind.None;
                timeoutSeconds = null;
                reason = string.Empty;
            }

            if (verdict == LearningVerdict.Allow)
            {
                if (action != ModerationActionKind.None
                    || timeoutSeconds is not null
                    || reason.Length != 0
                    || triggers.Count != 0)
                {
                    return Invalid(
                        "allow_payload_invalid",
                        "An allow decision must not contain a moderation action, reason, timeout, or triggers.");
                }
            }
            else
            {
                if (action == ModerationActionKind.None
                    || !ModerationActionRules.IsValidAiRuleSuggestion(
                        action, timeoutSeconds, rule.RuleCode))
                {
                    return Invalid(
                        "moderation_action_invalid",
                        "The moderation action is not valid for the selected rule.");
                }

                if (string.Equals(
                        rule.RuleCode,
                        ModerationRuleCodes.Backseatgaming,
                        StringComparison.Ordinal)
                    && !HasMinimumBackseatContext(request.Message.LowerText))
                {
                    return Invalid(
                        "backseat_context_insufficient",
                        "Backseatgaming requires a concrete multi-word statement and cannot be inferred from a question or fragment.");
                }

                if (string.Equals(
                        rule.RuleCode,
                        ModerationRuleCodes.Backseatgaming,
                        StringComparison.Ordinal)
                    && (triggers.Count == 0
                        || triggers.Any(
                            phrase => CountLexicalTokens(phrase) < 2)))
                {
                    return Invalid(
                        "backseat_evidence_insufficient",
                        "Backseatgaming requires at least one concrete multi-word trigger phrase.");
                }

                reason = ModerationReasonNormalizer.Normalize(
                    reason,
                    rule,
                    rule.PolicySet);
            }

            return new LearningEnvelopeValidationResult(
                new ValidatedLearningTestDecision(
                    verdict,
                    action,
                    timeoutSeconds,
                    rule.PolicySet,
                    rule.RuleCode,
                    reason,
                    triggers,
                    confidence,
                    usedRuleFallback,
                    evidenceKind,
                    sensitiveDataType),
                []);
        }
        catch (JsonException exception)
        {
            return Invalid("json_invalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid("json_shape_invalid", exception.Message);
        }
    }

    private static bool TryTriggers(
        JsonElement decision,
        string lowerText,
        string ruleCode,
        LearningVerdict verdict,
        out IReadOnlyList<string> triggers,
        out LearningEnvelopeValidationError? error)
    {
        triggers = [];
        error = null;
        if (!decision.TryGetProperty("triggerPhrases", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            error = new(
                "triggers_invalid",
                "Trigger phrases must be an array.");
            return false;
        }

        if (verdict == LearningVerdict.Allow)
        {
            // Evidence is irrelevant for an allow verdict. The original model
            // response remains available in validation diagnostics.
            return true;
        }

        var isBackseat = string.Equals(
            ruleCode,
            ModerationRuleCodes.Backseatgaming,
            StringComparison.Ordinal);
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || item.GetString() is not { Length: > 0 } value
                || !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
                || !lowerText.Contains(value, StringComparison.Ordinal)
                || !seen.Add(value))
            {
                if (isBackseat)
                {
                    error = new(
                        "trigger_invalid",
                        "Backseat evidence must be one unique exact lowercase substring.");
                    return false;
                }

                continue;
            }

            if (values.Count < 32)
            {
                values.Add(value);
            }
        }

        if (!ModerationRuleCodes.AllowsTriggerLearning(ruleCode)
            && values.Count != 0)
        {
            return true;
        }

        if (isBackseat)
        {
            if (values.Count != 1
                || !LearningTriggerPhraseFilter.IsContextual(values[0]))
            {
                error = new(
                    "backseat_evidence_insufficient",
                    "Backseatgaming requires one concrete multi-word evidence phrase.");
                return false;
            }

            triggers = values;
            return true;
        }

        triggers = ModerationTriggerEvidenceNormalizer.Normalize(
            lowerText,
            ruleCode,
            values);
        return true;
    }

    private static bool HasExactProperties(
        JsonElement element,
        HashSet<string> expected)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !found.Add(property.Name))
            {
                return false;
            }
        }

        return found.SetEquals(expected);
    }

    private static bool TryString(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()!) is not null;
    }

    private static bool TryNullableString(
        JsonElement element,
        string name,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryNullableInt32(
        JsonElement element,
        string name,
        out int? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var integer))
        {
            return false;
        }

        value = integer;
        return true;
    }

    private static bool TryParseVerdict(
        string value,
        out LearningVerdict verdict)
    {
        verdict = value switch
        {
            "allow" => LearningVerdict.Allow,
            "moderate" => LearningVerdict.Moderate,
            _ => default,
        };
        return value is "allow" or "moderate";
    }

    private static bool TryParseAction(
        string value,
        out ModerationActionKind action)
    {
        action = value switch
        {
            "none" => ModerationActionKind.None,
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            _ => default,
        };
        return value is "none" or "delete" or "timeout" or "ban";
    }

    private static bool TryParsePolicySet(
        string value,
        out PolicySetCode policySet)
    {
        policySet = value switch
        {
            "G" => PolicySetCode.General,
            "B" => PolicySetCode.Backseatgaming,
            _ => default,
        };
        return value is "G" or "B";
    }

    private static bool HasMinimumBackseatContext(string lowerText)
    {
        var text = lowerText.AsSpan().Trim();
        if (text.IsEmpty
            || text.Contains('?')
            || text.Contains('¿')
            || text.Contains('？'))
        {
            return false;
        }

        return CountLexicalTokens(text) >= 2;
    }

    private static int CountLexicalTokens(string text)
    {
        return CountLexicalTokens(text.AsSpan());
    }

    private static int CountLexicalTokens(ReadOnlySpan<char> text)
    {
        var count = 0;
        var insideToken = false;
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!insideToken)
                {
                    count++;
                }

                insideToken = true;
            }
            else
            {
                insideToken = false;
            }
        }

        return count;
    }

    private static LearningEnvelopeValidationResult Invalid(
        string code,
        string message) =>
        new(
            null,
            [new LearningEnvelopeValidationError(code, message)]);

    private static LearningEnvelopeValidationResult Invalid(
        LearningEnvelopeValidationError error) =>
        new(null, [error]);
}
