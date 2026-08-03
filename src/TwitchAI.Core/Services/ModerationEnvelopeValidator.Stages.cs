using System.Text.Json;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed partial class ModerationEnvelopeValidator
{
    private static readonly HashSet<string> StageRootProperties =
        Set("envelopeVersion", "batchId", "decisions");
    private static readonly HashSet<string> ClassificationModerationProperties =
        Set(
            "action", "timeoutSeconds", "ruleCode", "evidenceKind",
            "sensitiveDataType", "confidence");
    private static readonly HashSet<string> ClassificationRequiredProperties =
        Set("action", "ruleCode", "evidenceKind", "confidence");
    private static readonly HashSet<string> ClassificationReplyProperties =
        Set("replyTemplateId", "confidence");
    private static readonly HashSet<string> ConfirmationProperties =
        Set(
            "evidenceKind", "sensitiveDataType", "violationReason",
            "guidance", "triggerPhrases", "confidence");
    private static readonly HashSet<string> ConfirmationRequiredProperties =
        Set(
            "evidenceKind", "violationReason", "guidance",
            "triggerPhrases", "confidence");

    public ModerationClassificationReport ValidateClassification(
        string rawJson,
        ModerationClassificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(request);
        if (rawJson.Length > MaximumRawResponseCharacters)
        {
            return FullClassificationRepair(
                request,
                "response_too_large",
                "The classification response exceeds the safety limit.");
        }

        try
        {
            using var document = ParseStrictJson(rawJson);
            var root = document.RootElement;
            var rootError = ValidateStageRoot(
                root,
                request.Envelope.BatchId,
                ModerationPromptCompiler.ClassificationResponseEnvelopeVersion);
            if (rootError is not null)
            {
                return FullClassificationRepair(
                    request,
                    rootError.Code,
                    rootError.Message);
            }

            var sourceById = request.Envelope.Messages.ToDictionary(
                message => message.MessageId,
                StringComparer.Ordinal);
            if (sourceById.Count != request.Envelope.Messages.Count)
            {
                return FullClassificationRepair(
                    request,
                    "message_snapshot_invalid",
                    "Classification message IDs must be unique.");
            }

            var replyOnlyIds = request.ReplyOnlyMessageIds
                .ToHashSet(StringComparer.Ordinal);
            var replyTemplates = request.Envelope.ReplyTemplates
                .ToDictionary(template => template.TemplateId, StringComparer.Ordinal);
            var rulesByCode = request.Envelope.ActiveRules
                .ToDictionary(rule => rule.RuleCode, StringComparer.Ordinal);
            var activePolicySets = request.Envelope.ActivePolicySets
                .Select(policy => policy.Code)
                .ToHashSet();
            var decisionsElement = root.GetProperty("decisions");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var validById = new Dictionary<string, ModerationClassificationDecision>(
                StringComparer.Ordinal);
            var invalidIds = new HashSet<string>(StringComparer.Ordinal);
            var issues = new List<ModerationValidationIssue>();
            var fullRepair = false;

            foreach (var property in decisionsElement.EnumerateObject())
            {
                var messageId = property.Name;
                if (!sourceById.ContainsKey(messageId))
                {
                    issues.Add(
                        new(
                            "message_id_unknown",
                            $"Classification ID '{messageId}' is not part of the request."));
                    fullRepair = true;
                    continue;
                }

                if (!seenIds.Add(messageId))
                {
                    AddClassificationIssue(
                        messageId,
                        "message_id_duplicate",
                        $"Classification ID '{messageId}' occurs more than once.",
                        invalidIds,
                        validById,
                        issues);
                    continue;
                }

                var source = sourceById[messageId];
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    if (!replyOnlyIds.Contains(messageId)
                        && RepeatedPhraseSpamDetector.IsSpam(source.LowerText))
                    {
                        AddClassificationIssue(
                            messageId,
                            "repeated_phrase_spam_missed",
                            $"Classification '{messageId}' missed deterministic repeated-phrase spam.",
                            invalidIds,
                            validById,
                            issues);
                        continue;
                    }

                    validById[messageId] = NoActionClassification(messageId);
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    AddClassificationIssue(
                        messageId,
                        "decision_not_object",
                        $"Classification '{messageId}' must be null or an object.",
                        invalidIds,
                        validById,
                        issues);
                    continue;
                }

                var result = property.Value.TryGetProperty("replyTemplateId", out _)
                    ? ValidateClassificationReply(
                        property.Value,
                        messageId,
                        replyTemplates)
                    : ValidateClassificationModeration(
                        property.Value,
                        messageId,
                        source,
                        rulesByCode,
                        activePolicySets,
                        replyOnlyIds.Contains(messageId));
                if (result.Decision is not null)
                {
                    validById[messageId] = result.Decision;
                }
                else
                {
                    AddClassificationIssue(
                        messageId,
                        result.Code!,
                        result.Message!,
                        invalidIds,
                        validById,
                        issues);
                }
            }

            foreach (var messageId in sourceById.Keys.Where(id => !seenIds.Contains(id)))
            {
                AddClassificationIssue(
                    messageId,
                    "message_id_missing",
                    $"Classification ID '{messageId}' is missing.",
                    invalidIds,
                    validById,
                    issues);
            }

            var validDecisions = request.Envelope.Messages
                .Where(message => validById.ContainsKey(message.MessageId))
                .Select(message => validById[message.MessageId])
                .ToArray();
            if (issues.Count == 0)
            {
                return new(
                    new ModerationClassificationEnvelope(
                        request.Envelope.BatchId,
                        validDecisions),
                    validDecisions,
                    [],
                    [],
                    RequiresFullRepair: false);
            }

            var repairIds = fullRepair
                ? request.Envelope.Messages.Select(message => message.MessageId).ToArray()
                : request.Envelope.Messages
                    .Select(message => message.MessageId)
                    .Where(invalidIds.Contains)
                    .ToArray();
            return new(
                Envelope: null,
                validDecisions,
                issues,
                repairIds,
                fullRepair);
        }
        catch (JsonException exception)
        {
            return FullClassificationRepair(
                request,
                "json_invalid",
                $"The classification response is invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return FullClassificationRepair(
                request,
                "envelope_invalid",
                exception.Message);
        }
    }

    public ModerationConfirmationReport ValidateConfirmation(
        string rawJson,
        ModerationConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(request);
        var allIds = request.Candidates
            .Select(candidate => candidate.TemporaryMessageId)
            .ToArray();
        if (rawJson.Length > MaximumRawResponseCharacters)
        {
            return InvalidConfirmationRoot(
                allIds,
                "response_too_large",
                "The confirmation response exceeds the safety limit.");
        }

        try
        {
            using var document = ParseStrictJson(rawJson);
            var root = document.RootElement;
            var rootError = ValidateStageRoot(
                root,
                request.Envelope.BatchId,
                ModerationPromptCompiler.ConfirmationResponseEnvelopeVersion);
            if (rootError is not null)
            {
                return InvalidConfirmationRoot(
                    allIds,
                    rootError.Code,
                    rootError.Message);
            }

            var sourceById = request.Envelope.Messages.ToDictionary(
                message => message.MessageId,
                StringComparer.Ordinal);
            var candidateById = request.Candidates.ToDictionary(
                candidate => candidate.TemporaryMessageId,
                StringComparer.Ordinal);
            var ruleByCode = request.Envelope.ActiveRules.ToDictionary(
                rule => rule.RuleCode,
                StringComparer.Ordinal);
            var decisionsElement = root.GetProperty("decisions");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var confirmed = new List<ValidatedModerationDecision>();
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            var issues = new List<ModerationValidationIssue>();

            foreach (var property in decisionsElement.EnumerateObject())
            {
                var messageId = property.Name;
                if (!candidateById.TryGetValue(messageId, out var candidate))
                {
                    return InvalidConfirmationRoot(
                        allIds,
                        "message_id_unknown",
                        $"Confirmation ID '{messageId}' is not part of the request.");
                }

                if (!seenIds.Add(messageId))
                {
                    rejected.Add(messageId);
                    issues.Add(
                        new(
                            "message_id_duplicate",
                            $"Confirmation ID '{messageId}' occurs more than once.",
                            messageId));
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    rejected.Add(messageId);
                    continue;
                }

                if (!sourceById.TryGetValue(messageId, out var source)
                    || !ruleByCode.TryGetValue(candidate.RuleCode, out var rule))
                {
                    rejected.Add(messageId);
                    issues.Add(
                        new(
                            "candidate_snapshot_invalid",
                            $"Confirmation candidate '{messageId}' has no active source or rule.",
                            messageId));
                    continue;
                }

                var validation = ValidateConfirmationDecision(
                    property.Value,
                    candidate,
                    source,
                    rule);
                if (validation.Decision is not null)
                {
                    confirmed.Add(validation.Decision);
                }
                else
                {
                    rejected.Add(messageId);
                    issues.Add(
                        new(
                            validation.Code!,
                            validation.Message!,
                            messageId));
                }
            }

            foreach (var messageId in allIds.Where(id => !seenIds.Contains(id)))
            {
                rejected.Add(messageId);
                issues.Add(
                    new(
                        "message_id_missing",
                        $"Confirmation ID '{messageId}' is missing.",
                        messageId));
            }

            return new(
                IsEnvelopeValid: true,
                confirmed,
                allIds.Where(rejected.Contains).ToArray(),
                issues);
        }
        catch (JsonException exception)
        {
            return InvalidConfirmationRoot(
                allIds,
                "json_invalid",
                $"The confirmation response is invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return InvalidConfirmationRoot(
                allIds,
                "envelope_invalid",
                exception.Message);
        }
    }

    private static ClassificationDecisionValidation ValidateClassificationReply(
        JsonElement element,
        string messageId,
        Dictionary<string, ChatReplyTemplatePrompt> replyTemplates)
    {
        var shape = ValidateObjectShape(
            element,
            ClassificationReplyProperties,
            ClassificationReplyProperties,
            "classification reply");
        if (shape is not null)
        {
            return ClassificationDecisionValidation.Invalid(shape.Code, shape.Message);
        }

        if (!TryGetRequiredString(element, "replyTemplateId", out var templateId)
            || !replyTemplates.ContainsKey(templateId))
        {
            return ClassificationDecisionValidation.Invalid(
                "reply_template_invalid",
                $"Classification '{messageId}' selected an unknown reply template.");
        }

        if (!TryReadConfidence(element, out var confidence))
        {
            return ClassificationDecisionValidation.Invalid(
                "confidence_invalid",
                $"Classification '{messageId}' requires confidence between zero and one.");
        }

        return ClassificationDecisionValidation.Valid(
            NoActionClassification(messageId) with
            {
                Confidence = confidence,
                ReplyTemplateId = templateId,
                ReplyConfidence = confidence,
            });
    }

    private static ClassificationDecisionValidation ValidateClassificationModeration(
        JsonElement element,
        string messageId,
        ModerationRequestMessageV3 source,
        Dictionary<string, ModerationRuleDefinition> rulesByCode,
        HashSet<PolicySetCode> activePolicySets,
        bool isReplyOnly)
    {
        if (isReplyOnly)
        {
            return ClassificationDecisionValidation.Invalid(
                "exact_allow_overridden",
                $"Reply-only classification '{messageId}' cannot be moderated.");
        }

        var shape = ValidateObjectShape(
            element,
            ClassificationModerationProperties,
            ClassificationRequiredProperties,
            "classification moderation");
        if (shape is not null)
        {
            return ClassificationDecisionValidation.Invalid(shape.Code, shape.Message);
        }

        if (!TryGetRequiredString(element, "action", out var actionText)
            || !TryParseAction(actionText, out var action)
            || action == ModerationActionKind.None)
        {
            return ClassificationDecisionValidation.Invalid(
                "action_invalid",
                $"Classification '{messageId}' requires delete, timeout, or ban.");
        }

        var hasTimeout = element.TryGetProperty("timeoutSeconds", out var timeoutElement);
        if (action == ModerationActionKind.Timeout && !hasTimeout
            || action != ModerationActionKind.Timeout && hasTimeout)
        {
            return ClassificationDecisionValidation.Invalid(
                "timeout_invalid",
                $"Classification '{messageId}' has an invalid timeout shape.");
        }

        int? timeoutSeconds = hasTimeout
            && timeoutElement.ValueKind == JsonValueKind.Number
            && timeoutElement.TryGetInt32(out var parsedTimeout)
                ? parsedTimeout
                : hasTimeout
                    ? int.MinValue
                    : null;
        if (!TryGetRequiredString(element, "ruleCode", out var ruleCode)
            || !rulesByCode.TryGetValue(ruleCode, out var rule)
            || !activePolicySets.Contains(rule.PolicySet))
        {
            return ClassificationDecisionValidation.Invalid(
                "rule_invalid",
                $"Classification '{messageId}' requires an active rule.");
        }

        if (ModerationRuleCodes.RequiresLocalPhraseMatch(rule.RuleCode)
            && !source.LocalMatchCodes.Contains(rule.RuleCode, StringComparer.Ordinal))
        {
            return ClassificationDecisionValidation.Invalid(
                "phrase_gate_missing",
                $"Classification '{messageId}' selected a phrase-gated rule without a local match.");
        }

        (action, timeoutSeconds) = ModerationActionRules.NormalizeAiSuggestionForRule(
            action,
            timeoutSeconds,
            rule.RuleCode);
        if (!ModerationActionRules.IsValidAiRuleSuggestion(
                action,
                timeoutSeconds,
                rule.RuleCode))
        {
            return ClassificationDecisionValidation.Invalid(
                "action_rule_invalid",
                $"Classification '{messageId}' selected an invalid action for {rule.RuleCode}.");
        }

        if (!TryGetRequiredString(element, "evidenceKind", out var evidenceKind)
            || !ModerationEvidenceKinds.IsValid(rule.RuleCode, evidenceKind))
        {
            return ClassificationDecisionValidation.Invalid(
                "evidence_kind_invalid",
                $"Classification '{messageId}' requires rule-compatible evidence.");
        }

        var sensitiveDataType = ReadOptionalStageString(element, "sensitiveDataType");
        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.PersonalData,
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(sensitiveDataType))
        {
            return ClassificationDecisionValidation.Invalid(
                "sensitive_data_type_missing",
                $"Classification '{messageId}' requires sensitiveDataType.");
        }

        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal)
            && !HasMinimumBackseatContext(source.LowerText))
        {
            return ClassificationDecisionValidation.Invalid(
                "backseat_context_insufficient",
                $"Classification '{messageId}' lacks a concrete multi-word directive.");
        }

        if (source.IsTwitchEmoteOnly == true
            && source.TwitchEmoteCount is > 0
                and < ModerationRuleCodes.MinimumEmoteSpamCount)
        {
            return ClassificationDecisionValidation.Invalid(
                "emote_only_below_spam_threshold",
                $"Classification '{messageId}' must be noAction below the emote threshold.");
        }

        if (!TryReadConfidence(element, out var confidence))
        {
            return ClassificationDecisionValidation.Invalid(
                "confidence_invalid",
                $"Classification '{messageId}' requires confidence between zero and one.");
        }

        return ClassificationDecisionValidation.Valid(
            new ModerationClassificationDecision(
                messageId,
                action,
                timeoutSeconds,
                rule.RuleCode,
                evidenceKind,
                sensitiveDataType,
                confidence));
    }

    private static ConfirmationDecisionValidation ValidateConfirmationDecision(
        JsonElement element,
        ModerationConfirmationCandidate candidate,
        ModerationRequestMessageV3 source,
        ModerationRuleDefinition rule)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ConfirmationDecisionValidation.Invalid(
                "decision_not_object",
                $"Confirmation '{candidate.TemporaryMessageId}' must be null or an object.");
        }

        var required = string.Equals(
            candidate.RuleCode,
            ModerationRuleCodes.PersonalData,
            StringComparison.Ordinal)
            ? Set(ConfirmationRequiredProperties.Append("sensitiveDataType").ToArray())
            : ConfirmationRequiredProperties;
        var allowed = string.Equals(
            candidate.RuleCode,
            ModerationRuleCodes.PersonalData,
            StringComparison.Ordinal)
            ? ConfirmationProperties
            : Set(ConfirmationProperties
                .Where(property => !string.Equals(
                    property,
                    "sensitiveDataType",
                    StringComparison.Ordinal))
                .ToArray());
        var shape = ValidateObjectShape(element, allowed, required, "confirmation");
        if (shape is not null)
        {
            return ConfirmationDecisionValidation.Invalid(shape.Code, shape.Message);
        }

        if (!TryGetRequiredString(element, "evidenceKind", out var evidenceKind)
            || !ModerationEvidenceKinds.IsValid(rule.RuleCode, evidenceKind))
        {
            return ConfirmationDecisionValidation.Invalid(
                "evidence_kind_invalid",
                $"Confirmation '{candidate.TemporaryMessageId}' requires rule-compatible evidence.");
        }

        var sensitiveDataType = ReadOptionalStageString(element, "sensitiveDataType");
        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.PersonalData,
                StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(sensitiveDataType))
        {
            return ConfirmationDecisionValidation.Invalid(
                "sensitive_data_type_missing",
                $"Confirmation '{candidate.TemporaryMessageId}' requires sensitiveDataType.");
        }

        var triggerResult = ParseTriggerPhrases(
            element.GetProperty("triggerPhrases"),
            source.LowerText,
            candidate.Action);
        if (triggerResult.Error is not null)
        {
            return ConfirmationDecisionValidation.Invalid(
                triggerResult.Error.Code,
                $"Confirmation '{candidate.TemporaryMessageId}': {triggerResult.Error.Message}");
        }

        var triggers = triggerResult.TriggerPhrases!;
        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.PersonalData,
                StringComparison.Ordinal))
        {
            if (triggers.Count != 0)
            {
                return ConfirmationDecisionValidation.Invalid(
                    "triggers_learning_forbidden",
                    $"Confirmation '{candidate.TemporaryMessageId}' must not expose personal data as trigger evidence.");
            }
        }
        else if (string.Equals(
                     rule.RuleCode,
                     ModerationRuleCodes.Backseatgaming,
                     StringComparison.Ordinal))
        {
            if (!HasMinimumBackseatContext(source.LowerText)
                || triggers.Count == 0
                || triggers.Any(trigger => CountLexicalTokens(trigger) < 2))
            {
                return ConfirmationDecisionValidation.Invalid(
                    "backseat_evidence_insufficient",
                    $"Confirmation '{candidate.TemporaryMessageId}' requires concrete multi-word backseatgaming evidence.");
            }
        }
        else
        {
            triggers = ModerationTriggerEvidenceNormalizer.Normalize(
                source.LowerText,
                rule.RuleCode,
                triggers);
        }

        var violationReason = ReadOptionalStageString(element, "violationReason");
        var guidance = ReadOptionalStageString(element, "guidance");
        if (!ModerationReasonComposer.TryComposeAiReason(
                violationReason,
                guidance,
                candidate.Action,
                rule,
                source.LowerText,
                triggers,
                out var composition,
                out var reasonErrorCode))
        {
            return ConfirmationDecisionValidation.Invalid(
                reasonErrorCode,
                $"Confirmation '{candidate.TemporaryMessageId}' has an invalid reason or guidance.");
        }

        if (!TryReadConfidence(element, out var confirmationConfidence))
        {
            return ConfirmationDecisionValidation.Invalid(
                "confidence_invalid",
                $"Confirmation '{candidate.TemporaryMessageId}' requires confidence between zero and one.");
        }

        var confidence = candidate.Source == ModerationCandidateSource.SemanticClassification
            ? Math.Min(candidate.Confidence, confirmationConfidence)
            : confirmationConfidence;
        var decision = new ValidatedModerationDecision(
            candidate.TemporaryMessageId,
            candidate.Action,
            candidate.TimeoutSeconds,
            candidate.PolicySet,
            composition.Reason,
            triggers,
            confidence,
            candidate.RuleCode)
        {
            EvidenceKind = evidenceKind,
            SensitiveDataType = sensitiveDataType,
        };
        return ConfirmationDecisionValidation.Valid(decision);
    }

    private static JsonDocument ParseStrictJson(string rawJson) =>
        JsonDocument.Parse(
            rawJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });

    private static EnvelopeValidationError? ValidateStageRoot(
        JsonElement root,
        Guid expectedBatchId,
        string expectedVersion)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new("root_not_object", "The stage envelope must be a JSON object.");
        }

        var shape = ValidateObjectShape(
            root,
            StageRootProperties,
            StageRootProperties,
            "stage root");
        if (shape is not null)
        {
            return shape;
        }

        if (!TryGetRequiredString(root, "envelopeVersion", out var version)
            || !string.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            return new("version_invalid", "The stage response version is invalid.");
        }

        if (!TryGetRequiredString(root, "batchId", out var batchIdText)
            || !Guid.TryParse(batchIdText, out var batchId)
            || batchId != expectedBatchId)
        {
            return new("batch_id_invalid", "The stage response batch ID does not match.");
        }

        if (root.GetProperty("decisions").ValueKind != JsonValueKind.Object)
        {
            return new("decisions_invalid", "Stage decisions must be keyed by message ID.");
        }

        return null;
    }

    private static bool TryReadConfidence(JsonElement element, out double confidence)
    {
        confidence = 0;
        return element.TryGetProperty("confidence", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out confidence)
            && double.IsFinite(confidence)
            && confidence is >= 0 and <= 1;
    }

    private static string? ReadOptionalStageString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static ModerationClassificationDecision NoActionClassification(
        string messageId) =>
        new(
            messageId,
            ModerationActionKind.None,
            null,
            null,
            null,
            null,
            0d);

    private static void AddClassificationIssue(
        string messageId,
        string code,
        string message,
        HashSet<string> invalidIds,
        Dictionary<string, ModerationClassificationDecision> validById,
        List<ModerationValidationIssue> issues)
    {
        invalidIds.Add(messageId);
        validById.Remove(messageId);
        issues.Add(new(code, message, messageId));
    }

    private static ModerationClassificationReport FullClassificationRepair(
        ModerationClassificationRequest request,
        string code,
        string message) =>
        new(
            Envelope: null,
            ValidDecisions: [],
            Issues: [new ModerationValidationIssue(code, message)],
            RepairMessageIds: request.Envelope.Messages
                .Select(item => item.MessageId)
                .ToArray(),
            RequiresFullRepair: true);

    private static ModerationConfirmationReport InvalidConfirmationRoot(
        IReadOnlyList<string> allIds,
        string code,
        string message) =>
        new(
            IsEnvelopeValid: false,
            ConfirmedDecisions: [],
            RejectedMessageIds: allIds,
            Issues: [new ModerationValidationIssue(code, message)]);

    private sealed record ClassificationDecisionValidation(
        ModerationClassificationDecision? Decision,
        string? Code,
        string? Message)
    {
        public static ClassificationDecisionValidation Valid(
            ModerationClassificationDecision decision) =>
            new(decision, null, null);

        public static ClassificationDecisionValidation Invalid(
            string code,
            string message) =>
            new(null, code, message);
    }

    private sealed record ConfirmationDecisionValidation(
        ValidatedModerationDecision? Decision,
        string? Code,
        string? Message)
    {
        public static ConfirmationDecisionValidation Valid(
            ValidatedModerationDecision decision) =>
            new(decision, null, null);

        public static ConfirmationDecisionValidation Invalid(
            string code,
            string message) =>
            new(null, code, message);
    }
}
