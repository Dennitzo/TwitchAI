using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreLocalModerationClientTests
{
    [Fact]
    public async Task ModerateAsync_CompiledPrompt_ForwardsExactV6Artifacts()
    {
        var prompt = new ModerationPromptCompiler().Compile(Envelope());
        var lmStudio = new RecordingLmStudioClient();
        var sut = new CoreLocalModerationClient(
            lmStudio,
            new InfrastructureOptions
            {
                LmStudio = new LmStudioOptions
                {
                    UseCompactModerationContractV6 = true,
                },
            });

        var result = await sut.ModerateAsync(prompt, "channel-1");

        Assert.Equal("{}", result);
        var request = Assert.IsType<LmStudioModerationRequest>(lmStudio.Request);
        Assert.Same(prompt.SystemPrompt, request.SystemPrompt);
        Assert.Same(prompt.UserPrompt, request.UserPrompt);
        Assert.Equal(LmStudioResponseSchemaKind.ModerationV6, request.ResponseSchema);
        Assert.True(request.ResponseFormat.HasValue);
        Assert.Equal(
            prompt.ResponseFormat.GetRawText(),
            request.ResponseFormat.Value.GetRawText());
        Assert.Equal(prompt.MessageIds.Count, request.MessageCount);
        Assert.Equal(prompt.ActiveRuleCodes, request.ActiveRuleCodes);
        Assert.Equal(prompt.ReplyTemplateIds, request.ReplyTemplateIds);
        Assert.Equal("channel-1", request.SchedulingKey);
    }

    [Fact]
    public async Task ModerateAsync_WhenCompactContractDisabled_UsesLegacyV5Request()
    {
        var envelope = Envelope();
        var lmStudio = new RecordingLmStudioClient();
        var sut = new CoreLocalModerationClient(
            lmStudio,
            new InfrastructureOptions
            {
                LmStudio = new LmStudioOptions
                {
                    UseCompactModerationContractV6 = false,
                },
            });

        await sut.ModerateAsync(envelope);

        var request = Assert.IsType<LmStudioModerationRequest>(lmStudio.Request);
        Assert.Equal(LmStudioResponseSchemaKind.Moderation, request.ResponseSchema);
        Assert.False(request.ResponseFormat.HasValue);
        Assert.Contains(
            $"\"envelopeVersion\":\"{ModerationRequestFactory.LegacyRequestEnvelopeVersion}\"",
            request.UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains("\"activePolicySets\"", request.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModerateAsync_ConfirmationUsesStageCorrelationAndChannelKey()
    {
        var envelope = Envelope();
        var candidate = new ModerationConfirmationCandidate(
            "m1",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            ModerationRuleCodes.PersonalAttack,
            "direct_insult",
            null,
            [],
            0.9,
            ModerationCandidateSource.SemanticClassification);
        var prompt = new ModerationPromptCompiler().CompileConfirmation(
            new ModerationConfirmationRequest(envelope, [candidate]));
        var lmStudio = new RecordingLmStudioClient();
        var sut = new CoreLocalModerationClient(
            lmStudio,
            new InfrastructureOptions
            {
                LmStudio = new LmStudioOptions
                {
                    UseCompactModerationContractV6 = true,
                },
            });

        await sut.ModerateAsync(prompt, "channel-1");

        var request = Assert.IsType<LmStudioModerationRequest>(lmStudio.Request);
        Assert.Equal($"{envelope.BatchId:D}-confirm", request.CorrelationId);
        Assert.Equal("channel-1", request.SchedulingKey);
        Assert.Equal(prompt.ResponseFormat.GetRawText(), request.ResponseFormat?.GetRawText());
    }

    private static ModerationRequestEnvelopeV3 Envelope() =>
        new(
            ModerationPromptCompiler.RequestEnvelopeVersion,
            Guid.Parse("fab55857-954a-452e-8b2b-1fcfc8b69fe4"),
            "509658",
            "Just Chatting",
            [new PolicySetRevision(PolicySetCode.General, 1)],
            [
                new ModerationRuleDefinition(
                    ModerationRuleCodes.PersonalAttack,
                    PolicySetCode.General,
                    "Beleidigung",
                    "Direkte Beleidigungen erkennen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Beleidigung eines Nutzers oder Streamers."),
            ],
            [new ModerationRequestMessageV3("m1", "du idiot", [])],
            [],
            []);

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
                    "{}",
                    new LmStudioUsageMetrics(
                        1,
                        1,
                        2,
                        1,
                        "test-model",
                        "stop")));
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
