using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ModerationEnvelopeValidator : IModerationEnvelopeValidator
{
    public const string ResponseEnvelopeVersion = "twitchai.moderation.response.v3";
    private const int MaximumRawResponseCharacters = 262_144;
    private const int MaximumTriggerPhrases = 3;

    private static readonly HashSet<string> RootProperties =
        Set("envelopeVersion", "batchId", "decisions");

    private static readonly HashSet<string> DecisionProperties =
        Set(
            "messageId",
            "action",
            "timeoutSeconds",
            "policySet",
            "ruleCode",
            "reason",
            "triggerPhrases",
            "confidence");

    public EnvelopeValidationResult Validate(string rawJson, ModerationBatch batch) =>
        ValidateCore(rawJson, batch, allowMissingRuleFallback: false);

    public EnvelopeValidationResult ValidateWithMissingRuleFallback(
        string rawJson,
        ModerationBatch batch) =>
        ValidateCore(rawJson, batch, allowMissingRuleFallback: true);

    private static EnvelopeValidationResult ValidateCore(
        string rawJson,
        ModerationBatch batch,
        bool allowMissingRuleFallback)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(batch);
        if (rawJson.Length > MaximumRawResponseCharacters)
        {
            return Error("response_too_large", "The model response exceeds the safety limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error("root_not_object", "The moderation envelope must be a JSON object.");
            }

            var structureError = ValidateObjectShape(root, RootProperties, "root");
            if (structureError is not null)
            {
                return Error(structureError);
            }

            if (!TryGetRequiredString(root, "envelopeVersion", out var version)
                || version != ResponseEnvelopeVersion)
            {
                return Error("version_invalid", "The response envelope version is invalid.");
            }

            if (!TryGetRequiredString(root, "batchId", out var batchIdText)
                || !Guid.TryParse(batchIdText, out var batchId)
                || batchId != batch.BatchId)
            {
                return Error("batch_id_invalid", "The response batch ID does not match.");
            }

            var decisionsElement = root.GetProperty("decisions");
            if (decisionsElement.ValueKind != JsonValueKind.Array)
            {
                return Error("decisions_not_array", "Decisions must be a JSON array.");
            }

            var sourceById = batch.Messages.ToDictionary(
                item => item.TemporaryMessageId,
                StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var validated = new List<ValidatedModerationDecision>(sourceById.Count);

            foreach (var decisionElement in decisionsElement.EnumerateArray())
            {
                var decisionResult = ValidateDecision(
                    decisionElement,
                    sourceById,
                    batch.Context.EffectivePolicy,
                    seen,
                    allowMissingRuleFallback);
                if (decisionResult.Error is not null)
                {
                    return Error(decisionResult.Error);
                }

                validated.Add(decisionResult.Decision!);
            }

            if (validated.Count != sourceById.Count)
            {
                return Error(
                    "decision_set_incomplete",
                    "The response must contain exactly one decision for every batch message.");
            }

            return new EnvelopeValidationResult(
                new ValidatedModerationEnvelope(batch.BatchId, validated),
                []);
        }
        catch (JsonException exception)
        {
            return Error("json_invalid", $"The model response is invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Error("envelope_invalid", exception.Message);
        }
    }

    private static DecisionValidationResult ValidateDecision(
        JsonElement element,
        Dictionary<string, ModerationBatchMessage> sourceById,
        EffectivePolicySnapshot activePolicy,
        HashSet<string> seen,
        bool allowMissingRuleFallback)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return DecisionValidationResult.Invalid(
                "decision_not_object",
                "Every decision must be a JSON object.");
        }

        var structureError = ValidateObjectShape(element, DecisionProperties, "decision");
        if (structureError is not null)
        {
            return DecisionValidationResult.Invalid(structureError.Code, structureError.Message);
        }

        if (!TryGetRequiredString(element, "messageId", out var messageId)
            || !sourceById.TryGetValue(messageId, out var source))
        {
            return DecisionValidationResult.Invalid(
                "message_id_unknown",
                "A decision contains a missing or unknown temporary message ID.");
        }

        if (!seen.Add(messageId))
        {
            return DecisionValidationResult.Invalid(
                "message_id_duplicate",
                $"Temporary message ID '{messageId}' occurs more than once.");
        }

        if (!TryGetRequiredString(element, "action", out var actionText)
            || !TryParseAction(actionText, out var action))
        {
            return DecisionValidationResult.Invalid(
                "action_invalid",
                $"Decision '{messageId}' has an invalid action.");
        }

        var timeoutElement = element.GetProperty("timeoutSeconds");
        int? timeoutSeconds = timeoutElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when timeoutElement.TryGetInt32(out var value) => value,
            _ => int.MinValue,
        };
        if (!ModerationActionRules.IsValid(action, timeoutSeconds))
        {
            return DecisionValidationResult.Invalid(
                "timeout_invalid",
                $"Decision '{messageId}' has an invalid timeout value.");
        }

        var policyResult = ParsePolicySet(element.GetProperty("policySet"), action, activePolicy);
        if (policyResult.Error is not null)
        {
            return DecisionValidationResult.Invalid(
                policyResult.Error.Code,
                $"Decision '{messageId}': {policyResult.Error.Message}");
        }

        var ruleResult = ParseRuleCode(
            element.GetProperty("ruleCode"),
            action,
            policyResult.PolicySet,
            activePolicy,
            source.LocalMatches.Select(match => match.RuleCode),
            allowMissingRuleFallback);
        if (ruleResult.Error is not null)
        {
            return DecisionValidationResult.Invalid(
                ruleResult.Error.Code,
                $"Decision '{messageId}': {ruleResult.Error.Message}");
        }
        if (action != ModerationActionKind.None
            && string.Equals(
                ruleResult.Rule?.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal)
            && !HasMinimumBackseatContext(source.Message.Text.LowerText))
        {
            return DecisionValidationResult.Invalid(
                "backseat_context_insufficient",
                $"Decision '{messageId}': Backseatgaming requires a concrete multi-word statement and cannot be inferred from a question or fragment.");
        }

        var reasonElement = element.GetProperty("reason");
        string reason;
        if (action == ModerationActionKind.None)
        {
            if (reasonElement.ValueKind != JsonValueKind.String
                || !string.Equals(
                    reasonElement.GetString(),
                    string.Empty,
                    StringComparison.Ordinal))
            {
                return DecisionValidationResult.Invalid(
                    "reason_for_none",
                    $"Decision '{messageId}' must use an empty reason for none.");
            }

            reason = string.Empty;
        }
        else
        {
            var candidate = reasonElement.ValueKind == JsonValueKind.String
                && reasonElement.GetString() is { } text
                && !text.Any(char.IsControl)
                    ? text
                    : null;
            reason = ModerationReasonNormalizer.Normalize(
                candidate,
                ruleResult.Rule,
                policyResult.PolicySet);
        }

        var triggerResult = ParseTriggerPhrases(
            element.GetProperty("triggerPhrases"),
            source.Message.Text.LowerText,
            action);
        if (triggerResult.Error is not null)
        {
            return DecisionValidationResult.Invalid(
                triggerResult.Error.Code,
                $"Decision '{messageId}': {triggerResult.Error.Message}");
        }
        if (!ModerationRuleCodes.AllowsTriggerLearning(ruleResult.Rule?.RuleCode)
            && triggerResult.TriggerPhrases!.Count != 0)
        {
            return DecisionValidationResult.Invalid(
                "triggers_learning_forbidden",
                $"Decision '{messageId}': Personal data must not be proposed for trigger learning.");
        }
        if (string.Equals(
                ruleResult.Rule?.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal)
            && (triggerResult.TriggerPhrases!.Count == 0
                || triggerResult.TriggerPhrases.Any(
                    phrase => CountLexicalTokens(phrase) < 2)))
        {
            return DecisionValidationResult.Invalid(
                "backseat_evidence_insufficient",
                $"Decision '{messageId}': Backseatgaming requires at least one concrete multi-word trigger phrase.");
        }

        var confidenceElement = element.GetProperty("confidence");
        if (confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            return DecisionValidationResult.Invalid(
                "confidence_invalid",
                $"Decision '{messageId}' has an invalid confidence.");
        }

        return DecisionValidationResult.Valid(
            new ValidatedModerationDecision(
                messageId,
                action,
                timeoutSeconds,
                policyResult.PolicySet,
                reason,
                triggerResult.TriggerPhrases!,
                confidence,
                ruleResult.Rule?.RuleCode));
    }

    private static PolicyParseResult ParsePolicySet(
        JsonElement element,
        ModerationActionKind action,
        EffectivePolicySnapshot activePolicy)
    {
        if (action == ModerationActionKind.None)
        {
            return element.ValueKind == JsonValueKind.Null
                ? new PolicyParseResult(null, null)
                : new PolicyParseResult(
                    null,
                    new("policy_for_none", "A none decision must use a null policySet."));
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return new(
                null,
                new("policy_invalid", "An actionable decision requires policySet G or B."));
        }

        var value = element.GetString();
        var policy = value switch
        {
            "G" => PolicySetCode.General,
            "B" => PolicySetCode.Backseatgaming,
            _ => (PolicySetCode?)null,
        };
        if (policy is null || !activePolicy.Contains(policy.Value))
        {
            return new(
                null,
                new("policy_inactive", "The selected policySet is unknown or inactive."));
        }

        return new(policy, null);
    }

    private static RuleParseResult ParseRuleCode(
        JsonElement element,
        ModerationActionKind action,
        PolicySetCode? policySet,
        EffectivePolicySnapshot activePolicy,
        IEnumerable<string> localMatchCodes,
        bool allowMissingRuleFallback)
    {
        if (action == ModerationActionKind.None)
        {
            return element.ValueKind == JsonValueKind.Null
                ? new RuleParseResult(null, null)
                : new RuleParseResult(
                    null,
                    new("rule_for_none", "A none decision must use a null ruleCode."));
        }

        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            var isMissing = element.ValueKind == JsonValueKind.Null
                || (element.ValueKind == JsonValueKind.String
                    && string.IsNullOrWhiteSpace(element.GetString()));
            if (allowMissingRuleFallback && isMissing)
            {
                var fallback = ModerationRuleFallbackResolver.Resolve(
                    activePolicy,
                    policySet,
                    localMatchCodes: localMatchCodes);
                if (fallback is not null)
                {
                    return new(fallback, null);
                }
            }

            return new(
                null,
                new("rule_invalid", "An actionable decision requires an active ruleCode."));
        }

        var ruleCode = element.GetString()!;
        var rule = activePolicy.FindRule(ruleCode);
        if (rule is null)
        {
            return new(
                null,
                new("rule_inactive", "The selected ruleCode is unknown or inactive."));
        }

        if (rule.PolicySet != policySet)
        {
            return new(
                null,
                new("rule_policy_mismatch", "The ruleCode does not belong to policySet."));
        }

        return new(rule, null);
    }

    private static TriggerParseResult ParseTriggerPhrases(
        JsonElement element,
        string lowerSourceText,
        ModerationActionKind action)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return new(null, new("triggers_not_array", "triggerPhrases must be an array."));
        }

        var triggers = new List<string>(MaximumTriggerPhrases);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var triggerElement in element.EnumerateArray())
        {
            if (triggers.Count == MaximumTriggerPhrases)
            {
                return new(
                    null,
                    new("triggers_too_many", "At most three trigger phrases are allowed."));
            }

            if (triggerElement.ValueKind != JsonValueKind.String)
            {
                return new(
                    null,
                    new("trigger_invalid", "Every trigger phrase must be a string."));
            }

            var trigger = triggerElement.GetString()!;
            if (trigger.Length == 0
                || trigger.Length > lowerSourceText.Length
                || !string.Equals(
                    trigger,
                    trigger.ToLowerInvariant(),
                    StringComparison.Ordinal)
                || !lowerSourceText.Contains(trigger, StringComparison.Ordinal)
                || !seen.Add(trigger))
            {
                return new(
                    null,
                    new(
                        "trigger_invalid",
                        "Trigger phrases must be unique lowercase substrings of their source message."));
            }

            triggers.Add(trigger);
        }

        if (action == ModerationActionKind.None && triggers.Count != 0)
        {
            return new(
                null,
                new("triggers_for_none", "A none decision cannot propose trigger phrases."));
        }

        return new(triggers, null);
    }

    private static EnvelopeValidationError? ValidateObjectShape(
        JsonElement element,
        HashSet<string> expectedProperties,
        string location)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return new(
                    "property_duplicate",
                    $"Property '{property.Name}' is duplicated in {location}.");
            }

            if (!expectedProperties.Contains(property.Name))
            {
                return new(
                    "property_unknown",
                    $"Property '{property.Name}' is not allowed in {location}.");
            }
        }

        var missing = expectedProperties.FirstOrDefault(property => !seen.Contains(property));
        return missing is null
            ? null
            : new("property_missing", $"Property '{missing}' is missing in {location}.");
    }

    private static bool TryGetRequiredString(
        JsonElement parent,
        string propertyName,
        out string value)
    {
        var element = parent.GetProperty(propertyName);
        if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
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
            _ => (ModerationActionKind)(-1),
        };
        return Enum.IsDefined(action);
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

    private static int CountLexicalTokens(string text) =>
        CountLexicalTokens(text.AsSpan());

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

    private static EnvelopeValidationResult Error(
        string code,
        string message) =>
        EnvelopeValidationResult.Invalid(new EnvelopeValidationError(code, message));

    private static EnvelopeValidationResult Error(EnvelopeValidationError error) =>
        EnvelopeValidationResult.Invalid(error);

    private static HashSet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private sealed record DecisionValidationResult(
        ValidatedModerationDecision? Decision,
        EnvelopeValidationError? Error)
    {
        public static DecisionValidationResult Valid(ValidatedModerationDecision decision) =>
            new(decision, null);

        public static DecisionValidationResult Invalid(string code, string message) =>
            new(null, new(code, message));
    }

    private sealed record PolicyParseResult(
        PolicySetCode? PolicySet,
        EnvelopeValidationError? Error);

    private sealed record RuleParseResult(
        ModerationRuleDefinition? Rule,
        EnvelopeValidationError? Error);

    private sealed record TriggerParseResult(
        IReadOnlyList<string>? TriggerPhrases,
        EnvelopeValidationError? Error);
}
