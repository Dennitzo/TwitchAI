using System.Collections.Concurrent;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using StoredPolicy = TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LearningTestServiceTests
{
    [Fact]
    public async Task AnalyzeAsync_UsesOfflineGeneralAndBackseatPolicyForWhitelistedExample()
    {
        var testIdFromRequest = Guid.Empty;
        var lmStudio = new FakeLmStudioClient(request =>
        {
            testIdFromRequest = ReadTestId(request.UserPrompt);
            return Completion(Allow(testIdFromRequest, "G_OTHER_ABUSE"));
        });
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var sut = CreateService(lmStudio, monitor: monitor);

        var result = await sut.AnalyzeAsync(new LearningTestRequest("KEKW"));

        Assert.Equal(LearningVerdict.Allow, result.Verdict);
        Assert.Equal("kekw", result.LowerText);
        Assert.Equal("KEKW", result.OriginalText);
        Assert.Equal(ModerationActionKind.None, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Empty(result.ProposedReason);
        Assert.Equal("G_OTHER_ABUSE", result.ProposedRuleCode);
        Assert.Contains(
            result.ActiveRules,
            item => item.PolicySet == PolicySetCode.General);
        Assert.Contains(
            result.ActiveRules,
            item => item.PolicySet == PolicySetCode.Backseatgaming);
        var sent = Assert.Single(lmStudio.Requests);
        Assert.Equal(LmStudioResponseSchemaKind.LearningTest, sent.ResponseSchema);
        Assert.Equal(LocalAiRunKind.LearningTest, sent.RunKind);
        Assert.DoesNotContain("KEKW", sent.UserPrompt, StringComparison.Ordinal);
        var queued = Assert.Single(activities);
        Assert.Equal(LocalAiRunState.Queued, queued.State);
        Assert.Equal(LocalAiRunKind.LearningTest, queued.RunKind);
        Assert.StartsWith("learning-test:", queued.CorrelationId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_ModeratePreservesValidActionAndNormalizesReason()
    {
        var lmStudio = new FakeLmStudioClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "moderate",
                    "timeout",
                    120,
                    "G",
                    "G_SPAM",
                    "Repeated spam directed at chat.",
                    ["spam spam"],
                    0.91));
        });
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("SPAM SPAM jetzt"));

        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(120, result.ProposedTimeoutSeconds);
        Assert.Equal("G_SPAM", result.ProposedRuleCode);
        Assert.Equal("Wiederholter Spam.", result.ProposedReason);
        Assert.Equal(["spam spam"], result.ProposedTriggerPhrases);
        Assert.False(result.UsedRuleFallback);
    }

    [Fact]
    public async Task AnalyzeAsync_DeleteForInsultBecomesDefaultTimeout()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "delete",
                    null,
                    "G",
                    "G_INSULT",
                    "Direkte Beleidigung gegen einen Nutzer.",
                    ["du idiot"],
                    0.94)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Du Idiot"));

        Assert.Equal(LearningVerdict.Moderate, result.Verdict);
        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(600, result.ProposedTimeoutSeconds);
        Assert.Equal("G_INSULT", result.ProposedRuleCode);
        Assert.Equal(
            "Direkte Beleidigung gegen einen Nutzer oder Streamer.",
            result.ProposedReason);
    }

    [Fact]
    public async Task AnalyzeAsync_DeleteForSpamBecomesLightTimeout()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "delete",
                    null,
                    "G",
                    ModerationRuleCodes.Spam,
                    "Wiederholter Spam.",
                    ["spam spam"],
                    0.91)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("SPAM SPAM"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(120, result.ProposedTimeoutSeconds);
        Assert.Equal(ModerationRuleCodes.Spam, result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_DeleteForBackseatgamingBecomesDefaultTimeout()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "delete",
                    null,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Unaufgeforderter Spielhinweis.",
                    ["geh nach links"],
                    0.93)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Geh nach links"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(600, result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.Backseatgaming,
            result.ProposedRuleCode);
    }

    [Theory]
    [InlineData("delete", null)]
    [InlineData("timeout", 120)]
    public async Task AnalyzeAsync_AdvertisingAlwaysBecomesBan(
        string action,
        int? timeoutSeconds)
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    action,
                    timeoutSeconds,
                    "G",
                    ModerationRuleCodes.Advertising,
                    "Unaufgeforderte Werbung oder Eigenwerbung.",
                    ["meinen kanal"],
                    0.97)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Besucht meinen Kanal"));

        Assert.Equal(ModerationActionKind.Ban, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.Advertising,
            result.ProposedRuleCode);
    }

    [Theory]
    [InlineData("delete", null)]
    [InlineData("timeout", 120)]
    public async Task AnalyzeAsync_BeggingAlwaysBecomesDayTimeout(
        string action,
        int? timeoutSeconds)
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    action,
                    timeoutSeconds,
                    "G",
                    ModerationRuleCodes.Begging,
                    "Betteln nach Subs, Geschenken oder Giveaways.",
                    ["schenk mir bitte einen sub"],
                    0.95)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Schenk mir bitte einen Sub"));

        Assert.Equal(ModerationActionKind.Timeout, result.ProposedAction);
        Assert.Equal(86_400, result.ProposedTimeoutSeconds);
        Assert.Equal(
            ModerationRuleCodes.Begging,
            result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_DeleteForLowContextOtherAbuseRemainsDelete()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "delete",
                    null,
                    "G",
                    ModerationRuleCodes.OtherAbuse,
                    "Unnötige Störnachricht.",
                    ["unnötiger lärm"],
                    0.83)));
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Unnötiger Lärm"));

        Assert.Equal(ModerationActionKind.Delete, result.ProposedAction);
        Assert.Null(result.ProposedTimeoutSeconds);
        Assert.Equal(ModerationRuleCodes.OtherAbuse, result.ProposedRuleCode);
    }

    [Fact]
    public async Task AnalyzeAsync_AfterSingleRepairFallsBackToGeneralBaseRule()
    {
        var lmStudio = new FakeLmStudioClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "allow",
                    "none",
                    null,
                    "G",
                    null,
                    string.Empty,
                    [],
                    0.4));
        });
        var sut = CreateService(lmStudio);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Eine harmlose Testnachricht"));

        Assert.Equal(2, lmStudio.Requests.Count);
        Assert.Contains(
            "call them \"Nutzer oder Streamer\", never only \"Nutzer\"",
            lmStudio.Requests[1].SystemPrompt,
            StringComparison.Ordinal);
        Assert.Equal("G_OTHER_ABUSE", result.ProposedRuleCode);
        Assert.Equal(PolicySetCode.General, result.ProposedPolicySet);
        Assert.True(result.UsedRuleFallback);
    }

    [Fact]
    public async Task AnalyzeAsync_UnknownExplicitRuleRemainsInvalidAfterOneRepair()
    {
        var lmStudio = new FakeLmStudioClient(request =>
        {
            var id = ReadTestId(request.UserPrompt);
            return Completion(
                Response(
                    id,
                    "allow",
                    "none",
                    null,
                    "G",
                    "G_UNKNOWN",
                    string.Empty,
                    [],
                    0.4));
        });
        var sut = CreateService(lmStudio);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Eine harmlose Testnachricht")));

        Assert.Equal(2, lmStudio.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_ExplicitInvalidPolicyIsNeverSilentlyReplaced()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "allow",
                    "none",
                    null,
                    "X",
                    "G_OTHER_ABUSE",
                    string.Empty,
                    [],
                    0.4)));
        var sut = CreateService(lmStudio);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Eine harmlose Testnachricht")));

        Assert.Contains("policy_set_invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, lmStudio.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_BackseatQuestionIsRejectedAfterSingleRepair()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Unaufgeforderter Spielhinweis.",
                    ["geh nach links"],
                    0.9)));
        var sut = CreateService(lmStudio);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Geh nach links?")));

        Assert.Contains(
            "backseat_context_insufficient",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, lmStudio.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_BackseatRequiresConcreteMultiwordTrigger()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    ModerationRuleCodes.Backseatgaming,
                    "Unaufgeforderter Spielhinweis.",
                    ["links"],
                    0.9)));
        var sut = CreateService(lmStudio);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("Geh nach links")));

        Assert.Contains(
            "backseat_evidence_insufficient",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(2, lmStudio.Requests.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_MissingRuleUsesMatchingLocalBackseatRuleAfterRepair()
    {
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(
                Response(
                    ReadTestId(request.UserPrompt),
                    "moderate",
                    "timeout",
                    600,
                    "B",
                    null,
                    "Unaufgeforderter Spielhinweis.",
                    ["geh nach links"],
                    0.9)));
        var phraseSource = new StaticPhraseRuleSource(
        [
            new PhraseRule(
                1,
                ModerationRuleCodes.Backseatgaming,
                PolicySetCode.Backseatgaming,
                "geh nach links",
                PhraseManualOverride.Active,
                1,
                0,
                IsArchived: false),
        ]);
        var sut = CreateService(lmStudio, phraseSource: phraseSource);

        var result = await sut.AnalyzeAsync(
            new LearningTestRequest("Geh nach links"));

        Assert.Equal(2, lmStudio.Requests.Count);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.ProposedRuleCode);
        Assert.Equal(PolicySetCode.Backseatgaming, result.ProposedPolicySet);
        Assert.True(result.UsedRuleFallback);
        Assert.Equal(
            [ModerationRuleCodes.Backseatgaming],
            result.LocalMatchCodes);
    }

    [Fact]
    public async Task AnalyzeAsync_IncludesBoundedLowercaseCurrentLearningExamples()
    {
        var retrieval = new StaticRetrievalStore(
        [
            new RetrievalExample(
                "SPAM SPAM",
                ModerationActionKind.Timeout,
                120,
                PolicySetCode.General,
                "Wiederholter Spam.",
                IsPositive: true,
                "G_SPAM"),
        ]);
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE")));
        var sut = CreateService(lmStudio, retrieval: retrieval);

        await sut.AnalyzeAsync(new LearningTestRequest("harmlos"));

        var sent = Assert.Single(lmStudio.Requests);
        Assert.Contains("\"learningExamples\":[", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("\"lowerText\":\"spam spam\"", sent.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SPAM SPAM", sent.UserPrompt, StringComparison.Ordinal);
        Assert.Equal(1, retrieval.CallCount);
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsResultWhenPolicyChangedDuringInference()
    {
        var original = Policy("policy-1");
        var changed = Policy("policy-2");
        var store = new FakePolicyStore(original, changed);
        var lmStudio = new FakeLmStudioClient(request =>
            Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE")));
        var sut = CreateService(lmStudio, store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.AnalyzeAsync(
                new LearningTestRequest("harmlos")));

        Assert.Contains("erneut starten", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_AllowsOnlyOneRunningTest()
    {
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lmStudio = new FakeLmStudioClient(async request =>
        {
            entered.SetResult();
            await release.Task;
            return Completion(Allow(ReadTestId(request.UserPrompt), "G_OTHER_ABUSE"));
        });
        var sut = CreateService(lmStudio);
        var first = sut.AnalyzeAsync(new LearningTestRequest("erste")).AsTask();
        await entered.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await sut.AnalyzeAsync(new LearningTestRequest("zweite")));

        release.SetResult();
        await first;
    }

    [Fact]
    public async Task IsPolicyCurrentAsync_ComparesLearningPolicyFingerprint()
    {
        var currentPolicy = Policy("policy-current");
        var store = new FakePolicyStore(currentPolicy);
        var sut = CreateService(
            new FakeLmStudioClient(
                _ => Task.FromException<LmStudioModerationCompletion>(
                    new NotSupportedException())),
            store);
        var result = Result(
            LearningPolicyRevisionFingerprint.Create(currentPolicy));

        Assert.True(await sut.IsPolicyCurrentAsync(result));
        Assert.False(
                await CreateService(
                    new FakeLmStudioClient(
                        _ => Task.FromException<LmStudioModerationCompletion>(
                            new NotSupportedException())),
                    new FakePolicyStore(Policy("different")))
                .IsPolicyCurrentAsync(result));
        Assert.False(
                await CreateService(
                    new FakeLmStudioClient(
                        _ => Task.FromException<LmStudioModerationCompletion>(
                            new NotSupportedException())),
                    new FakePolicyStore(
                        currentPolicy with
                        {
                            GeneralRevision = currentPolicy.GeneralRevision + 1,
                        }))
                .IsPolicyCurrentAsync(result));
    }

    private static LearningTestService CreateService(
        ILmStudioClient client,
        IPolicyStore? store = null,
        ILocalAiRunActivityMonitor? monitor = null,
        IPhraseRuleSource? phraseSource = null,
        IRetrievalExampleStore? retrieval = null) =>
        new(
            store ?? new FakePolicyStore(Policy("policy-1")),
            new TwitchAI.Core.Services.ChatTextNormalizer(),
            phraseSource ?? new EmptyPhraseRuleSource(),
            new BlacklistMatcher(),
            client,
            new InfrastructureOptions(),
            monitor,
            timeProvider: null,
            retrieval);

    private static StoredPolicy Policy(string hash)
    {
        var now = DateTimeOffset.UtcNow;
        return new StoredPolicy(
            GlobalPolicyStorage.BroadcasterId,
            CategoryId: null,
            GeneralRevision: 3,
            CategoryRevision: 3,
            hash,
            "prompt",
            [
                StoredRule(
                    1,
                    "G_OTHER_ABUSE",
                    "Sonstiger Verstoß",
                    "Allgemeiner Verstoß.",
                    100,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    2,
                    ModerationRuleCodes.Spam,
                    "Spam",
                    "Wiederholter Spam.",
                    80,
                    ModerationRuleAction.Timeout,
                    120,
                    now),
                StoredRule(
                    3,
                    ModerationRuleCodes.Backseatgaming,
                    "Backseatgaming",
                    "Unaufgeforderter Spielhinweis.",
                    100,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    4,
                    "G_INSULT",
                    "Beleidigung",
                    "Direkte Beleidigung eines Nutzers oder Streamers.",
                    70,
                    ModerationRuleAction.Timeout,
                    600,
                    now),
                StoredRule(
                    5,
                    ModerationRuleCodes.Advertising,
                    "Werbung",
                    "Unaufgeforderte Werbung oder Eigenwerbung.",
                    75,
                    ModerationRuleAction.Timeout,
                    120,
                    now),
                StoredRule(
                    6,
                    ModerationRuleCodes.Begging,
                    "Gebettel",
                    "Betteln nach Subs, Geschenken oder Giveaways.",
                    76,
                    ModerationRuleAction.Timeout,
                    120,
                    now),
            ],
            []);
    }

    private static BlacklistRuleRecord StoredRule(
        long id,
        string code,
        string title,
        string publicReason,
        int priority,
        ModerationRuleAction action,
        int? timeout,
        DateTimeOffset now) =>
        new(
            id,
            ScopeId: code == ModerationRuleCodes.Backseatgaming ? 2 : 1,
            code,
            title,
            "rule",
            action,
            timeout,
            publicReason,
            priority,
            Enabled: true,
            RowVersion: 1,
            now,
            now,
            ArchivedAt: null);

    private static string Allow(Guid id, string ruleCode) =>
        Response(
            id,
            "allow",
            "none",
            null,
            "G",
            ruleCode,
            string.Empty,
            [],
            0.75);

    private static string Response(
        Guid id,
        string verdict,
        string action,
        int? timeoutSeconds,
        string? policySet,
        string? ruleCode,
        string reason,
        IReadOnlyList<string> triggerPhrases,
        double confidence) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                envelopeVersion = "twitchai.learning-test.response.v1",
                testId = id,
                decision = new
                {
                    verdict,
                    action,
                    timeoutSeconds,
                    policySet,
                    ruleCode,
                    reason,
                    triggerPhrases,
                    confidence,
                },
            });

    private static Guid ReadTestId(string requestJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        return root.TryGetProperty("testId", out var testId)
            ? testId.GetGuid()
            : root
                .GetProperty("request")
                .GetProperty("testId")
                .GetGuid();
    }

    private static LmStudioModerationCompletion Completion(string envelope) =>
        new(
            envelope,
            new LmStudioUsageMetrics(
                PromptTokens: 12,
                CompletionTokens: 8,
                TotalTokens: 20,
                ElapsedMilliseconds: 5,
                "model",
                "stop"));

    private static LearningTestResult Result(string fingerprint) =>
        new(
            Guid.NewGuid(),
            "Text",
            "text",
            LearningVerdict.Allow,
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "G_OTHER_ABUSE",
            "Regelverstoß.",
            [],
            0.5,
            [],
            1,
            fingerprint,
            [],
            "model",
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            false);

    private sealed class FakePolicyStore(params StoredPolicy[] policies)
        : IPolicyStore
    {
        private readonly ConcurrentQueue<StoredPolicy> _policies = new(policies);
        private StoredPolicy _last = policies[^1];

        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<StoredPolicy> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_last);

        public Task<StoredPolicy> GetLearningPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            if (_policies.TryDequeue(out var policy))
            {
                _last = policy;
            }

            return Task.FromResult(_last);
        }
    }

    private sealed class EmptyPhraseRuleSource : IPhraseRuleSource
    {
        public ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
            string channelId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PhraseRule>>([]);
    }

    private sealed class StaticPhraseRuleSource(
        IReadOnlyList<PhraseRule> rules) : IPhraseRuleSource
    {
        public ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
            string channelId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rules);
    }

    private sealed class StaticRetrievalStore(
        IReadOnlyList<RetrievalExample> examples) : IRetrievalExampleStore
    {
        public int CallCount { get; private set; }

        public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
            string channelId,
            string? categoryId,
            TwitchAI.Core.Models.EffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(examples);

        public ValueTask<IReadOnlyList<RetrievalExample>>
            GetRelevantForLearningTestAsync(
                TwitchAI.Core.Models.EffectivePolicySnapshot policy,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(examples);
        }
    }

    private sealed class FakeLmStudioClient : ILmStudioClient
    {
        private readonly Func<
            LmStudioModerationRequest,
            Task<LmStudioModerationCompletion>> _response;

        public FakeLmStudioClient(
            Func<LmStudioModerationRequest, LmStudioModerationCompletion> response)
            : this(request => Task.FromResult(response(request)))
        {
        }

        public FakeLmStudioClient(
            Func<
                LmStudioModerationRequest,
                Task<LmStudioModerationCompletion>> response)
        {
            _response = response;
        }

        public List<LmStudioModerationRequest> Requests { get; } = [];

        public Task<LmStudioModerationCompletion> CompleteModerationAsync(
            LmStudioModerationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _response(request);
        }

        public Task<IReadOnlyList<string>> GetLoadedModelsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
