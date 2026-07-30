using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ModerationRepairClientTests
{
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
