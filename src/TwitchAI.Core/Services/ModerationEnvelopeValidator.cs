using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ModerationEnvelopeValidator : IModerationEnvelopeValidator
{
    public const string ResponseEnvelopeVersion = "twitchai.moderation.response.v4";
    public const string SparseResponseEnvelopeVersion =
        "twitchai.moderation.response.v6";
    public const string KeyedResponseEnvelopeVersion =
        ModerationPromptCompiler.ResponseEnvelopeVersion;
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
            "evidenceKind",
            "sensitiveDataType",
            "reason",
            "triggerPhrases",
            "confidence",
            "replyTemplateId",
            "replyConfidence");

    private static readonly HashSet<string> SparseRootProperties =
        Set(
            "envelopeVersion",
            "batchId",
            "noActionMessageIds",
            "moderations",
            "replies");

    private static readonly HashSet<string> RequiredSparseRootProperties =
        Set(
            "envelopeVersion",
            "batchId",
            "noActionMessageIds",
            "moderations");

    private static readonly HashSet<string> SparseModerationProperties =
        Set(
            "messageId",
            "action",
            "timeoutSeconds",
            "ruleCode",
            "evidenceKind",
            "sensitiveDataType",
            "reason",
            "triggerPhrases",
            "confidence");

    private static readonly HashSet<string> RequiredSparseModerationProperties =
        Set(
            "messageId",
            "action",
            "reason",
            "triggerPhrases",
            "confidence");

    private static readonly HashSet<string> SparseReplyProperties =
        Set("messageId", "replyTemplateId", "confidence");

    private static readonly HashSet<string> KeyedRootProperties =
        Set("envelopeVersion", "batchId", "decisions");

    private static readonly HashSet<string> KeyedModerationProperties =
        Set(
            "action",
            "timeoutSeconds",
            "ruleCode",
            "evidenceKind",
            "sensitiveDataType",
            "violationReason",
            "guidance",
            "triggerPhrases",
            "confidence");

    private static readonly HashSet<string> RequiredKeyedModerationProperties =
        Set("action", "ruleCode", "evidenceKind", "violationReason", "guidance", "triggerPhrases", "confidence");

    private static readonly HashSet<string> KeyedReplyProperties =
        Set("replyTemplateId", "confidence");

    public EnvelopeValidationResult Validate(string rawJson, ModerationBatch batch)
    {
        if (IsCompactResponse(rawJson))
        {
            return ToEnvelopeValidationResult(
                ValidateCompactCore(rawJson, batch, allowMissingRuleFallback: false));
        }

        return ValidateCore(rawJson, batch, allowMissingRuleFallback: false);
    }

    public EnvelopeValidationResult ValidateWithMissingRuleFallback(
        string rawJson,
        ModerationBatch batch)
    {
        if (IsCompactResponse(rawJson))
        {
            return ToEnvelopeValidationResult(
                ValidateCompactCore(rawJson, batch, allowMissingRuleFallback: true));
        }

        return ValidateCore(rawJson, batch, allowMissingRuleFallback: true);
    }

    public ModerationValidationReport ValidateDetailed(
        string rawJson,
        ModerationBatch batch) =>
        IsCompactResponse(rawJson)
            ? ValidateCompactCore(rawJson, batch, allowMissingRuleFallback: true)
            : ValidateV4(rawJson, batch);

    public ModerationValidationReport ValidateDetailedWithMissingRuleFallback(
        string rawJson,
        ModerationBatch batch) =>
        IsCompactResponse(rawJson)
            ? ValidateCompactCore(rawJson, batch, allowMissingRuleFallback: true)
            : ValidateV4(rawJson, batch);

    public ModerationValidationReport ValidateV6(
        string rawJson,
        ModerationBatch batch) =>
        ValidateCompactCore(rawJson, batch, allowMissingRuleFallback: true);

    public ModerationValidationReport ValidateV4(
        string rawJson,
        ModerationBatch batch)
    {
        var report = FromLegacyResult(
            ValidateCore(rawJson, batch, allowMissingRuleFallback: true),
            batch);
        return report.IsValid
            ? report with
            {
                LocalFallbackMessageIds = FindLegacyMissingRuleIds(rawJson),
            }
            : report;
    }

    private static List<string> FindLegacyMissingRuleIds(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "decisions",
                    out var decisions)
                || decisions.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var decision in decisions.EnumerateArray())
            {
                if (decision.ValueKind != JsonValueKind.Object
                    || !decision.TryGetProperty("action", out var action)
                    || action.ValueKind != JsonValueKind.String
                    || string.Equals(action.GetString(), "none", StringComparison.Ordinal)
                    || !decision.TryGetProperty("messageId", out var messageId)
                    || messageId.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var ruleMissing = !decision.TryGetProperty("ruleCode", out var ruleCode)
                    || ruleCode.ValueKind == JsonValueKind.Null
                    || ruleCode.ValueKind == JsonValueKind.String
                        && string.IsNullOrWhiteSpace(ruleCode.GetString());
                if (ruleMissing && messageId.GetString() is { } value)
                {
                    result.Add(value);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ModerationValidationReport ValidateCompactCore(
        string rawJson,
        ModerationBatch batch,
        bool allowMissingRuleFallback) =>
        IsSparseV6(rawJson)
            ? ValidateV6Core(rawJson, batch, allowMissingRuleFallback)
            : ValidateV7Core(rawJson, batch, allowMissingRuleFallback);

    private static ModerationValidationReport ValidateV7Core(
        string rawJson,
        ModerationBatch batch,
        bool allowMissingRuleFallback)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(batch);
        if (rawJson.Length > MaximumRawResponseCharacters)
        {
            return FullRepairReport(
                batch,
                "response_too_large",
                "The model response exceeds the safety limit.");
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
                return FullRepairReport(
                    batch,
                    "root_not_object",
                    "The moderation envelope must be a JSON object.");
            }

            var structureError = ValidateObjectShape(
                root,
                KeyedRootProperties,
                KeyedRootProperties,
                "root");
            if (structureError is not null)
            {
                return FullRepairReport(
                    batch,
                    structureError.Code,
                    structureError.Message);
            }

            if (!TryGetRequiredString(root, "envelopeVersion", out var version)
                || !string.Equals(
                    version,
                    KeyedResponseEnvelopeVersion,
                    StringComparison.Ordinal))
            {
                return FullRepairReport(
                    batch,
                    "version_invalid",
                    "The response envelope version is invalid.");
            }

            if (!TryGetRequiredString(root, "batchId", out var batchIdText)
                || !Guid.TryParse(batchIdText, out var batchId)
                || batchId != batch.BatchId)
            {
                return FullRepairReport(
                    batch,
                    "batch_id_invalid",
                    "The response batch ID does not match.");
            }

            var decisionsElement = root.GetProperty("decisions");
            if (decisionsElement.ValueKind != JsonValueKind.Object)
            {
                return FullRepairReport(
                    batch,
                    "decisions_invalid",
                    "decisions must be an object keyed by temporary message ID.");
            }

            Dictionary<string, ModerationBatchMessage> sourceById;
            try
            {
                sourceById = batch.Messages.ToDictionary(
                    item => item.TemporaryMessageId,
                    StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
                return FullRepairReport(
                    batch,
                    "message_snapshot_invalid",
                    "Temporary message IDs in the batch must be unique.");
            }

            var replyTemplatesById = new Dictionary<string, ChatReplyTemplatePrompt>(
                StringComparer.Ordinal);
            foreach (var template in batch.ReplyTemplates)
            {
                if (string.IsNullOrWhiteSpace(template.TemplateId)
                    || !replyTemplatesById.TryAdd(template.TemplateId, template))
                {
                    return FullRepairReport(
                        batch,
                        "reply_template_snapshot_invalid",
                        "Reply-template IDs in the batch must be non-empty and unique.");
                }
            }

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var invalidIds = new HashSet<string>(StringComparer.Ordinal);
            var validatedById = new Dictionary<string, ValidatedModerationDecision>(
                StringComparer.Ordinal);
            var issues = new List<ModerationValidationIssue>();
            var localFallbackIds = new HashSet<string>(StringComparer.Ordinal);
            var requiresFullRepair = false;

            foreach (var property in decisionsElement.EnumerateObject())
            {
                var messageId = property.Name;
                if (!sourceById.ContainsKey(messageId))
                {
                    issues.Add(
                        new(
                            "message_id_unknown",
                            $"Temporary message ID '{messageId}' is not part of the batch."));
                    requiresFullRepair = true;
                    continue;
                }

                if (!seenIds.Add(messageId))
                {
                    MarkMessageInvalid(
                        messageId,
                        "message_id_duplicate",
                        $"Temporary message ID '{messageId}' occurs more than once.",
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    if (RepeatedPhraseSpamDetector.IsSpam(
                            sourceById[messageId].Message.Text.LowerText))
                    {
                        MarkMessageInvalid(
                            messageId,
                            "repeated_phrase_spam_missed",
                            $"Decision '{messageId}': A phrase repeated at least {ModerationRuleCodes.MinimumRepeatedPhraseSpamCount} times requires G_SPAM.",
                            invalidIds,
                            validatedById,
                            issues);
                        continue;
                    }

                    validatedById[messageId] = NoActionDecision(messageId);
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    MarkMessageInvalid(
                        messageId,
                        "decision_not_object",
                        $"Decision '{messageId}' must be null or an object.",
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                SparseDecisionValidationResult result;
                if (property.Value.TryGetProperty("replyTemplateId", out _))
                {
                    result = ValidateV6Reply(
                        property.Value,
                        sourceById,
                        replyTemplatesById,
                        claimed: null,
                        keyedMessageId: messageId);
                }
                else
                {
                    result = ValidateV6Decision(
                        property.Value,
                        sourceById,
                        batch.Context.EffectivePolicy,
                        allowMissingRuleFallback,
                        keyedMessageId: messageId,
                        sanitizeOptionalTriggers: true);
                }

                if (result.Error is not null)
                {
                    MarkMessageInvalid(
                        messageId,
                        result.Error.Code,
                        result.Error.Message,
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                validatedById[messageId] = result.Decision!;
                if (result.UsedLocalFallback)
                {
                    localFallbackIds.Add(messageId);
                }
            }

            foreach (var messageId in sourceById.Keys)
            {
                if (!seenIds.Contains(messageId))
                {
                    MarkMessageInvalid(
                        messageId,
                        "decision_missing",
                        $"Temporary message ID '{messageId}' is missing from decisions.",
                        invalidIds,
                        validatedById,
                        issues);
                }
            }

            var validDecisions = batch.Messages
                .Select(item => item.TemporaryMessageId)
                .Where(validatedById.ContainsKey)
                .Select(messageId => validatedById[messageId])
                .ToArray();
            if (issues.Count == 0)
            {
                return new ModerationValidationReport(
                    new ValidatedModerationEnvelope(batch.BatchId, validDecisions),
                    validDecisions,
                    [],
                    [],
                    RequiresFullRepair: false)
                {
                    LocalFallbackMessageIds = batch.Messages
                        .Select(item => item.TemporaryMessageId)
                        .Where(localFallbackIds.Contains)
                        .ToArray(),
                };
            }

            var repairMessageIds = requiresFullRepair
                ? batch.Messages.Select(item => item.TemporaryMessageId).ToArray()
                : batch.Messages
                    .Select(item => item.TemporaryMessageId)
                    .Where(invalidIds.Contains)
                    .ToArray();
            return new ModerationValidationReport(
                Envelope: null,
                validDecisions,
                issues,
                repairMessageIds,
                requiresFullRepair)
            {
                LocalFallbackMessageIds = validDecisions
                    .Select(item => item.TemporaryMessageId)
                    .Where(localFallbackIds.Contains)
                    .ToArray(),
            };
        }
        catch (JsonException exception)
        {
            return FullRepairReport(
                batch,
                "json_invalid",
                $"The model response is invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return FullRepairReport(batch, "envelope_invalid", exception.Message);
        }
    }

    private static ModerationValidationReport ValidateV6Core(
        string rawJson,
        ModerationBatch batch,
        bool allowMissingRuleFallback)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
        ArgumentNullException.ThrowIfNull(batch);
        if (rawJson.Length > MaximumRawResponseCharacters)
        {
            return FullRepairReport(
                batch,
                "response_too_large",
                "The model response exceeds the safety limit.");
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
                return FullRepairReport(
                    batch,
                    "root_not_object",
                    "The moderation envelope must be a JSON object.");
            }

            var structureError = ValidateObjectShape(
                root,
                batch.ReplyTemplates.Count > 0
                    ? SparseRootProperties
                    : RequiredSparseRootProperties,
                batch.ReplyTemplates.Count > 0
                    ? SparseRootProperties
                    : RequiredSparseRootProperties,
                "root");
            if (structureError is not null)
            {
                return FullRepairReport(
                    batch,
                    structureError.Code,
                    structureError.Message);
            }

            if (!TryGetRequiredString(root, "envelopeVersion", out var version)
                || !string.Equals(
                    version,
                    SparseResponseEnvelopeVersion,
                    StringComparison.Ordinal))
            {
                return FullRepairReport(
                    batch,
                    "version_invalid",
                    "The response envelope version is invalid.");
            }

            if (!TryGetRequiredString(root, "batchId", out var batchIdText)
                || !Guid.TryParse(batchIdText, out var batchId)
                || batchId != batch.BatchId)
            {
                return FullRepairReport(
                    batch,
                    "batch_id_invalid",
                    "The response batch ID does not match.");
            }

            if (root.GetProperty("noActionMessageIds").ValueKind
                    != JsonValueKind.Array
                || root.GetProperty("moderations").ValueKind
                    != JsonValueKind.Array
                || root.TryGetProperty("replies", out var repliesElement)
                    && repliesElement.ValueKind != JsonValueKind.Array)
            {
                return FullRepairReport(
                    batch,
                    "response_collections_invalid",
                    "noActionMessageIds, moderations, and optional replies must be arrays.");
            }

            Dictionary<string, ModerationBatchMessage> sourceById;
            try
            {
                sourceById = batch.Messages.ToDictionary(
                    item => item.TemporaryMessageId,
                    StringComparer.Ordinal);
            }
            catch (ArgumentException)
            {
                return FullRepairReport(
                    batch,
                    "message_snapshot_invalid",
                    "Temporary message IDs in the batch must be unique.");
            }

            var replyTemplatesById = new Dictionary<string, ChatReplyTemplatePrompt>(
                StringComparer.Ordinal);
            foreach (var template in batch.ReplyTemplates)
            {
                if (string.IsNullOrWhiteSpace(template.TemplateId)
                    || !replyTemplatesById.TryAdd(template.TemplateId, template))
                {
                    return FullRepairReport(
                        batch,
                        "reply_template_snapshot_invalid",
                        "Reply-template IDs in the batch must be non-empty and unique.");
                }
            }

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var invalidIds = new HashSet<string>(StringComparer.Ordinal);
            var validatedById = new Dictionary<string, ValidatedModerationDecision>(
                StringComparer.Ordinal);
            var issues = new List<ModerationValidationIssue>();
            var localFallbackIds = new HashSet<string>(StringComparer.Ordinal);
            var requiresFullRepair = false;

            foreach (var element in root
                .GetProperty("noActionMessageIds")
                .EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(element.GetString()))
                {
                    issues.Add(
                        new(
                            "message_id_invalid",
                            "Every no-action entry must contain a temporary message ID."));
                    requiresFullRepair = true;
                    continue;
                }

                var messageId = element.GetString()!;
                if (!sourceById.ContainsKey(messageId))
                {
                    issues.Add(
                        new(
                            "message_id_unknown",
                            $"Temporary message ID '{messageId}' is not part of the batch."));
                    requiresFullRepair = true;
                    continue;
                }

                if (!claimed.Add(messageId))
                {
                    MarkMessageInvalid(
                        messageId,
                        "message_id_duplicate",
                        $"Temporary message ID '{messageId}' occurs more than once.",
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                if (RepeatedPhraseSpamDetector.IsSpam(
                        sourceById[messageId].Message.Text.LowerText))
                {
                    MarkMessageInvalid(
                        messageId,
                        "repeated_phrase_spam_missed",
                        $"Decision '{messageId}': A phrase repeated at least {ModerationRuleCodes.MinimumRepeatedPhraseSpamCount} times requires G_SPAM.",
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                validatedById[messageId] = NoActionDecision(messageId);
            }

            foreach (var element in root.GetProperty("moderations").EnumerateArray())
            {
                var decisionResult = ValidateV6Decision(
                    element,
                    sourceById,
                    batch.Context.EffectivePolicy,
                    allowMissingRuleFallback);
                if (decisionResult.MessageId is null)
                {
                    issues.Add(
                        new(
                            decisionResult.Error!.Code,
                            decisionResult.Error.Message));
                    requiresFullRepair = true;
                    continue;
                }

                var messageId = decisionResult.MessageId;
                if (!sourceById.ContainsKey(messageId))
                {
                    issues.Add(
                        new(
                            "message_id_unknown",
                            $"Temporary message ID '{messageId}' is not part of the batch."));
                    requiresFullRepair = true;
                    continue;
                }

                if (!claimed.Add(messageId))
                {
                    MarkMessageInvalid(
                        messageId,
                        "message_id_duplicate",
                        $"Temporary message ID '{messageId}' occurs in more than one result group.",
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                if (decisionResult.Error is not null)
                {
                    MarkMessageInvalid(
                        messageId,
                        decisionResult.Error.Code,
                        decisionResult.Error.Message,
                        invalidIds,
                        validatedById,
                        issues);
                    continue;
                }

                validatedById[messageId] = decisionResult.Decision!;
                if (decisionResult.UsedLocalFallback)
                {
                    localFallbackIds.Add(messageId);
                }
            }

            if (root.TryGetProperty("replies", out repliesElement))
            {
                foreach (var element in repliesElement.EnumerateArray())
                {
                    var replyResult = ValidateV6Reply(
                        element,
                        sourceById,
                        replyTemplatesById,
                        claimed);
                    if (replyResult.MessageId is null)
                    {
                        issues.Add(
                            new(
                                replyResult.Error!.Code,
                                replyResult.Error.Message));
                        requiresFullRepair = true;
                        continue;
                    }

                    if (replyResult.Error is not null)
                    {
                        MarkMessageInvalid(
                            replyResult.MessageId,
                            replyResult.Error.Code,
                            replyResult.Error.Message,
                            invalidIds,
                            validatedById,
                            issues);
                        continue;
                    }

                    validatedById[replyResult.MessageId] = replyResult.Decision!;
                }
            }

            foreach (var messageId in sourceById.Keys)
            {
                if (!claimed.Contains(messageId))
                {
                    MarkMessageInvalid(
                        messageId,
                        "decision_missing",
                        $"Temporary message ID '{messageId}' is missing from the response.",
                        invalidIds,
                        validatedById,
                        issues);
                }
            }

            var validDecisions = batch.Messages
                .Select(item => item.TemporaryMessageId)
                .Where(validatedById.ContainsKey)
                .Select(messageId => validatedById[messageId])
                .ToArray();
            if (issues.Count == 0)
            {
                return new ModerationValidationReport(
                    new ValidatedModerationEnvelope(batch.BatchId, validDecisions),
                    validDecisions,
                    [],
                    [],
                    RequiresFullRepair: false)
                {
                    LocalFallbackMessageIds = batch.Messages
                        .Select(item => item.TemporaryMessageId)
                        .Where(localFallbackIds.Contains)
                        .ToArray(),
                };
            }

            var repairMessageIds = requiresFullRepair
                ? batch.Messages
                    .Select(item => item.TemporaryMessageId)
                    .ToArray()
                : batch.Messages
                    .Select(item => item.TemporaryMessageId)
                    .Where(invalidIds.Contains)
                    .ToArray();
            return new ModerationValidationReport(
                Envelope: null,
                validDecisions,
                issues,
                repairMessageIds,
                requiresFullRepair)
            {
                LocalFallbackMessageIds = validDecisions
                    .Select(item => item.TemporaryMessageId)
                    .Where(localFallbackIds.Contains)
                    .ToArray(),
            };
        }
        catch (JsonException exception)
        {
            return FullRepairReport(
                batch,
                "json_invalid",
                $"The model response is invalid JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return FullRepairReport(batch, "envelope_invalid", exception.Message);
        }
    }

    private static SparseDecisionValidationResult ValidateV6Decision(
        JsonElement element,
        Dictionary<string, ModerationBatchMessage> sourceById,
        EffectivePolicySnapshot activePolicy,
        bool allowMissingRuleFallback,
        string? keyedMessageId = null,
        bool sanitizeOptionalTriggers = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return SparseDecisionValidationResult.Invalid(
                null,
                "decision_not_object",
                "Every moderation must be a JSON object.");
        }

        var isKeyedDecision = keyedMessageId is not null;
        string? messageId = keyedMessageId;
        if (!isKeyedDecision
            && element.TryGetProperty("messageId", out var messageIdElement)
            && messageIdElement.ValueKind == JsonValueKind.String)
        {
            messageId = messageIdElement.GetString();
        }

        var structureError = ValidateObjectShape(
            element,
            isKeyedDecision
                ? KeyedModerationProperties
                : SparseModerationProperties,
            isKeyedDecision
                ? RequiredKeyedModerationProperties
                : RequiredSparseModerationProperties,
            "moderation");
        if (structureError is not null)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                structureError.Code,
                messageId is null
                    ? structureError.Message
                    : $"Decision '{messageId}': {structureError.Message}");
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            return SparseDecisionValidationResult.Invalid(
                null,
                "message_id_invalid",
                "A moderation contains a missing temporary message ID.");
        }

        if (!sourceById.TryGetValue(messageId, out var source))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "message_id_unknown",
                $"Temporary message ID '{messageId}' is not part of the batch.");
        }

        if (!TryGetRequiredString(element, "action", out var actionText)
            || !TryParseAction(actionText, out var action)
            || action == ModerationActionKind.None)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "action_invalid",
                $"Decision '{messageId}' must use delete, timeout, or ban.");
        }

        var hasTimeout = element.TryGetProperty(
            "timeoutSeconds",
            out var timeoutElement);
        if (action == ModerationActionKind.Timeout && !hasTimeout)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "timeout_missing",
                $"Decision '{messageId}' requires timeoutSeconds.");
        }

        if (action != ModerationActionKind.Timeout && hasTimeout)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "timeout_for_non_timeout",
                $"Decision '{messageId}' must omit timeoutSeconds for delete or ban.");
        }

        int? timeoutSeconds = hasTimeout
            && timeoutElement.ValueKind == JsonValueKind.Number
            && timeoutElement.TryGetInt32(out var value)
                ? value
                : hasTimeout
                    ? int.MinValue
                    : null;

        ModerationRuleDefinition? rule = null;
        var usedLocalFallback = false;
        var hasRuleCode = element.TryGetProperty("ruleCode", out var ruleElement);
        if (hasRuleCode
            && ruleElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(ruleElement.GetString()))
        {
            rule = activePolicy.FindRule(ruleElement.GetString()!);
            if (rule is null)
            {
                return SparseDecisionValidationResult.Invalid(
                    messageId,
                    "rule_inactive",
                    $"Decision '{messageId}' selects an unknown or inactive ruleCode.");
            }
        }
        else if (allowMissingRuleFallback)
        {
            rule = ResolveSafeMissingRuleFallback(activePolicy, source);
            usedLocalFallback = rule is not null;
        }

        if (rule is null)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "rule_invalid",
                $"Decision '{messageId}' requires an active ruleCode.");
        }

        if (!activePolicy.Contains(rule.PolicySet))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "policy_inactive",
                $"Decision '{messageId}' selects a rule from an inactive policy set.");
        }

        if (ModerationRuleCodes.RequiresLocalPhraseMatch(rule.RuleCode)
            && !source.LocalMatches.Any(match => string.Equals(
                match.RuleCode,
                rule.RuleCode,
                StringComparison.Ordinal)))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "phrase_gate_missing",
                $"Decision '{messageId}' selects {rule.RuleCode} without a matching active local phrase.");
        }

        (action, timeoutSeconds) =
            ModerationActionRules.NormalizeAiSuggestionForRule(
                action,
                timeoutSeconds,
                rule.RuleCode);
        if (!ModerationActionRules.IsValidAiRuleSuggestion(
                action,
                timeoutSeconds,
                rule.RuleCode))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "action_rule_invalid",
                $"Decision '{messageId}' has an action or timeout that is invalid for {rule.RuleCode}.");
        }

        var evidenceKind = element.TryGetProperty("evidenceKind", out var evidenceElement)
            && evidenceElement.ValueKind == JsonValueKind.String
            ? evidenceElement.GetString()
            : null;
        if (isKeyedDecision
            && !ModerationEvidenceKinds.IsValid(rule.RuleCode, evidenceKind))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "evidence_kind_invalid",
                $"Decision '{messageId}' requires a rule-compatible evidenceKind.");
        }

        var sensitiveDataType = element.TryGetProperty(
                "sensitiveDataType",
                out var sensitiveElement)
            && sensitiveElement.ValueKind == JsonValueKind.String
            ? sensitiveElement.GetString()
            : null;
        if (string.Equals(rule.RuleCode, ModerationRuleCodes.PersonalData, StringComparison.Ordinal)
            && isKeyedDecision
            && string.IsNullOrWhiteSpace(sensitiveDataType))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "sensitive_data_type_missing",
                $"Decision '{messageId}' requires sensitiveDataType.");
        }

        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal)
            && !HasMinimumBackseatContext(source.Message.Text.LowerText))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "backseat_context_insufficient",
                $"Decision '{messageId}': Backseatgaming requires a concrete multi-word statement and cannot be inferred from a question or fragment.");
        }

        if (string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.Spam,
                StringComparison.Ordinal)
            && source.Message.IsTwitchEmoteOnly
            && source.Message.TwitchEmoteCount < ModerationRuleCodes.MinimumEmoteSpamCount)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "spam_emote_threshold_not_met",
                $"Decision '{messageId}': Emote-only spam requires at least {ModerationRuleCodes.MinimumEmoteSpamCount} Twitch emotes; the message contains {source.Message.TwitchEmoteCount}.");
        }

        if (source.Message.IsTwitchEmoteOnly
            && source.Message.TwitchEmoteCount is > 0
                and < ModerationRuleCodes.MinimumEmoteSpamCount)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "emote_only_below_spam_threshold",
                $"Decision '{messageId}': Fewer than {ModerationRuleCodes.MinimumEmoteSpamCount} native Twitch emotes require noAction.");
        }

        string? candidateViolationReason;
        string? candidateGuidance;
        if (isKeyedDecision)
        {
            candidateViolationReason = ReadStringProperty(
                element,
                "violationReason");
            candidateGuidance = ReadStringProperty(element, "guidance");
        }
        else
        {
            candidateViolationReason = ReadStringProperty(element, "reason");
            candidateGuidance = null;
        }

        if (ModerationReasonNormalizer.IsSelfNegating(candidateViolationReason))
        {
            return SparseDecisionValidationResult.Valid(
                NoActionDecision(messageId),
                usedLocalFallback: true);
        }

        var triggerResult = ParseTriggerPhrases(
            element.GetProperty("triggerPhrases"),
            source.Message.Text.LowerText,
            action);
        if (triggerResult.Error is not null)
        {
            if (!sanitizeOptionalTriggers
                || string.Equals(
                    rule.RuleCode,
                    ModerationRuleCodes.Backseatgaming,
                    StringComparison.Ordinal)
                || TrySanitizeOptionalTriggerPhrases(
                    element.GetProperty("triggerPhrases"),
                    source.Message.Text.LowerText,
                    rule.RuleCode) is not { } sanitizedTriggers)
            {
                return SparseDecisionValidationResult.Invalid(
                    messageId,
                    triggerResult.Error.Code,
                    $"Decision '{messageId}': {triggerResult.Error.Message}");
            }

            triggerResult = new(sanitizedTriggers, Error: null);
            usedLocalFallback = true;
        }

        if (!ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode)
            && triggerResult.TriggerPhrases!.Count != 0)
        {
            if (!sanitizeOptionalTriggers)
            {
                return SparseDecisionValidationResult.Invalid(
                    messageId,
                    "triggers_learning_forbidden",
                    $"Decision '{messageId}': Personal data must not be proposed for trigger learning.");
            }

            triggerResult = new([], Error: null);
            usedLocalFallback = true;
        }

        if (!string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal))
        {
            var originalTriggers = triggerResult.TriggerPhrases!;
            var evidence = ModerationTriggerEvidenceNormalizer.Normalize(
                source.Message.Text.LowerText,
                rule.RuleCode,
                originalTriggers);
            if (!evidence.SequenceEqual(originalTriggers, StringComparer.Ordinal))
            {
                triggerResult = new(evidence, Error: null);
                usedLocalFallback = true;
            }
        }

        string reason;
        if (isKeyedDecision)
        {
            if (!ModerationReasonComposer.TryComposeAiReason(
                    candidateViolationReason,
                    candidateGuidance,
                    action,
                    rule,
                    source.Message.Text.LowerText,
                    triggerResult.TriggerPhrases,
                    out var composition,
                    out var reasonErrorCode))
            {
                return SparseDecisionValidationResult.Invalid(
                    messageId,
                    reasonErrorCode,
                    $"Decision '{messageId}': violationReason or guidance is invalid.");
            }

            reason = composition.Reason;
        }
        else if (!ModerationReasonNormalizer.TryNormalizeContextualAiReason(
                     candidateViolationReason,
                     rule,
                     out reason))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "reason_not_contextual",
                $"Decision '{messageId}': reason must use neutral German past tense without direct address, quotations, or a generic rule reason.");
        }
        else if (ModerationReasonNormalizer.ContainsTriggerPhraseExcerpt(
                     reason,
                     triggerResult.TriggerPhrases))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "reason_contains_trigger_phrase",
                $"Decision '{messageId}': reason must summarize the violation and must not repeat triggerPhrases.");
        }

        var isBackseat = string.Equals(
            rule.RuleCode,
            ModerationRuleCodes.Backseatgaming,
            StringComparison.Ordinal);
        if (isBackseat
            && (triggerResult.TriggerPhrases!.Count == 0
                || triggerResult.TriggerPhrases.Any(
                    phrase => CountLexicalTokens(phrase) < 2)))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "backseat_evidence_insufficient",
                $"Decision '{messageId}': Backseatgaming requires at least one concrete multi-word trigger phrase.");
        }

        var evidenceMissing = !string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.PersonalData,
                StringComparison.Ordinal)
            && triggerResult.TriggerPhrases!.Count != 1;

        var confidenceElement = element.GetProperty("confidence");
        if (confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "confidence_invalid",
                $"Decision '{messageId}' has an invalid confidence.");
        }

        if (evidenceMissing)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "evidence_phrase_missing",
                $"Decision '{messageId}' requires exactly one contiguous evidence phrase.");
        }

        return SparseDecisionValidationResult.Valid(
            new ValidatedModerationDecision(
                messageId,
                action,
                timeoutSeconds,
                rule.PolicySet,
                reason,
                triggerResult.TriggerPhrases!,
                confidence,
                rule.RuleCode)
            {
                EvidenceKind = evidenceKind,
                SensitiveDataType = sensitiveDataType,
            },
            usedLocalFallback);
    }

    private static SparseDecisionValidationResult ValidateV6Reply(
        JsonElement element,
        Dictionary<string, ModerationBatchMessage> sourceById,
        Dictionary<string, ChatReplyTemplatePrompt> replyTemplatesById,
        HashSet<string>? claimed,
        string? keyedMessageId = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return SparseDecisionValidationResult.Invalid(
                null,
                "reply_not_object",
                "Every reply must be a JSON object.");
        }

        var isKeyedDecision = keyedMessageId is not null;
        string? messageId = keyedMessageId;
        if (!isKeyedDecision
            && element.TryGetProperty("messageId", out var messageIdElement)
            && messageIdElement.ValueKind == JsonValueKind.String)
        {
            messageId = messageIdElement.GetString();
        }

        var structureError = ValidateObjectShape(
            element,
            isKeyedDecision ? KeyedReplyProperties : SparseReplyProperties,
            isKeyedDecision ? KeyedReplyProperties : SparseReplyProperties,
            "reply");
        if (structureError is not null)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                structureError.Code,
                messageId is null
                    ? structureError.Message
                    : $"Reply '{messageId}': {structureError.Message}");
        }

        if (string.IsNullOrWhiteSpace(messageId)
            || !sourceById.ContainsKey(messageId))
        {
            return SparseDecisionValidationResult.Invalid(
                null,
                "reply_message_id_unknown",
                "A reply contains a missing or unknown temporary message ID.");
        }

        if (claimed is not null && !claimed.Add(messageId))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "reply_message_id_duplicate",
                $"Reply message ID '{messageId}' occurs in more than one result group.");
        }

        if (!TryGetRequiredString(element, "replyTemplateId", out var templateId)
            || !replyTemplatesById.ContainsKey(templateId))
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "reply_template_unknown",
                $"Reply '{messageId}' selects a missing or unknown reply template.");
        }

        var confidenceElement = element.GetProperty("confidence");
        if (confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            return SparseDecisionValidationResult.Invalid(
                messageId,
                "reply_confidence_invalid",
                $"Reply '{messageId}' requires confidence from 0 to 1.");
        }

        return SparseDecisionValidationResult.Valid(
            NoActionDecision(messageId) with
            {
                ReplyTemplateId = templateId,
                ReplyConfidence = confidence,
            });
    }

    private static ModerationRuleDefinition? ResolveSafeMissingRuleFallback(
        EffectivePolicySnapshot activePolicy,
        ModerationBatchMessage source)
    {
        var matchingRules = source.LocalMatches
            .Select(match => activePolicy.FindRule(match.RuleCode))
            .Where(rule => rule is not null)
            .DistinctBy(rule => rule!.RuleCode, StringComparer.Ordinal)
            .ToArray();
        if (matchingRules.Length == 1)
        {
            return matchingRules[0];
        }

        return activePolicy.ActiveRules.Count == 1
            ? activePolicy.ActiveRules[0]
            : null;
    }

    private static ValidatedModerationDecision NoActionDecision(string messageId) =>
        new(
            messageId,
            ModerationActionKind.None,
            TimeoutSeconds: null,
            PolicySet: null,
            Reason: string.Empty,
            TriggerPhrases: [],
            Confidence: 1D);

    private static void MarkMessageInvalid(
        string messageId,
        string code,
        string message,
        HashSet<string> invalidIds,
        Dictionary<string, ValidatedModerationDecision> validatedById,
        List<ModerationValidationIssue> issues)
    {
        invalidIds.Add(messageId);
        validatedById.Remove(messageId);
        issues.Add(new(code, message, messageId));
    }

    private static ModerationValidationReport FullRepairReport(
        ModerationBatch batch,
        string code,
        string message) =>
        new(
            Envelope: null,
            ValidDecisions: [],
            Issues: [new(code, message)],
            RepairMessageIds: batch.Messages
                .Select(item => item.TemporaryMessageId)
                .ToArray(),
            RequiresFullRepair: true);

    private static ModerationValidationReport FromLegacyResult(
        EnvelopeValidationResult result,
        ModerationBatch batch)
    {
        if (result.IsValid)
        {
            return new(
                result.Envelope,
                result.Envelope!.Decisions,
                [],
                [],
                RequiresFullRepair: false);
        }

        return new(
            Envelope: null,
            ValidDecisions: [],
            Issues: result.Errors
                .Select(error => new ModerationValidationIssue(
                    error.Code,
                    error.Message))
                .ToArray(),
            RepairMessageIds: batch.Messages
                .Select(item => item.TemporaryMessageId)
                .ToArray(),
            RequiresFullRepair: true);
    }

    private static EnvelopeValidationResult ToEnvelopeValidationResult(
        ModerationValidationReport report) =>
        report.IsValid
            ? new EnvelopeValidationResult(report.Envelope, [])
            : EnvelopeValidationResult.Invalid(
                report.Issues
                    .Select(issue => new EnvelopeValidationError(
                        issue.Code,
                        issue.Message))
                    .ToArray());

    private static bool IsCompactResponse(string rawJson) =>
        HasEnvelopeVersion(rawJson, SparseResponseEnvelopeVersion)
        || HasEnvelopeVersion(rawJson, KeyedResponseEnvelopeVersion);

    private static bool IsSparseV6(string rawJson) =>
        HasEnvelopeVersion(rawJson, SparseResponseEnvelopeVersion);

    private static bool HasEnvelopeVersion(string rawJson, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(rawJson);
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
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(
                    "envelopeVersion",
                    out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String
                && string.Equals(
                    versionElement.GetString(),
                    expectedVersion,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
            var replyTemplatesById = new Dictionary<string, ChatReplyTemplatePrompt>(
                StringComparer.Ordinal);
            foreach (var template in batch.ReplyTemplates)
            {
                if (string.IsNullOrWhiteSpace(template.TemplateId)
                    || !replyTemplatesById.TryAdd(template.TemplateId, template))
                {
                    return Error(
                        "reply_template_snapshot_invalid",
                        "Reply-template IDs in the batch must be non-empty and unique.");
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var validated = new List<ValidatedModerationDecision>(sourceById.Count);

            foreach (var decisionElement in decisionsElement.EnumerateArray())
            {
                var decisionResult = ValidateDecision(
                    decisionElement,
                    sourceById,
                    batch.Context.EffectivePolicy,
                    replyTemplatesById,
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
        IReadOnlyDictionary<string, ChatReplyTemplatePrompt> replyTemplatesById,
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
            && ModerationRuleCodes.RequiresLocalPhraseMatch(ruleResult.Rule?.RuleCode)
            && !source.LocalMatches.Any(match => string.Equals(
                match.RuleCode,
                ruleResult.Rule?.RuleCode,
                StringComparison.Ordinal)))
        {
            return DecisionValidationResult.Invalid(
                "phrase_gate_missing",
                $"Decision '{messageId}' selects a phrase-gated rule without a matching active local phrase.");
        }

        (action, timeoutSeconds) =
            ModerationActionRules.NormalizeAiSuggestionForRule(
                action,
                timeoutSeconds,
                ruleResult.Rule?.RuleCode);
        if (!ModerationActionRules.IsValidAiSuggestion(
                action,
                timeoutSeconds))
        {
            return DecisionValidationResult.Invalid(
                "timeout_invalid",
                $"Decision '{messageId}' has an invalid timeout value.");
        }

        if (!ModerationActionRules.IsValidAiRuleAction(
                action,
                ruleResult.Rule?.RuleCode))
        {
            return DecisionValidationResult.Invalid(
                "action_rule_invalid",
                $"Decision '{messageId}': The selected rule cannot justify a permanent ban on its own.");
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
        if (action != ModerationActionKind.None
            && string.Equals(
                ruleResult.Rule?.RuleCode,
                ModerationRuleCodes.Spam,
                StringComparison.Ordinal)
            && source.Message.IsTwitchEmoteOnly
            && source.Message.TwitchEmoteCount < ModerationRuleCodes.MinimumEmoteSpamCount)
        {
            return DecisionValidationResult.Invalid(
                "spam_emote_threshold_not_met",
                $"Decision '{messageId}': Emote-only spam requires at least {ModerationRuleCodes.MinimumEmoteSpamCount} Twitch emotes; the message contains {source.Message.TwitchEmoteCount}.");
        }
        if (action != ModerationActionKind.None
            && source.Message.IsTwitchEmoteOnly
            && source.Message.TwitchEmoteCount is > 0
                and < ModerationRuleCodes.MinimumEmoteSpamCount)
        {
            return DecisionValidationResult.Invalid(
                "emote_only_below_spam_threshold",
                $"Decision '{messageId}': Fewer than {ModerationRuleCodes.MinimumEmoteSpamCount} native Twitch emotes require noAction.");
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
            if (!ModerationReasonNormalizer.TryNormalizeContextualAiReason(
                    candidate,
                    ruleResult.Rule,
                    out reason))
            {
                return DecisionValidationResult.Invalid(
                    "reason_not_contextual",
                    $"Decision '{messageId}': reason must use neutral German past tense without direct address, quotations, or a generic rule reason.");
            }
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
        if (!string.Equals(
                ruleResult.Rule?.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.Ordinal))
        {
            triggerResult = new(
                triggerResult.TriggerPhrases!
                    .Where(LearningTriggerPhraseFilter.IsContextual)
                    .ToArray(),
                Error: null);
        }
        if (action != ModerationActionKind.None
            && ModerationReasonNormalizer.ContainsTriggerPhraseExcerpt(
                reason,
                triggerResult.TriggerPhrases))
        {
            return DecisionValidationResult.Invalid(
                "reason_contains_trigger_phrase",
                $"Decision '{messageId}': reason must summarize the violation and must not repeat triggerPhrases.");
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

        var replyResult = ParseReplyTemplate(
            element.GetProperty("replyTemplateId"),
            element.GetProperty("replyConfidence"),
            action,
            replyTemplatesById);
        if (replyResult.Error is not null)
        {
            return DecisionValidationResult.Invalid(
                replyResult.Error.Code,
                $"Decision '{messageId}': {replyResult.Error.Message}");
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
                ruleResult.Rule?.RuleCode,
                replyResult.TemplateId,
                replyResult.Confidence));
    }

    private static ReplyTemplateParseResult ParseReplyTemplate(
        JsonElement templateIdElement,
        JsonElement confidenceElement,
        ModerationActionKind action,
        IReadOnlyDictionary<string, ChatReplyTemplatePrompt> replyTemplatesById)
    {
        if (action != ModerationActionKind.None)
        {
            return templateIdElement.ValueKind == JsonValueKind.Null
                && confidenceElement.ValueKind == JsonValueKind.Null
                    ? new(null, null, null)
                    : new(
                        null,
                        null,
                        new(
                            "reply_for_moderation_action",
                            "A delete, timeout, or ban decision cannot select a reply template."));
        }

        if (templateIdElement.ValueKind == JsonValueKind.Null)
        {
            return confidenceElement.ValueKind == JsonValueKind.Null
                ? new(null, null, null)
                : new(
                    null,
                    null,
                    new(
                        "reply_confidence_without_template",
                        "replyConfidence must be null when no reply template is selected."));
        }

        if (templateIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(templateIdElement.GetString()))
        {
            return new(
                null,
                null,
                new(
                    "reply_template_invalid",
                    "replyTemplateId must be null or a non-empty temporary template ID."));
        }

        var templateId = templateIdElement.GetString()!;
        if (!replyTemplatesById.ContainsKey(templateId))
        {
            return new(
                null,
                null,
                new(
                    "reply_template_unknown",
                    "The selected replyTemplateId is not present in this batch."));
        }

        if (confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            return new(
                null,
                null,
                new(
                    "reply_confidence_invalid",
                    "A selected reply template requires replyConfidence from 0 to 1."));
        }

        return new(templateId, confidence, null);
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

    private static List<string>? TrySanitizeOptionalTriggerPhrases(
        JsonElement element,
        string lowerSourceText,
        string? ruleCode)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var triggers = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var triggerElement in element.EnumerateArray())
        {
            if (triggers.Count == 32)
            {
                break;
            }

            if (triggerElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(triggerElement.GetString()))
            {
                continue;
            }

            var normalized = triggerElement.GetString()!.ToLowerInvariant();
            if (normalized.Length <= lowerSourceText.Length
                && lowerSourceText.Contains(normalized, StringComparison.Ordinal)
                && seen.Add(normalized))
            {
                triggers.Add(normalized);
            }
        }

        return ModerationTriggerEvidenceNormalizer
            .Normalize(lowerSourceText, ruleCode, triggers)
            .ToList();
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
        => ValidateObjectShape(
            element,
            expectedProperties,
            expectedProperties,
            location);

    private static EnvelopeValidationError? ValidateObjectShape(
        JsonElement element,
        HashSet<string> allowedProperties,
        HashSet<string> requiredProperties,
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

            if (!allowedProperties.Contains(property.Name))
            {
                return new(
                    "property_unknown",
                    $"Property '{property.Name}' is not allowed in {location}.");
            }
        }

        var missing = requiredProperties.FirstOrDefault(property => !seen.Contains(property));
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

    private static string? ReadStringProperty(
        JsonElement parent,
        string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || value.Any(char.IsControl))
        {
            return null;
        }

        return value;
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

    private sealed record SparseDecisionValidationResult(
        string? MessageId,
        ValidatedModerationDecision? Decision,
        EnvelopeValidationError? Error,
        bool UsedLocalFallback)
    {
        public static SparseDecisionValidationResult Valid(
            ValidatedModerationDecision decision,
            bool usedLocalFallback = false) =>
            new(
                decision.TemporaryMessageId,
                decision,
                null,
                usedLocalFallback);

        public static SparseDecisionValidationResult Invalid(
            string? messageId,
            string code,
            string message) =>
            new(messageId, null, new(code, message), UsedLocalFallback: false);
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

    private sealed record ReplyTemplateParseResult(
        string? TemplateId,
        double? Confidence,
        EnvelopeValidationError? Error);
}
