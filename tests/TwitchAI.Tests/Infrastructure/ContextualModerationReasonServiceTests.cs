using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using StoredPolicy = TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ContextualModerationReasonServiceTests
{
    [Fact]
    public async Task GenerateAsync_RepairsInvalidGuidanceWithManualPriority()
    {
        var responseNumber = 0;
        var client = new FakeLmStudioClient(
            _ => Completion(
                Interlocked.Increment(ref responseNumber) == 1
                    ? "{\"violationReason\":\"Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.\",\"guidance\":\"Du solltest Beleidigungen vermeiden.\"}"
                    : "{\"violationReason\":\"Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.\",\"guidance\":\"Abwertungen künftig bitte vermeiden.\"}"));
        var sut = CreateService(client);

        var result = await sut.GenerateAsync(Request());

        Assert.Equal(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers. Abwertungen künftig bitte vermeiden.",
            result.Reason);
        Assert.Equal(2, client.Requests.Count);
        Assert.All(
            client.Requests,
            request =>
            {
                Assert.Equal(LocalAiRunKind.ReviewReason, request.RunKind);
                Assert.Equal(LocalAiRunPriority.ManualReview, request.Priority);
                Assert.Equal("channel-1", request.SchedulingKey);
            });
        Assert.EndsWith(
            "-repair",
            client.Requests[1].CorrelationId,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_SecondInvalidResponseUsesNoPublicReason()
    {
        var client = new FakeLmStudioClient(
            _ => Completion(
                "{\"violationReason\":\"Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.\",\"guidance\":\"Du solltest das vermeiden.\"}"));
        var sut = CreateService(client);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => sut.GenerateAsync(Request()).AsTask());

        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task GenerateAsync_DeleteAcceptsEmptyParts()
    {
        var client = new FakeLmStudioClient(
            _ => Completion("{\"violationReason\":\"\",\"guidance\":\"\"}"));
        var sut = CreateService(client);

        var result = await sut.GenerateAsync(
            Request() with
            {
                Action = ModerationActionKind.Delete,
                TimeoutSeconds = null,
            });

        Assert.Empty(result.Reason);
        var properties = Assert.Single(client.Requests).ResponseFormat!.Value
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("properties");
        Assert.Equal(
            string.Empty,
            properties.GetProperty("violationReason").GetProperty("const").GetString());
        Assert.Equal(
            string.Empty,
            properties.GetProperty("guidance").GetProperty("const").GetString());
    }

    private static ContextualModerationReasonService CreateService(
        ILmStudioClient client) =>
        new(
            new FakePolicyStore(Policy()),
            new TwitchAI.Core.Services.ChatTextNormalizer(),
            client,
            new InfrastructureOptions());

    private static ContextualModerationReasonRequest Request() =>
        new(
            "@max du bist völlig unfähig",
            "channel-1",
            CategoryId: null,
            ModerationRuleCodes.PersonalAttack,
            ModerationActionKind.Timeout,
            TimeoutSeconds: 600,
            TriggerPhrases: ["du bist völlig unfähig"],
            CorrelationId: "review:case-1");

    private static StoredPolicy Policy()
    {
        var now = DateTimeOffset.UtcNow;
        return new StoredPolicy(
            "channel-1",
            CategoryId: null,
            GeneralRevision: 1,
            CategoryRevision: 1,
            PolicyHash: "policy",
            PromptFragment: string.Empty,
            Rules:
            [
                new BlacklistRuleRecord(
                    RuleId: 1,
                    ScopeId: 1,
                    ModerationRuleCodes.PersonalAttack,
                    Title: "Beleidigung und Belästigung",
                    PromptInstruction: ModerationRuleCodes.PersonalAttackPromptInstruction,
                    DefaultAction: ModerationRuleAction.Timeout,
                    DefaultTimeoutSeconds: 600,
                    PublicReason: "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
                    Priority: 50,
                    Enabled: true,
                    RowVersion: 1,
                    CreatedAt: now,
                    UpdatedAt: now,
                    ArchivedAt: null),
            ],
            Phrases: []);
    }

    private static LmStudioModerationCompletion Completion(string envelope) =>
        new(
            envelope,
            new LmStudioUsageMetrics(
                PromptTokens: 12,
                CompletionTokens: 8,
                TotalTokens: 20,
                ElapsedMilliseconds: 5,
                Model: "model",
                FinishReason: "stop"));

    private sealed class FakePolicyStore(StoredPolicy policy)
        : IPolicyStore
    {
        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StoredPolicy> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(policy);
    }

    private sealed class FakeLmStudioClient(
        Func<LmStudioModerationRequest, LmStudioModerationCompletion> response)
        : ILmStudioClient
    {
        public List<LmStudioModerationRequest> Requests { get; } = [];

        public Task<LmStudioModerationCompletion> CompleteModerationAsync(
            LmStudioModerationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
