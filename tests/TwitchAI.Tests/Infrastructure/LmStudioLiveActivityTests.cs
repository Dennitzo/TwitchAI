using System.Net;
using System.Text;
using System.Text.Json;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LmStudioLiveActivityTests
{
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
        var sut = new LmStudioClient(
            httpClient,
            metrics,
            new EmptyCredentialVault(),
            CreateOptions(),
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
        var sut = new LmStudioClient(
            httpClient,
            metrics,
            new EmptyCredentialVault(),
            CreateOptions(),
            monitor);

        await Assert.ThrowsAsync<LmStudioException>(
            () => sut.CompleteModerationAsync(Request()));

        Assert.Equal(
            [LocalAiRunState.Running, LocalAiRunState.Failed],
            activities.Select(item => item.State));
        Assert.Equal(nameof(LmStudioException), activities[^1].ErrorCode);
        Assert.Null(activities[^1].TotalTokens);
        var stored = Assert.Single(metrics.Records);
        Assert.False(stored.Succeeded);
        Assert.Equal(nameof(LmStudioException), stored.ErrorCode);
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
        var sut = new LmStudioClient(
            httpClient,
            new RecordingMetricsStore(),
            new EmptyCredentialVault(),
            CreateOptions());

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
                                """{"envelopeVersion":"twitchai.learning-test.response.v1","testId":"11111111-1111-1111-1111-111111111111","decision":{"verdict":"allow","action":"none","timeoutSeconds":null,"policySet":"G","ruleCode":"G_OTHER_ABUSE","reason":"","triggerPhrases":[],"confidence":0.8}}""",
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
        var sut = new LmStudioClient(
            httpClient,
            new RecordingMetricsStore(),
            new EmptyCredentialVault(),
            CreateOptions(),
            monitor,
            gate);

        await sut.CompleteModerationAsync(
            new LmStudioModerationRequest(
                "learning-test:11111111-1111-1111-1111-111111111111",
                "policy",
                MessageCount: 1,
                "system",
                "{}",
                ["G_OTHER_ABUSE", "BACKSEATGAMING"],
                ReplyTemplateIds: [],
                LmStudioResponseSchemaKind.LearningTest,
                LocalAiRunKind.LearningTest));

        using var document = JsonDocument.Parse(handler.RequestBody!);
        var schema = document.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema")
            .GetProperty("schema");
        Assert.Equal(
            "twitchai.learning-test.response.v1",
            schema
                .GetProperty("properties")
                .GetProperty("envelopeVersion")
                .GetProperty("const")
                .GetString());
        var decision = schema
            .GetProperty("properties")
            .GetProperty("decision");
        Assert.Equal("object", decision.GetProperty("type").GetString());
        Assert.Equal(
            ["BACKSEATGAMING", "G_OTHER_ABUSE"],
            decision
                .GetProperty("properties")
                .GetProperty("ruleCode")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Equal(1, gate.AcquireCount);
        Assert.All(
            activities,
            item => Assert.Equal(LocalAiRunKind.LearningTest, item.RunKind));
    }

    private static LmStudioModerationRequest Request(
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
            BaseAddress = new Uri("http://127.0.0.1:1234/"),
        };

    private static InfrastructureOptions CreateOptions() =>
        new()
        {
            LmStudio = new LmStudioOptions
            {
                BaseAddress = new Uri("http://127.0.0.1:1234/"),
                Model = "openai/gpt-oss-20b",
            },
        };

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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

    private sealed class EmptyCredentialVault : ILmStudioCredentialVault
    {
        public Task<string?> LoadTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SaveTokenAsync(
            string tokenValue,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteTokenAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingExecutionGate : ILocalAiExecutionGate
    {
        public int AcquireCount { get; private set; }

        public ValueTask<IAsyncDisposable> AcquireAsync(
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
