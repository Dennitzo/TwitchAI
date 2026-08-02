using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ModerationRepairClientTests
{
    [Fact]
    public async Task RepairAsync_LegacyPromptRequiresMessageSpecificReason()
    {
        var request = RequestWithMessages("m1");
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(client);

        await sut.RepairAsync(
            request,
            "{}",
            [new EnvelopeValidationError(
                "reason_not_contextual",
                "invalid")]);

        var sent = Assert.IsType<LmStudioModerationRequest>(client.Request);
        Assert.Contains(
            ModerationReasonNormalizer.ContextualAiReasonInstruction,
            sent.SystemPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairAsync_V6SendsOnlyTargetMessagesWithoutInvalidResponse()
    {
        var envelope = RequestWithMessages("m1", "m2");
        var compiled = new ModerationPromptCompiler().Compile(envelope);
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(client);

        await sut.RepairAsync(
            new ModerationRepairRequest(
                compiled,
                ["m2"],
                [new ModerationValidationIssue(
                    "rule_inactive",
                    "human-readable detail must not be sent",
                    "m2")],
                RequiresFullBatchRepair: false));

        var sent = Assert.IsType<LmStudioModerationRequest>(client.Request);
        Assert.Equal(1, sent.MessageCount);
        Assert.Equal(LmStudioResponseSchemaKind.ModerationV6, sent.ResponseSchema);
        Assert.NotNull(sent.ResponseFormat);
        Assert.Contains("\"messageId\":\"m2\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messageId\":\"m1\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("rule_inactive", sent.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "human-readable detail must not be sent",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("invalidResponse", sent.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairAsync_RollbackOptionUsesLegacyRepairContract()
    {
        var envelope = RequestWithMessages("m1", "m2");
        var compiled = new ModerationPromptCompiler().Compile(envelope);
        var options = new InfrastructureOptions();
        options.LmStudio.UseCompactModerationContractV6 = false;
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(
            client,
            new ModerationPromptCompiler(),
            options);

        await sut.RepairAsync(
            new ModerationRepairRequest(
                compiled,
                ["m2"],
                [new ModerationValidationIssue("rule_inactive", "invalid", "m2")],
                RequiresFullBatchRepair: false));

        var sent = Assert.IsType<LmStudioModerationRequest>(client.Request);
        Assert.Equal(1, sent.MessageCount);
        Assert.Equal(LmStudioResponseSchemaKind.Moderation, sent.ResponseSchema);
        Assert.Null(sent.ResponseFormat);
        Assert.Contains("validationErrors", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains(
            $"\"envelopeVersion\":\"{ModerationRequestFactory.LegacyRequestEnvelopeVersion}\"",
            sent.UserPrompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ModerationPromptCompiler.RequestEnvelopeVersion,
            sent.UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains("\"messageId\":\"m2\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"messageId\":\"m1\"", sent.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairAsync_ContextInvalidBackseatRemovesOnlyForbiddenRuleAndKeepsReplies()
    {
        var envelope = RequestWithMessages("m1") with
        {
            ActivePolicySets =
            [
                new PolicySetRevision(PolicySetCode.General, 1),
                new PolicySetRevision(PolicySetCode.Backseatgaming, 1),
            ],
            ActiveRules =
            [
                .. RequestWithMessages("m1").ActiveRules,
                new ModerationRuleDefinition(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming,
                    "Backseatgaming",
                    "Erkenne konkrete unaufgeforderte Spielanweisungen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Unaufgeforderte Spielanweisung.",
                    Priority: 100),
            ],
            LearningExamples =
            [
                new RetrievalExample(
                    "fahr links",
                    ModerationActionKind.Timeout,
                    600,
                    PolicySetCode.Backseatgaming,
                    "Unaufgeforderte Spielanweisung.",
                    IsPositive: true,
                    ModerationRuleCodes.Backseatgaming),
            ],
            ReplyTemplates = [new ChatReplyTemplatePrompt("r1", "event dauer")],
        };
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(client);

        await sut.RepairAsync(
            new ModerationRepairRequest(
                new ModerationPromptCompiler().Compile(envelope),
                ["m1"],
                [new ModerationValidationIssue(
                    "backseat_context_insufficient",
                    "invalid",
                    "m1")],
                RequiresFullBatchRepair: false));

        var sent = Assert.IsType<LmStudioModerationRequest>(client.Request);
        Assert.DoesNotContain(
            ModerationRuleCodes.Backseatgaming,
            sent.ActiveRuleCodes!);
        Assert.Contains("\"replyTemplates\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("fahr links", sent.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairAsync_BackseatEvidenceErrorKeepsBackseatRuleForTriggerRepair()
    {
        var original = RequestWithMessages("m1");
        var envelope = original with
        {
            ActivePolicySets =
            [
                .. original.ActivePolicySets,
                new PolicySetRevision(PolicySetCode.Backseatgaming, 1),
            ],
            ActiveRules =
            [
                .. original.ActiveRules,
                new ModerationRuleDefinition(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming,
                    "Backseatgaming",
                    "Erkenne konkrete unaufgeforderte Spielanweisungen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Unaufgeforderte Spielanweisung.",
                    Priority: 100),
            ],
        };
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(client);

        await sut.RepairAsync(
            new ModerationRepairRequest(
                new ModerationPromptCompiler().Compile(envelope),
                ["m1"],
                [new ModerationValidationIssue(
                    "backseat_evidence_insufficient",
                    "invalid",
                    "m1")],
                RequiresFullBatchRepair: false));

        Assert.Contains(
            ModerationRuleCodes.Backseatgaming,
            client.Request!.ActiveRuleCodes!);
    }

    [Fact]
    public async Task RepairAsync_PreservesV4ReplyContractAndUsesModerationScale()
    {
        var batchId = Guid.Parse("c96142f5-10a7-4c90-b2fd-b4b457c20d8e");
        var request = new ModerationRequestEnvelopeV3(
            ModerationRequestFactory.RequestEnvelopeVersion,
            batchId,
            "509658",
            "Just Chatting",
            [new PolicySetRevision(PolicySetCode.General, 1)],
            [
                new ModerationRuleDefinition(
                    "G_INSULT",
                    PolicySetCode.General,
                    "Beleidigung",
                    "Erkenne direkte Beleidigungen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Direkte Beleidigung eines Nutzers oder Streamers.",
                    Priority: 70),
            ],
            [new ModerationRequestMessageV3("m1", "du idiot", [])],
            [],
            [new ChatReplyTemplatePrompt("r1", "wie lange läuft das event?")]);
        var client = new RecordingLmStudioClient();
        var sut = new ModerationRepairClient(client);

        var result = await sut.RepairAsync(
            request,
            """{"replyTemplateId":"r1","replyConfidence":0.9}""",
            [new EnvelopeValidationError("timeout_invalid", "invalid")]);

        Assert.Equal("""{"repaired":true}""", result);
        Assert.NotNull(client.Request);
        var sent = client.Request!;
        Assert.Contains(
            "delete, timeout, or ban",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "use a 600-second timeout as the normal baseline",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "2-second timeout as purge for a very light disruption where that user's recent messages should be cleared",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "120-second timeout",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "at least 20 emote occurrences in one message",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "One through nineteen emotes alone are none",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Do not relabel one through nineteen repeated emote names as text spam or disruption",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text spam means clearly excessive repetition or flooding of text, words, or short phrases",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Special-character spam means long or dense repeated symbol/punctuation sequences",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "600-second timeout",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "86400-second timeout",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "ban for unambiguously extreme abuse",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every actionable G_ADVERTISING decision must use ban",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Every actionable G_BEGGING decision must use a 86400-second timeout",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "delete only for a single low-context, unnecessary nuisance message",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never use delete merely because a message is short or unwanted",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Rule defaults are fallbacks, not constraints",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "Never ban for an unnecessary message, spam, begging, or backseatgaming alone",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "For delete or ban, timeoutSeconds must be null",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "timeout must be exactly 2, 120, 600, or 86400 seconds",
            sent.SystemPrompt,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "call them \"Nutzer oder Streamer\", never only \"Nutzer\"",
            sent.SystemPrompt,
            StringComparison.Ordinal);
        Assert.Equal(["G_INSULT"], sent.ActiveRuleCodes);
        Assert.Equal(["r1"], sent.ReplyTemplateIds);
        Assert.Contains("\"replyTemplates\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("replyTemplateId", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("replyConfidence", sent.UserPrompt, StringComparison.Ordinal);
    }

    private static ModerationRequestEnvelopeV3 RequestWithMessages(
        params string[] messageIds)
    {
        return new ModerationRequestEnvelopeV3(
            ModerationPromptCompiler.RequestEnvelopeVersion,
            Guid.Parse("c96142f5-10a7-4c90-b2fd-b4b457c20d8e"),
            "509658",
            "Just Chatting",
            [new PolicySetRevision(PolicySetCode.General, 1)],
            [
                new ModerationRuleDefinition(
                    "G_INSULT",
                    PolicySetCode.General,
                    "Beleidigung",
                    "Erkenne direkte Beleidigungen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Direkte Beleidigung eines Nutzers oder Streamers.",
                    Priority: 70),
            ],
            messageIds
                .Select(messageId => new ModerationRequestMessageV3(
                    messageId,
                    $"text for {messageId}",
                    []))
                .ToArray(),
            [],
            []);
    }

    private sealed class RecordingLmStudioClient : ILmStudioClient
    {
        public LmStudioModerationRequest? Request { get; private set; }

        public Task<LmStudioModerationCompletion> CompleteModerationAsync(
            LmStudioModerationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            return Task.FromResult(
                new LmStudioModerationCompletion(
                    """{"repaired":true}""",
                    new LmStudioUsageMetrics(
                        PromptTokens: 1,
                        CompletionTokens: 1,
                        TotalTokens: 2,
                        ElapsedMilliseconds: 1,
                        Model: "test-model",
                        FinishReason: "stop")));
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
