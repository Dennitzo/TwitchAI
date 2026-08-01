using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Infrastructure.AI;

public enum LmStudioResponseSchemaKind
{
    Moderation,
    ModerationV6,
    LearningTest,
}

public sealed record LmStudioModerationRequest(
    string CorrelationId,
    string PolicyHash,
    int MessageCount,
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<string>? ActiveRuleCodes = null,
    IReadOnlyList<string>? ReplyTemplateIds = null,
    LmStudioResponseSchemaKind ResponseSchema =
        LmStudioResponseSchemaKind.Moderation,
    LocalAiRunKind RunKind = LocalAiRunKind.Moderation,
    JsonElement? ResponseFormat = null);

public sealed record LmStudioUsageMetrics(
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long ElapsedMilliseconds,
    string Model,
    string? FinishReason);

public sealed record LmStudioModerationCompletion(
    string RawEnvelope,
    LmStudioUsageMetrics Usage);

public sealed class LmStudioException : HttpRequestException
{
    public LmStudioException(
        string message,
        HttpStatusCode statusCode,
        string? responseBody)
        : base(message, null, statusCode)
    {
        ResponseBody = responseBody;
    }

    public string? ResponseBody { get; }
}

public interface ILmStudioClient
{
    Task<LmStudioModerationCompletion> CompleteModerationAsync(
        LmStudioModerationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetLoadedModelsAsync(
        CancellationToken cancellationToken = default);
}

public interface ILlmRunMetricsStore
{
    Task RecordAsync(
        LlmRunMetric metric,
        CancellationToken cancellationToken = default);
}

public sealed record LlmRunMetric(
    string CorrelationId,
    string ModelId,
    string PolicyHash,
    int MessageCount,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    long ElapsedMilliseconds,
    string? FinishReason,
    bool Succeeded,
    string? ErrorCode,
    DateTimeOffset RequestedAt);

public sealed class SqliteLlmRunMetricsStore : ILlmRunMetricsStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public SqliteLlmRunMetricsStore(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task RecordAsync(
        LlmRunMetric metric,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metric);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ai_run_metrics(
                correlation_id, model_id, policy_hash, message_count,
                prompt_tokens, completion_tokens, total_tokens,
                elapsed_ms, finish_reason, succeeded, error_code, requested_utc)
            VALUES(
                @correlation_id, @model_id, @policy_hash, @message_count,
                @prompt_tokens, @completion_tokens, @total_tokens,
                @elapsed_ms, @finish_reason, @succeeded, @error_code, @requested_utc);
            """;
        command.Parameters.AddWithValue("@correlation_id", metric.CorrelationId);
        command.Parameters.AddWithValue("@model_id", metric.ModelId);
        command.Parameters.AddWithValue("@policy_hash", metric.PolicyHash);
        command.Parameters.AddWithValue("@message_count", metric.MessageCount);
        command.Parameters.AddWithValue("@prompt_tokens", SqliteData.DbValue(metric.PromptTokens));
        command.Parameters.AddWithValue(
            "@completion_tokens",
            SqliteData.DbValue(metric.CompletionTokens));
        command.Parameters.AddWithValue("@total_tokens", SqliteData.DbValue(metric.TotalTokens));
        command.Parameters.AddWithValue("@elapsed_ms", metric.ElapsedMilliseconds);
        command.Parameters.AddWithValue("@finish_reason", SqliteData.DbValue(metric.FinishReason));
        command.Parameters.AddWithValue("@succeeded", metric.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("@error_code", SqliteData.DbValue(metric.ErrorCode));
        command.Parameters.AddWithValue(
            "@requested_utc",
            SqliteData.ToDatabaseTimestamp(metric.RequestedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class LmStudioClient : ILmStudioClient
{
    private readonly HttpClient _httpClient;
    private readonly ILlmRunMetricsStore _metricsStore;
    private readonly ILmStudioCredentialVault _credentialVault;
    private readonly LmStudioOptions _options;
    private readonly ILocalAiRunActivityMonitor? _activityMonitor;
    private readonly ILocalAiExecutionGate _executionGate;

    public LmStudioClient(
        HttpClient httpClient,
        ILlmRunMetricsStore metricsStore,
        ILmStudioCredentialVault credentialVault,
        InfrastructureOptions options,
        ILocalAiRunActivityMonitor? activityMonitor = null,
        ILocalAiExecutionGate? executionGate = null)
    {
        _httpClient = httpClient;
        _metricsStore = metricsStore;
        _credentialVault = credentialVault;
        _options = options.LmStudio;
        _activityMonitor = activityMonitor;
        _executionGate = executionGate ?? LocalAiExecutionGate.ProcessWide;
    }

    public async Task<LmStudioModerationCompletion> CompleteModerationAsync(
        LmStudioModerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MessageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.MessageCount,
                "A moderation request must contain at least one message.");
        }

        await using var executionLease = await _executionGate
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        var requestedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        PublishActivity(
            request,
            LocalAiRunState.Running,
            _options.Model,
            elapsedMilliseconds: null,
            occurredAtUtc: requestedAt);
        try
        {
            object responseFormat = request.ResponseFormat is { } compiledResponseFormat
                ? compiledResponseFormat
                : CreateResponseFormat(
                    request.ActiveRuleCodes,
                    request.ReplyTemplateIds,
                    request.ResponseSchema);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                "v1/chat/completions")
            {
                Content = JsonContent.Create(
                    new
                    {
                        model = _options.Model,
                        temperature = _options.Temperature,
                        reasoning_effort = "low",
                        max_tokens = _options.MaximumOutputTokens,
                        stream = false,
                        messages = new object[]
                        {
                            new { role = "system", content = request.SystemPrompt },
                            new { role = "user", content = request.UserPrompt },
                        },
                        response_format = responseFormat,
                    }),
            };
            await ApplyOptionalAuthorizationAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(
                    httpRequest,
                    cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LmStudioException(
                    TryReadError(body) ?? response.ReasonPhrase ?? "LM Studio request failed.",
                    response.StatusCode,
                    body);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var choice = root.GetProperty("choices").EnumerateArray().FirstOrDefault();
            if (choice.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("LM Studio response contains no completion choice.");
            }

            var content = choice.GetProperty("message").GetProperty("content").GetString()
                ?? throw new InvalidDataException("LM Studio returned an empty moderation envelope.");
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException(
                    "LM Studio returned an empty moderation envelope.");
            }

            // Return the model payload unchanged. The domain validator owns
            // parsing and can send malformed JSON through the single bounded
            // repair request. Parsing here used to terminate the whole live
            // inference loop before that recovery path could run.

            stopwatch.Stop();
            var usage = ParseUsage(
                root,
                stopwatch.ElapsedMilliseconds,
                OptionalString(root, "model") ?? _options.Model,
                OptionalString(choice, "finish_reason"));
            await _metricsStore.RecordAsync(
                new LlmRunMetric(
                    request.CorrelationId,
                    usage.Model,
                    request.PolicyHash,
                    request.MessageCount,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    usage.ElapsedMilliseconds,
                    usage.FinishReason,
                    Succeeded: true,
                    ErrorCode: null,
                    requestedAt),
                cancellationToken).ConfigureAwait(false);
            PublishActivity(
                request,
                LocalAiRunState.Succeeded,
                usage.Model,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                usage.ElapsedMilliseconds,
                DateTimeOffset.UtcNow);
            return new LmStudioModerationCompletion(content, usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            PublishActivity(
                request,
                LocalAiRunState.Failed,
                _options.Model,
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                occurredAtUtc: DateTimeOffset.UtcNow,
                errorCode: "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            try
            {
                await _metricsStore.RecordAsync(
                    new LlmRunMetric(
                        request.CorrelationId,
                        _options.Model,
                        request.PolicyHash,
                        request.MessageCount,
                        PromptTokens: null,
                        CompletionTokens: null,
                        TotalTokens: null,
                        stopwatch.ElapsedMilliseconds,
                        FinishReason: null,
                        Succeeded: false,
                        ErrorCode: exception.GetType().Name,
                        requestedAt),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                PublishActivity(
                    request,
                    LocalAiRunState.Failed,
                    _options.Model,
                    elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                    occurredAtUtc: DateTimeOffset.UtcNow,
                    errorCode: exception.GetType().Name);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetLoadedModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/models");
        await ApplyOptionalAuthorizationAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new LmStudioException(
                TryReadError(body) ?? response.ReasonPhrase ?? "LM Studio model lookup failed.",
                response.StatusCode,
                body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        JsonElement data;
        if (root.TryGetProperty("models", out var models) &&
            models.ValueKind == JsonValueKind.Array)
        {
            data = models;
        }
        else if (!root.TryGetProperty("data", out data) ||
                 data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("LM Studio models response is missing its data array.");
        }

        return data.EnumerateArray()
            .Select(item => OptionalString(item, "id"))
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
    }

    private async Task ApplyOptionalAuthorizationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _credentialVault.LoadTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token);
        }
    }

    private static LmStudioUsageMetrics ParseUsage(
        JsonElement root,
        long elapsedMilliseconds,
        string model,
        string? finishReason)
    {
        if (!root.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object)
        {
            return new LmStudioUsageMetrics(
                null,
                null,
                null,
                elapsedMilliseconds,
                model,
                finishReason);
        }

        return new LmStudioUsageMetrics(
            OptionalInt32(usage, "prompt_tokens"),
            OptionalInt32(usage, "completion_tokens"),
            OptionalInt32(usage, "total_tokens"),
            elapsedMilliseconds,
            model,
            finishReason);
    }

    private static object CreateResponseFormat(
        IReadOnlyList<string>? activeRuleCodes,
        IReadOnlyList<string>? replyTemplateIds,
        LmStudioResponseSchemaKind responseSchema)
    {
        var ruleCodes = (activeRuleCodes ?? [])
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Cast<object?>()
            .Append(null)
            .ToArray();
        var templateIds = (replyTemplateIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Cast<object?>()
            .Append(null)
            .ToArray();
        return responseSchema switch
        {
            LmStudioResponseSchemaKind.Moderation =>
                CreateModerationResponseFormat(ruleCodes, templateIds),
            LmStudioResponseSchemaKind.ModerationV6 =>
                throw new InvalidOperationException(
                    "The compact moderation contract requires a compiled response format."),
            LmStudioResponseSchemaKind.LearningTest =>
                CreateLearningTestResponseFormat(ruleCodes),
            _ => throw new ArgumentOutOfRangeException(
                nameof(responseSchema),
                responseSchema,
                null),
        };
    }

    private static object CreateModerationResponseFormat(
        object?[] ruleCodes,
        object?[] templateIds)
    {
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "moderation_decisions",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "envelopeVersion", "batchId", "decisions" },
                    properties = new
                    {
                        envelopeVersion = new
                        {
                            type = "string",
                            @const = ModerationEnvelopeValidator.ResponseEnvelopeVersion,
                        },
                        batchId = new
                        {
                            type = "string",
                        },
                        decisions = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                required = new[]
                                {
                                    "messageId",
                                    "action",
                                    "timeoutSeconds",
                                    "policySet",
                                    "ruleCode",
                                    "reason",
                                    "triggerPhrases",
                                    "confidence",
                                    "replyTemplateId",
                                    "replyConfidence",
                                },
                                properties = new
                                {
                                    messageId = new { type = "string" },
                                    action = new
                                    {
                                        type = "string",
                                        @enum = new[] { "none", "delete", "timeout", "ban" },
                                    },
                                    timeoutSeconds = new
                                    {
                                        type = new[] { "integer", "null" },
                                        @enum = new object?[]
                                        {
                                            null,
                                            2,
                                            120,
                                            600,
                                            86_400,
                                        },
                                    },
                                    policySet = new
                                    {
                                        type = new[] { "string", "null" },
                                        @enum = new object?[]
                                        {
                                            "G",
                                            "B",
                                            null,
                                        },
                                    },
                                    ruleCode = new
                                    {
                                        type = new[] { "string", "null" },
                                        @enum = ruleCodes,
                                    },
                                    reason = new
                                    {
                                        type = "string",
                                        maxLength = 120,
                                    },
                                    triggerPhrases = new
                                    {
                                        type = "array",
                                        maxItems = ModerationTriggerEvidenceNormalizer.MaximumEvidencePhrases,
                                        items = new
                                        {
                                            type = "string",
                                            maxLength = ModerationTriggerEvidenceNormalizer.MaximumEvidenceCharacters,
                                        },
                                    },
                                    confidence = new
                                    {
                                        type = "number",
                                        minimum = 0,
                                        maximum = 1,
                                    },
                                    replyTemplateId = new
                                    {
                                        type = new[] { "string", "null" },
                                        @enum = templateIds,
                                    },
                                    replyConfidence = new
                                    {
                                        type = new[] { "number", "null" },
                                        minimum = 0,
                                        maximum = 1,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    internal static int GetLearningTestResponseFormatCharacterCount(
        IEnumerable<string> activeRuleCodes)
    {
        ArgumentNullException.ThrowIfNull(activeRuleCodes);
        var ruleCodes = activeRuleCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .Cast<object?>()
            .Append(null)
            .ToArray();
        return JsonSerializer.Serialize(
                CreateLearningTestResponseFormat(ruleCodes))
            .Length;
    }

    private static object CreateLearningTestResponseFormat(object?[] nullableRuleCodes)
    {
        var ruleCodes = nullableRuleCodes
            .Where(item => item is not null)
            .ToArray();
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "learning_test_decision",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "envelopeVersion", "testId", "decision" },
                    properties = new
                    {
                        envelopeVersion = new
                        {
                            type = "string",
                            @const = LearningTestPromptFactory.ResponseEnvelopeVersion,
                        },
                        testId = new { type = "string" },
                        decision = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[]
                            {
                                "verdict",
                                "action",
                                "timeoutSeconds",
                                "policySet",
                                "ruleCode",
                                "reason",
                                "triggerPhrases",
                                "confidence",
                            },
                            properties = new
                            {
                                verdict = new
                                {
                                    type = "string",
                                    @enum = new[] { "allow", "moderate" },
                                },
                                action = new
                                {
                                    type = "string",
                                    @enum = new[] { "none", "delete", "timeout", "ban" },
                                },
                                timeoutSeconds = new
                                {
                                    type = new[] { "integer", "null" },
                                    @enum = new object?[]
                                    {
                                        null,
                                        2,
                                        120,
                                        600,
                                        86_400,
                                    },
                                },
                                policySet = new
                                {
                                    type = "string",
                                    @enum = new[] { "G", "B" },
                                },
                                ruleCode = new
                                {
                                    type = "string",
                                    @enum = ruleCodes,
                                },
                                reason = new
                                {
                                    type = "string",
                                    maxLength = 120,
                                },
                                triggerPhrases = new
                                {
                                    type = "array",
                                    maxItems = ModerationTriggerEvidenceNormalizer.MaximumEvidencePhrases,
                                    items = new
                                    {
                                        type = "string",
                                        maxLength = ModerationTriggerEvidenceNormalizer.MaximumEvidenceCharacters,
                                    },
                                },
                                confidence = new
                                {
                                    type = "number",
                                    minimum = 0,
                                    maximum = 1,
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString(),
                    JsonValueKind.Object => OptionalString(error, "message"),
                    _ => null,
                };
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int? OptionalInt32(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) &&
               property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private void PublishActivity(
        LmStudioModerationRequest request,
        LocalAiRunState state,
        string modelId,
        int? promptTokens = null,
        int? completionTokens = null,
        int? totalTokens = null,
        long? elapsedMilliseconds = null,
        DateTimeOffset? occurredAtUtc = null,
        string? errorCode = null)
    {
        _activityMonitor?.Publish(
            new LocalAiRunActivity(
                request.CorrelationId,
                state,
                request.MessageCount,
                modelId,
                promptTokens,
                completionTokens,
                totalTokens,
                elapsedMilliseconds,
                occurredAtUtc ?? DateTimeOffset.UtcNow,
                errorCode,
                request.RunKind));
    }
}
