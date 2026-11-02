using System.Net;
using System.Text;
using System.Text.Json;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LocalAiLiveActivityTests
{
    [Fact]
    public async Task EnsureModelLoadedAsync_PostsBufferedModelAndReasoningBeforeInference()
    {
        var options = CreateOptions();
        options.LocalAi.ReasoningEffort = "low";
        var handler = new StaticResponseHandler(
            HttpStatusCode.OK,
            """{"model":"openai/gpt-oss-20b","status":"loaded"}""");
        using var httpClient = CreateHttpClient(handler);
        var metrics = new RecordingMetricsStore();
        var startupCalls = 0;
        using var runtime = new LocalAiRuntime(_ =>
        {
            startupCalls++;
            return Task.CompletedTask;
        });
        var sut = new LocalAiClient(httpClient, metrics, options, runtime);

        await sut.EnsureModelLoadedAsync();

        Assert.Equal(1, startupCalls);
        Assert.Equal(HttpMethod.Post, handler.RequestMethod);
        Assert.Equal("/v1/models/load", handler.RequestUri?.AbsolutePath);
        Assert.NotNull(handler.RequestBody);
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.RequestBody), handler.RequestContentLength);
        using var body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal(options.LocalAi.Model, body.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Empty(metrics.Records);
    }

    [Theory]
    [InlineData("other-model", "loaded")]
    [InlineData("openai/gpt-oss-20b", "loading")]
    public async Task EnsureModelLoadedAsync_RejectsIncorrectReadyConfirmation(
        string confirmedModel,
        string status)
    {
        var handler = new StaticResponseHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new { model = confirmedModel, status }));
        using var httpClient = CreateHttpClient(handler);
        using var runtime = new LocalAiRuntime(_ => Task.CompletedTask);
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            runtime);

        await Assert.ThrowsAsync<InvalidDataException>(() => sut.EnsureModelLoadedAsync());
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_PreservesGatewayFailureDetails()
    {
        const string responseBody = """{"error":{"message":"No GPU allocation is available"}}""";
        var handler = new StaticResponseHandler(HttpStatusCode.ServiceUnavailable, responseBody);
        using var httpClient = CreateHttpClient(handler);
        using var runtime = new LocalAiRuntime(_ => Task.CompletedTask);
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            runtime);

        var exception = await Assert.ThrowsAsync<LocalAiException>(
            () => sut.EnsureModelLoadedAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(responseBody, exception.ResponseBody);
        Assert.Contains("No GPU allocation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_RejectsEmptySelectionBeforeStartingRuntime()
    {
        var options = CreateOptions();
        options.LocalAi.Model = string.Empty;
        var handler = new StaticResponseHandler(HttpStatusCode.OK, "{}");
        using var httpClient = CreateHttpClient(handler);
        var runtimeStarted = false;
        using var runtime = new LocalAiRuntime(_ =>
        {
            runtimeStarted = true;
            return Task.CompletedTask;
        });
        var sut = new LocalAiClient(httpClient, new RecordingMetricsStore(), options, runtime);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnsureModelLoadedAsync());

        Assert.False(runtimeStarted);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task CompleteModerationAsync_PublishesRealUsageAfterMetricWasStored()
    {
        const string envelope =
            """{"envelopeVersion":"twitchai.moderation.response.v4","batchId":"batch","decisions":[]}""";
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new { content = envelope },
                        finish_reason = "stop",
                    },
                },
                usage = new
                {
                    prompt_tokens = 41,
                    completion_tokens = 7,
                    total_tokens = 48,
                },
            });
        var handler = new StaticResponseHandler(
            HttpStatusCode.OK,
            responseBody);
        using var httpClient = CreateHttpClient(handler);
        var metrics = new RecordingMetricsStore();
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var sut = new LocalAiClient(
            httpClient,
            metrics,
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask),
            monitor);

        var completion = await sut.CompleteModerationAsync(Request());

        Assert.Equal(envelope, completion.RawEnvelope);
        Assert.Collection(
            activities,
            running =>
            {
                Assert.Equal(LocalAiRunState.Running, running.State);
                Assert.Equal(3, running.MessageCount);
                Assert.Null(running.TotalTokens);
            },
            succeeded =>
            {
                Assert.Equal(LocalAiRunState.Succeeded, succeeded.State);
                Assert.Equal(41, succeeded.PromptTokens);
                Assert.Equal(7, succeeded.CompletionTokens);
                Assert.Equal(48, succeeded.TotalTokens);
                Assert.Equal("openai/gpt-oss-20b", succeeded.ModelId);
                Assert.True(succeeded.ElapsedMilliseconds >= 0);
            });
        var stored = Assert.Single(metrics.Records);
        Assert.True(stored.Succeeded);
        Assert.Equal(48, stored.TotalTokens);
        Assert.Equal(
            activities[^1].ElapsedMilliseconds,
            stored.ElapsedMilliseconds);
    }

    [Fact]
    public async Task CompleteModerationAsync_OnHttpErrorPublishesFailureAndFailureMetric()
    {
        var handler = new StaticResponseHandler(
            HttpStatusCode.InternalServerError,
            """{"error":{"message":"model unavailable"}}""");
        using var httpClient = CreateHttpClient(handler);
        var metrics = new RecordingMetricsStore();
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var sut = new LocalAiClient(
            httpClient,
            metrics,
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask),
            monitor);

        await Assert.ThrowsAsync<LocalAiException>(
            () => sut.CompleteModerationAsync(Request()));

        Assert.Equal(
            [LocalAiRunState.Running, LocalAiRunState.Failed],
            activities.Select(item => item.State));
        Assert.Equal(nameof(LocalAiException), activities[^1].ErrorCode);
        Assert.Null(activities[^1].TotalTokens);
        var stored = Assert.Single(metrics.Records);
        Assert.False(stored.Succeeded);
        Assert.Equal(nameof(LocalAiException), stored.ErrorCode);
    }

    [Fact]
    public async Task CompleteModerationAsync_MalformedInnerEnvelopeIsReturnedUnchangedForRepair()
    {
        const string malformedEnvelope = "{this is not valid json";
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new { content = malformedEnvelope },
                        finish_reason = "stop",
                    },
                },
                usage = new
                {
                    prompt_tokens = 17,
                    completion_tokens = 5,
                    total_tokens = 22,
                },
            });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, responseBody);
        using var httpClient = CreateHttpClient(handler);
        var metrics = new RecordingMetricsStore();
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var sut = new LocalAiClient(
            httpClient,
            metrics,
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask),
            monitor);

        var completion = await sut.CompleteModerationAsync(Request());

        Assert.Equal(malformedEnvelope, completion.RawEnvelope);
        Assert.Equal(
            [LocalAiRunState.Running, LocalAiRunState.Succeeded],
            activities.Select(item => item.State));
        Assert.Equal(22, completion.Usage.TotalTokens);
        var stored = Assert.Single(metrics.Records);
        Assert.True(stored.Succeeded);
        Assert.Null(stored.ErrorCode);
    }

    [Fact]
    public void ActivityMonitor_RejectsInvalidBatchMetadata()
    {
        var sut = new LocalAiRunActivityMonitor();
        var activity = new LocalAiRunActivity(
            "batch-1",
            LocalAiRunState.Queued,
            MessageCount: 0,
            ModelId: null,
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            ElapsedMilliseconds: null,
            DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => sut.Publish(activity));
    }

    [Fact]
    public void ActivityMonitor_PublishesSupersededLifecycleState()
    {
        var sut = new LocalAiRunActivityMonitor();
        LocalAiRunActivity? observed = null;
        sut.ActivityChanged += (_, args) => observed = args.Activity;
        var activity = new LocalAiRunActivity(
            "batch-replaced",
            LocalAiRunState.Superseded,
            MessageCount: 4,
            ModelId: "openai/gpt-oss-20b",
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            ElapsedMilliseconds: null,
            DateTimeOffset.UtcNow,
            ErrorCode: "policy_revision_changed");

        sut.Publish(activity);

        Assert.Same(activity, observed);
        Assert.Equal(LocalAiRunState.Superseded, observed!.State);
    }

    [Fact]
    public void ActivityMonitor_FaultySubscriberDoesNotBlockFollowingSubscribers()
    {
        var sut = new LocalAiRunActivityMonitor();
        var activity = new LocalAiRunActivity(
            "batch-observer-failure",
            LocalAiRunState.Running,
            MessageCount: 2,
            ModelId: "openai/gpt-oss-20b",
            PromptTokens: null,
            CompletionTokens: null,
            TotalTokens: null,
            ElapsedMilliseconds: null,
            DateTimeOffset.UtcNow);
        LocalAiRunActivity? observed = null;
        sut.ActivityChanged += (_, _) =>
            throw new InvalidOperationException("Detached UI observer.");
        sut.ActivityChanged += (_, args) => observed = args.Activity;

        var exception = Record.Exception(() => sut.Publish(activity));

        Assert.Null(exception);
        Assert.Same(activity, observed);
    }

    [Fact]
    public async Task CompleteModerationAsync_SchemaEnumeratesOnlyActiveRuleCodes()
    {
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content =
                                """{"envelopeVersion":"twitchai.moderation.response.v4","batchId":"batch-1","decisions":[]}""",
                        },
                        finish_reason = "stop",
                    },
                },
            });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, responseBody);
        using var httpClient = CreateHttpClient(handler);
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask));

        await sut.CompleteModerationAsync(
            Request(["G_INSULT", "G_PERSONAL_DATA"], ["r1", "r2"]));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var schema = document.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.Equal(
            "twitchai.moderation.response.v4",
            schema
                .GetProperty("properties")
                .GetProperty("envelopeVersion")
                .GetProperty("const")
                .GetString());
        var actionEnum = schema
            .GetProperty("properties")
            .GetProperty("decisions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("action")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Equal(["none", "delete", "timeout", "ban"], actionEnum);
        var timeoutEnum = schema
            .GetProperty("properties")
            .GetProperty("decisions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("timeoutSeconds")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Null
                ? (int?)null
                : item.GetInt32())
            .ToArray();
        Assert.Equal([null, 2, 120, 600, 86_400], timeoutEnum);
        var ruleCodeEnum = schema
            .GetProperty("properties")
            .GetProperty("decisions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("ruleCode")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Null ? null : item.GetString())
            .ToArray();
        Assert.Collection(
            ruleCodeEnum,
            item => Assert.Equal("G_INSULT", item),
            item => Assert.Equal("G_PERSONAL_DATA", item),
            Assert.Null);
        var replyTemplateEnum = schema
            .GetProperty("properties")
            .GetProperty("decisions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("replyTemplateId")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Null ? null : item.GetString())
            .ToArray();
        Assert.Collection(
            replyTemplateEnum,
            item => Assert.Equal("r1", item),
            item => Assert.Equal("r2", item),
            Assert.Null);
    }

    [Fact]
    public async Task CompleteModerationAsync_UsesPrecompiledResponseFormatUnchanged()
    {
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new { content = "{}" },
                        finish_reason = "stop",
                    },
                },
            });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, responseBody);
        using var httpClient = CreateHttpClient(handler);
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask));
        var responseFormat = JsonSerializer.SerializeToElement(
            new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "precompiled",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                    },
                },
            });

        await sut.CompleteModerationAsync(
            new LocalAiModerationRequest(
                "batch-1",
                "policy",
                1,
                "system",
                "{}",
                ["G_INSULT"],
                [],
                LocalAiResponseSchemaKind.ModerationV6,
                ResponseFormat: responseFormat));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(
            responseFormat.GetRawText(),
            document.RootElement.GetProperty("response_format").GetRawText());
    }

    [Fact]
    public async Task CompleteModerationAsync_LearningTestUsesDedicatedSchemaAndActivityKind()
    {
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content =
                                """{"envelopeVersion":"twitchai.learning-test.response.v2","testId":"11111111-1111-1111-1111-111111111111","decision":{"verdict":"allow","action":"none","timeoutSeconds":null,"policySet":"G","ruleCode":"G_PERSONAL_ATTACK","evidenceKind":null,"sensitiveDataType":null,"violationReason":"","guidance":"","triggerPhrases":[],"confidence":0.8}}""",
                        },
                        finish_reason = "stop",
                    },
                },
            });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, responseBody);
        using var httpClient = CreateHttpClient(handler);
        var monitor = new LocalAiRunActivityMonitor();
        var activities = new List<LocalAiRunActivity>();
        monitor.ActivityChanged += (_, args) => activities.Add(args.Activity);
        var gate = new RecordingExecutionGate();
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask),
            monitor,
            gate);

        await sut.CompleteModerationAsync(
            new LocalAiModerationRequest(
                "learning-test:11111111-1111-1111-1111-111111111111",
                "policy",
                MessageCount: 1,
                "system",
                "{}",
                ["G_PERSONAL_DATA", "G_PERSONAL_ATTACK", "BACKSEATGAMING"],
                ReplyTemplateIds: [],
                LocalAiResponseSchemaKind.LearningTest,
                LocalAiRunKind.LearningTest));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var schema = document.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.Equal(
            LearningTestPromptFactory.ResponseEnvelopeVersion,
            schema
                .GetProperty("properties")
                .GetProperty("envelopeVersion")
                .GetProperty("const")
                .GetString());
        var decision = schema
            .GetProperty("properties")
            .GetProperty("decision");
        var alternatives = decision.GetProperty("oneOf");
        Assert.Equal(9, alternatives.GetArrayLength());

        var allowPersonalAttack = FindLearningDecisionAlternative(
            alternatives,
            "allow",
            "G_PERSONAL_ATTACK");
        var allowProperties = allowPersonalAttack.GetProperty("properties");
        Assert.Equal(
            "null",
            allowProperties.GetProperty("evidenceKind").GetProperty("type").GetString());
        Assert.Equal(
            "null",
            allowProperties.GetProperty("sensitiveDataType").GetProperty("type").GetString());
        Assert.Equal(
            "none",
            allowProperties.GetProperty("action").GetProperty("const").GetString());
        Assert.Equal(
            "null",
            allowProperties.GetProperty("timeoutSeconds").GetProperty("type").GetString());
        Assert.Equal(
            string.Empty,
            allowProperties
                .GetProperty("violationReason")
                .GetProperty("const")
                .GetString());
        Assert.Equal(
            string.Empty,
            allowProperties.GetProperty("guidance").GetProperty("const").GetString());
        Assert.Equal(
            0,
            allowProperties.GetProperty("triggerPhrases").GetProperty("maxItems").GetInt32());

        var moderatePersonalAttack = FindLearningDecisionAlternative(
            alternatives,
            "moderate",
            "G_PERSONAL_ATTACK");
        var personalAttackProperties = moderatePersonalAttack.GetProperty("properties");
        Assert.Equal(
            ["direct_insult", "unwanted_intimate_approach", "verified_harassment"],
            personalAttackProperties
                .GetProperty("evidenceKind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            "null",
            personalAttackProperties
                .GetProperty("sensitiveDataType")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "G",
            personalAttackProperties
                .GetProperty("policySet")
                .GetProperty("const")
                .GetString());
        Assert.Equal(
            ModerationReasonComposer.MaximumViolationReasonLength,
            personalAttackProperties
                .GetProperty("violationReason")
                .GetProperty("maxLength")
                .GetInt32());
        Assert.Equal(
            ModerationReasonComposer.MaximumGuidanceLength,
            personalAttackProperties
                .GetProperty("guidance")
                .GetProperty("maxLength")
                .GetInt32());

        var moderatePersonalData = FindLearningDecisionAlternative(
            alternatives,
            "moderate",
            "G_PERSONAL_DATA");
        var personalDataProperties = moderatePersonalData.GetProperty("properties");
        Assert.Equal(
            ["disclosure", "targeted_request", "threat_to_disclose"],
            personalDataProperties
                .GetProperty("evidenceKind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            ["address", "phone", "email", "ip", "identity", "location", "credentials", "other"],
            personalDataProperties
                .GetProperty("sensitiveDataType")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));

        var moderateBackseat = FindLearningDecisionAlternative(
            alternatives,
            "moderate",
            "BACKSEATGAMING");
        var backseatProperties = moderateBackseat.GetProperty("properties");
        Assert.Equal(
            ["hidden_danger", "instruction", "solution", "spoiler"],
            backseatProperties
                .GetProperty("evidenceKind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(
            "B",
            backseatProperties
                .GetProperty("policySet")
                .GetProperty("const")
                .GetString());
        Assert.Equal(1, gate.AcquireCount);
        Assert.All(
            activities,
            item => Assert.Equal(LocalAiRunKind.LearningTest, item.RunKind));
    }

    [Fact]
    public async Task CompleteModerationAsync_LearningTestSchemaMatchesRuleActionMatrix()
    {
        string[] activeRuleCodes =
        [
            "G_PERSONAL_DATA",
            "G_HARM",
            "G_HATE",
            "G_SEXUAL_CONTENT",
            "G_PERSONAL_ATTACK",
            "G_BEGGING",
            "G_SPAM",
            "BACKSEATGAMING",
            "G_SELF_PROMOTION",
            "G_BOT_SCAM",
        ];
        var alternatives = await CaptureLearningDecisionAlternativesAsync(
            activeRuleCodes);
        Assert.Equal(30, alternatives.GetArrayLength());

        var expectedModerationActions = new Dictionary<string, string[]>(
            StringComparer.Ordinal)
        {
            ["G_PERSONAL_DATA"] = ["ban:null", "timeout:86400"],
            ["G_HARM"] = ["ban:null", "timeout:86400"],
            ["G_HATE"] = ["ban:null", "timeout:600", "timeout:86400"],
            ["G_SEXUAL_CONTENT"] = ["timeout:600", "timeout:86400"],
            ["G_PERSONAL_ATTACK"] = ["ban:null", "timeout:600", "timeout:86400"],
            ["G_BEGGING"] = ["timeout:86400"],
            ["G_SPAM"] = ["delete:null", "timeout:2", "timeout:120"],
            ["BACKSEATGAMING"] = ["timeout:600"],
            ["G_SELF_PROMOTION"] = ["ban:null", "timeout:86400"],
            ["G_BOT_SCAM"] = ["ban:null"],
        };

        foreach (var ruleCode in activeRuleCodes)
        {
            var allow = alternatives
                .EnumerateArray()
                .Where(item => HasLearningDecisionIdentity(item, "allow", ruleCode))
                .ToArray();
            Assert.Single(allow);
            Assert.Equal(
                "none:null",
                GetLearningDecisionActionKey(allow[0]));

            var actualModerationActions = alternatives
                .EnumerateArray()
                .Where(item => HasLearningDecisionIdentity(item, "moderate", ruleCode))
                .Select(GetLearningDecisionActionKey)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                expectedModerationActions[ruleCode].Order(StringComparer.Ordinal),
                actualModerationActions);
            Assert.DoesNotContain(
                actualModerationActions,
                item => item.StartsWith("none:", StringComparison.Ordinal));
        }
    }

    private static async Task<JsonElement> CaptureLearningDecisionAlternativesAsync(
        IReadOnlyList<string> activeRuleCodes)
    {
        var responseBody = JsonSerializer.Serialize(
            new
            {
                model = "openai/gpt-oss-20b",
                choices = new[]
                {
                    new
                    {
                        message = new { content = "{}" },
                        finish_reason = "stop",
                    },
                },
            });
        var handler = new StaticResponseHandler(HttpStatusCode.OK, responseBody);
        using var httpClient = CreateHttpClient(handler);
        var sut = new LocalAiClient(
            httpClient,
            new RecordingMetricsStore(),
            CreateOptions(),
            new LocalAiRuntime(_ => Task.CompletedTask));

        await sut.CompleteModerationAsync(
            new LocalAiModerationRequest(
                "learning-test:action-matrix",
                "policy",
                MessageCount: 1,
                "system",
                "{}",
                activeRuleCodes,
                ReplyTemplateIds: [],
                LocalAiResponseSchemaKind.LearningTest,
                LocalAiRunKind.LearningTest));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        return document.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema")
            .GetProperty("properties")
            .GetProperty("decision")
            .GetProperty("oneOf")
            .Clone();
    }

    private static string GetLearningDecisionActionKey(JsonElement alternative)
    {
        var properties = alternative.GetProperty("properties");
        var action = properties.GetProperty("action").GetProperty("const").GetString();
        var timeout = properties.GetProperty("timeoutSeconds");
        return timeout.GetProperty("type").GetString() == "null"
            ? $"{action}:null"
            : $"{action}:{timeout.GetProperty("const").GetInt32()}";
    }

    private static bool HasLearningDecisionIdentity(
        JsonElement alternative,
        string verdict,
        string ruleCode)
    {
        var properties = alternative.GetProperty("properties");
        return string.Equals(
                   properties.GetProperty("verdict").GetProperty("const").GetString(),
                   verdict,
                   StringComparison.Ordinal)
               && string.Equals(
                   properties.GetProperty("ruleCode").GetProperty("const").GetString(),
                   ruleCode,
                   StringComparison.Ordinal);
    }

    private static JsonElement FindLearningDecisionAlternative(
        JsonElement alternatives,
        string verdict,
        string ruleCode)
    {
        foreach (var alternative in alternatives.EnumerateArray())
        {
            if (HasLearningDecisionIdentity(alternative, verdict, ruleCode))
            {
                return alternative;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Missing learning decision alternative '{verdict}/{ruleCode}'.");
    }

    private static LocalAiModerationRequest Request(
        IReadOnlyList<string>? activeRuleCodes = null,
        IReadOnlyList<string>? replyTemplateIds = null) =>
        new(
            "batch-1",
            "G:1",
            MessageCount: 3,
            "system",
            """{"messages":[]}""",
            activeRuleCodes,
            replyTemplateIds);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8180/"),
        };

    private static InfrastructureOptions CreateOptions() =>
        new()
        {
            LocalAi = new LocalAiOptions
            {
                BaseAddress = new Uri("http://127.0.0.1:8180/"),
                Model = "openai/gpt-oss-20b",
            },
        };

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public HttpMethod? RequestMethod { get; private set; }

        public Uri? RequestUri { get; private set; }

        public long? RequestContentLength { get; private set; }

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            RequestMethod = request.Method;
            RequestUri = request.RequestUri;
            RequestContentLength = request.Content?.Headers.ContentLength;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class RecordingMetricsStore : ILlmRunMetricsStore
    {
        public List<LlmRunMetric> Records { get; } = [];

        public Task RecordAsync(
            LlmRunMetric metric,
            CancellationToken cancellationToken = default)
        {
            Records.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionGate : ILocalAiExecutionGate
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IAsyncDisposable> AcquireAsync(
            LocalAiRunPriority priority = LocalAiRunPriority.Normal,
            string? schedulingKey = null,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return ValueTask.FromResult<IAsyncDisposable>(new NoopLease());
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
