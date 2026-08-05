using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using CoreTextNormalizer = TwitchAI.Core.Contracts.IChatTextNormalizer;

namespace TwitchAI.Infrastructure.Pipeline;

public sealed class ReviewReasonService : IReviewReasonService
{
    private static readonly string[] RequiredReasonProperties = ["reason"];
    private readonly IPolicyStore _policyStore;
    private readonly CoreTextNormalizer _normalizer;
    private readonly ILmStudioClient _lmStudioClient;

    public ReviewReasonService(
        IPolicyStore policyStore,
        CoreTextNormalizer normalizer,
        ILmStudioClient lmStudioClient)
    {
        _policyStore = policyStore;
        _normalizer = normalizer;
        _lmStudioClient = lmStudioClient;
    }

    public async ValueTask<ReviewReasonResult> GenerateAsync(
        ReviewReasonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BroadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RuleCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        if (request.Action is not (
            ModerationActionKind.Timeout or ModerationActionKind.Ban))
        {
            throw new ArgumentException(
                "Ein Review-Reason-Run unterstützt nur Timeout oder Ban.",
                nameof(request));
        }

        var policy = await _policyStore.GetEffectivePolicyAsync(
            request.BroadcasterId,
            request.CategoryId,
            cancellationToken).ConfigureAwait(false);
        var storedRule = policy.Rules.FirstOrDefault(rule =>
            rule.Enabled
            && string.Equals(
                rule.RuleCode,
                request.RuleCode,
                StringComparison.OrdinalIgnoreCase));
        if (storedRule is null
            || !ModerationRuleCodes.IsSemanticAiRule(storedRule.RuleCode))
        {
            throw new InvalidOperationException(
                "Die ausgewählte Verstoßkategorie ist nicht mehr aktiv.");
        }

        var rule = MapRule(storedRule);
        var processed = _normalizer.Process(request.OriginalText);
        var systemPrompt =
            "Generate the user-facing reason for exactly one already confirmed Twitch moderation case. "
            + "The selected category and action are authoritative. Never allow, reclassify, or change them. "
            + "Use the supplied chat message only as context and tailor the reason to the concrete act. "
            + "Return exactly one JSON object with the property reason and no other property.\n"
            + ModerationReasonNormalizer.ContextualAiReasonInstruction
            + " For BACKSEATGAMING, reason must explicitly contain the word Backseatgaming.";
        var userPrompt = ModerationPromptCompiler.SerializeWire(
            new
            {
                message = processed.LowerText,
                selectedRule = new
                {
                    ruleCode = rule.RuleCode,
                    title = rule.Title,
                    instruction = rule.PromptInstruction,
                },
                action = ToWireAction(request.Action),
                timeoutSeconds = request.Action == ModerationActionKind.Timeout
                    ? request.TimeoutSeconds
                    : null,
            });

        var completion = await CompleteAsync(
                request.CorrelationId,
                policy.PolicyHash,
                systemPrompt,
                userPrompt,
                rule.RuleCode,
                request.BroadcasterId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryReadAndValidate(
                completion.RawEnvelope,
                request,
                rule,
                out var reason,
                out var validationCode))
        {
            completion = await CompleteAsync(
                    $"{request.CorrelationId}-repair",
                    policy.PolicyHash,
                    systemPrompt
                    + "\nThis is the only repair attempt. The previous reason failed local validation with code "
                    + validationCode
                    + ". Produce a new message-specific reason and return only the required JSON object.",
                    userPrompt,
                    rule.RuleCode,
                    request.BroadcasterId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!TryReadAndValidate(
                    completion.RawEnvelope,
                    request,
                    rule,
                    out reason,
                    out _))
            {
                throw new InvalidDataException(
                    "LM Studio lieferte auch nach der Reparatur keine gültige kontextbezogene Reason.");
            }
        }

        return new ReviewReasonResult(
            rule.RuleCode,
            reason,
            completion.Usage.ElapsedMilliseconds,
            completion.Usage.PromptTokens,
            completion.Usage.CompletionTokens,
            completion.Usage.TotalTokens);
    }

    private async Task<LmStudioModerationCompletion> CompleteAsync(
        string correlationId,
        string policyHash,
        string systemPrompt,
        string userPrompt,
        string ruleCode,
        string schedulingKey,
        CancellationToken cancellationToken) =>
        await _lmStudioClient.CompleteModerationAsync(
            new LmStudioModerationRequest(
                correlationId,
                policyHash,
                MessageCount: 1,
                systemPrompt,
                userPrompt,
                ActiveRuleCodes: [ruleCode],
                ReplyTemplateIds: [],
                LmStudioResponseSchemaKind.ModerationV6,
                LocalAiRunKind.ReviewReason,
                CreateResponseFormat(),
                LocalAiRunPriority.ManualReview,
                schedulingKey),
            cancellationToken).ConfigureAwait(false);

    private static bool TryReadAndValidate(
        string rawEnvelope,
        ReviewReasonRequest request,
        ModerationRuleDefinition rule,
        out string reason,
        out string validationCode)
    {
        reason = string.Empty;
        validationCode = "reason_json_invalid";
        try
        {
            using var document = JsonDocument.Parse(rawEnvelope);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("reason", out var reasonElement)
                || reasonElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!ModerationReasonComposer.TryComposeReasonOnly(
                    reasonElement.GetString(),
                    request.Action,
                    rule,
                    request.OriginalText,
                    request.TriggerPhrases,
                    out var composition,
                    out validationCode))
            {
                return false;
            }

            reason = composition.Reason;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement CreateResponseFormat() =>
        JsonSerializer.SerializeToElement(new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "review_reason",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = RequiredReasonProperties,
                    properties = new
                    {
                        reason = new
                        {
                            type = "string",
                            minLength = 1,
                            maxLength = ModerationReasonNormalizer.MaximumLength,
                        },
                    },
                },
            },
        });

    private static ModerationRuleDefinition MapRule(BlacklistRuleRecord rule) =>
        new(
            rule.RuleCode,
            string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.OrdinalIgnoreCase)
                    ? PolicySetCode.Backseatgaming
                    : PolicySetCode.General,
            rule.Title,
            rule.PromptInstruction,
            rule.DefaultAction switch
            {
                ModerationRuleAction.Delete => ModerationActionKind.Delete,
                ModerationRuleAction.Timeout => ModerationActionKind.Timeout,
                ModerationRuleAction.Ban => ModerationActionKind.Ban,
                _ => throw new InvalidDataException("Ungültige Moderationsaktion."),
            },
            rule.DefaultTimeoutSeconds,
            rule.PublicReason,
            rule.Priority);

    private static string ToWireAction(ModerationActionKind action) => action switch
    {
        ModerationActionKind.Timeout => "timeout",
        ModerationActionKind.Ban => "ban",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
