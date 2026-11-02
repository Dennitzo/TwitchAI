using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using StoredPolicy = TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ReviewReasonServiceTests
{
    [Fact]
    public async Task GenerateAsync_UsesMessageContextAndRepairsOnceWithManualPriority()
    {
        var responseNumber = 0;
        var client = new FakeLocalAiClient(
            _ => Completion(
                Interlocked.Increment(ref responseNumber) == 1
                    ? "{\"reason\":\"Gezielte persönliche Herabsetzung von Max.\"}"
                    : "{\"reason\":\"Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.\"}"));
        var sut = CreateService(client, Policy());

        var result = await sut.GenerateAsync(Request());

        Assert.Equal(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            result.Reason);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(
            "@max du bist völlig unfähig",
            client.Requests[0].UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            ModerationRuleCodes.PersonalAttack,
            client.Requests[0].UserPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "a username tagged in the current message, with or without @",
            client.Requests[0].SystemPrompt,
            StringComparison.Ordinal);
        Assert.Contains(
            "G_SEXUAL_CONTENT",
            client.Requests[0].SystemPrompt,
            StringComparison.Ordinal);
        Assert.All(
            client.Requests,
            request =>
            {
                Assert.Equal(LocalAiRunKind.ReviewReason, request.RunKind);
                Assert.Equal(LocalAiRunPriority.ManualReview, request.Priority);
                Assert.Equal("channel-1", request.SchedulingKey);
                Assert.Equal(1, request.MessageCount);
            });
        Assert.EndsWith(
            "-repair",
            client.Requests[1].CorrelationId,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_BackseatgamingReasonMustNameBackseatgaming()
    {
        var responseNumber = 0;
        var client = new FakeLocalAiClient(
            _ => Completion(
                Interlocked.Increment(ref responseNumber) == 1
                    ? "{\"reason\":\"Unaufgeforderte Spielanweisung an einen Nutzer oder den Streamer.\"}"
                    : "{\"reason\":\"Unaufgefordertes Backseatgaming gegenüber einem Nutzer oder dem Streamer.\"}"));
        var sut = CreateService(client, BackseatgamingPolicy());

        var result = await sut.GenerateAsync(
            Request() with
            {
                RuleCode = ModerationRuleCodes.Backseatgaming,
                OriginalText = "du musst jetzt links fahren und danach springen",
                TriggerPhrases = ["links fahren"],
            });

        Assert.Contains("Backseatgaming", result.Reason, StringComparison.Ordinal);
        Assert.Equal(2, client.Requests.Count);
    }

    private static ReviewReasonService CreateService(
        ILocalAiClient client,
        StoredPolicy policy) =>
        new(
            new FakePolicyStore(policy),
            new TwitchAI.Core.Services.ChatTextNormalizer(),
            client);

    private static ReviewReasonRequest Request() =>
        new(
            "@max du bist völlig unfähig",
            "channel-1",
            CategoryId: null,
            ModerationRuleCodes.PersonalAttack,
            ModerationActionKind.Timeout,
            TimeoutSeconds: 600,
            TriggerPhrases: ["du bist völlig unfähig"],
            CorrelationId: "review:case-1");

    private static StoredPolicy Policy() => PolicyWithRule(
        ModerationRuleCodes.PersonalAttack,
        "Beleidigung und Belästigung",
        ModerationRuleCodes.PersonalAttackPromptInstruction,
        "Persönlicher Angriff.");

    private static StoredPolicy BackseatgamingPolicy() => PolicyWithRule(
        ModerationRuleCodes.Backseatgaming,
        "Backseatgaming",
        "Erkenne konkrete unaufgeforderte Spielanweisungen.",
        "Unaufgefordertes Backseatgaming.");

    private static StoredPolicy PolicyWithRule(
        string ruleCode,
        string title,
        string instruction,
        string publicReason)
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
                    ruleCode,
                    title,
                    instruction,
                    DefaultAction: ModerationRuleAction.Timeout,
                    DefaultTimeoutSeconds: 600,
                    publicReason,
                    Priority: 50,
                    Enabled: true,
                    RowVersion: 1,
                    CreatedAt: now,
                    UpdatedAt: now,
                    ArchivedAt: null),
            ],
            Phrases: []);
    }

    private static LocalAiModerationCompletion Completion(string envelope) =>
        new(
            envelope,
            new LocalAiUsageMetrics(
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

    private sealed class FakeLocalAiClient(
        Func<LocalAiModerationRequest, LocalAiModerationCompletion> response)
        : ILocalAiClient
    {
        public List<LocalAiModerationRequest> Requests { get; } = [];

        public Task<LocalAiModerationCompletion> CompleteModerationAsync(
            LocalAiModerationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(response(request));
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
