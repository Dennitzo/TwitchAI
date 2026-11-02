using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Gateway.Smoke;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<int> Main(string[] args)
    {
        var arguments = ParseArguments(args);
        var output = Path.GetFullPath(arguments.GetValueOrDefault("output")
            ?? "artifacts/validation/gateway-live-smoke.json");
        var checks = new List<CheckResult>();
        var stages = new List<StageResult>();
        var caseResults = new List<CaseResult>();
        var metrics = new MemoryMetricsStore();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        string? fatalError = null;
        var model = arguments.GetValueOrDefault("model") ?? string.Empty;
        var reasoning = arguments.GetValueOrDefault("reasoning") ?? "low";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var token = cancellation.Token;
        using var http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:8180/"),
            Timeout = TimeSpan.FromMinutes(20),
        };

        try
        {
            var health = await GetJsonAsync(http, "health", token);
            checks.Add(new("health", health.GetProperty("owner").GetString() == "twitchai", health));
            var catalog = await GetJsonAsync(http, "v1/models", token);
            var models = catalog.GetProperty("data").EnumerateArray().ToArray();
            if (string.IsNullOrWhiteSpace(model))
                model = models.Select(item => item.GetProperty("id").GetString()!)
                    .FirstOrDefault(id => id.Contains("Qwen3.8-27B", StringComparison.Ordinal))
                    ?? models.First().GetProperty("id").GetString()!;
            var selected = models.Single(item => item.GetProperty("id").GetString() == model);
            var levels = selected.GetProperty("reasoning_levels").EnumerateArray()
                .Select(level => level.GetString()!).ToArray();
            if (!levels.Contains(reasoning, StringComparer.Ordinal))
                reasoning = levels.Contains("none", StringComparer.Ordinal) ? "none" : "auto";
            checks.Add(new("model_catalog", true, new { model, reasoningLevels = levels, count = models.Length }));

            var options = new InfrastructureOptions
            {
                LocalAi = new LocalAiOptions
                {
                    BaseAddress = http.BaseAddress,
                    Model = model,
                    ReasoningEffort = reasoning,
                    MaximumOutputTokens = int.Parse(arguments.GetValueOrDefault("max-tokens") ?? "8192",
                        System.Globalization.CultureInfo.InvariantCulture),
                    RequestTimeout = http.Timeout,
                },
            };
            using var runtime = new LocalAiRuntime(_ => Task.CompletedTask);
            var ai = new LocalAiClient(http, metrics, options, runtime);
            await ai.EnsureModelLoadedAsync(token);
            checks.Add(new("model_load_application_client", true, new { model, reasoning }));

            foreach (var (name, payload) in new[]
            {
                ("load_model_chunked", new { model, reasoning_effort = reasoning }),
                ("load_model_idempotent", new { model, reasoning_effort = reasoning }),
            })
            {
                using var response = await http.PostAsJsonAsync("v1/models/load", payload, token);
                var body = await response.Content.ReadAsStringAsync(token);
                using var loadDocument = JsonDocument.Parse(body);
                var loadResult = loadDocument.RootElement;
                var confirmed = response.IsSuccessStatusCode
                    && loadResult.GetProperty("model").GetString() == model
                    && loadResult.GetProperty("status").GetString() == "loaded"
                    && loadResult.GetProperty("reused").GetBoolean();
                checks.Add(new(name, confirmed, new { status = (int)response.StatusCode, body }));
                response.EnsureSuccessStatusCode();
            }
            catalog = await GetJsonAsync(http, "v1/models", token);
            var loaded = catalog.GetProperty("data").EnumerateArray()
                .Single(item => item.GetProperty("id").GetString() == model);
            var nativeStatus = loaded.GetProperty("status").GetProperty("value").GetString();
            checks.Add(new("native_model_loaded", nativeStatus is "loaded" or "sleeping", new { model, nativeStatus }));

            await NegativeAsync(http, checks, "unknown_model", "v1/models/load",
                JsonContent.Create(new { model = "coding/__unknown_gateway_smoke__" }), token);
            await NegativeAsync(http, checks, "unsupported_reasoning", "v1/models/load",
                JsonContent.Create(new { model, reasoning_effort = "__unsupported__" }), token);
            await NegativeAsync(http, checks, "malformed_json", "v1/chat/completions",
                new StringContent("{broken", Encoding.UTF8, "application/json"), token);

            var compiler = new ModerationPromptCompiler();
            var validator = new ModerationEnvelopeValidator();
            var client = new CoreLocalModerationClient(ai, options, compiler);
            var samples = Samples.Where(sample => !arguments.TryGetValue("case", out var filter)
                || sample.Name == filter).ToArray();
            if (samples.Length == 0) throw new ArgumentException("No matching --case.");
            var envelope = CreateEnvelope(samples);
            var classificationRequest = new ModerationClassificationRequest(envelope, []);
            var classificationPrompt = compiler.CompileClassification(classificationRequest);
            var classificationRaw = await client.ModerateAsync(classificationPrompt, token);
            var classification = validator.ValidateClassification(classificationRaw, classificationRequest);
            stages.Add(new("classification_application_client", classificationRaw, classification.IsValid,
                classification.Issues, classificationPrompt.ResponseContractVersion));

            var candidates = classification.ValidDecisions
                .Where(decision => decision.Action != ModerationActionKind.None && decision.RuleCode is not null)
                .Select(decision => new ModerationConfirmationCandidate(
                    decision.TemporaryMessageId,
                    decision.Action,
                    decision.TimeoutSeconds,
                    envelope.ActiveRules.Single(rule => rule.RuleCode == decision.RuleCode).PolicySet,
                    decision.RuleCode!,
                    decision.EvidenceKind,
                    decision.SensitiveDataType,
                    [],
                    decision.Confidence,
                    ModerationCandidateSource.SemanticClassification))
                .ToArray();
            ModerationConfirmationReport? confirmation = null;
            if (candidates.Length > 0)
            {
                var request = new ModerationConfirmationRequest(envelope, candidates);
                var prompt = compiler.CompileConfirmation(request);
                var raw = await client.ModerateAsync(prompt, token);
                confirmation = validator.ValidateConfirmation(raw, request);
                stages.Add(new("confirmation_application_client", raw,
                    confirmation.IsEnvelopeValid && confirmation.Issues.Count == 0,
                    confirmation.Issues, prompt.ResponseContractVersion));
            }
            foreach (var sample in samples)
            {
                var classified = classification.ValidDecisions.FirstOrDefault(decision => decision.TemporaryMessageId == sample.Id);
                var confirmed = confirmation?.ConfirmedDecisions.FirstOrDefault(decision => decision.TemporaryMessageId == sample.Id);
                var actualRule = confirmed?.RuleCode;
                var passed = sample.ExpectedRule is null
                    ? classified?.Action == ModerationActionKind.None && confirmed is null
                    : classified?.RuleCode == sample.ExpectedRule && actualRule == sample.ExpectedRule;
                caseResults.Add(new(sample.Name, sample.Id, sample.Text, sample.ExpectedRule, actualRule,
                    classified, confirmed, passed));
            }

            // Exercise both HTTP/1.1 body encodings with a real classification schema.
            // Production JsonContent is now buffered; the explicit chunked request
            // below reproduces the transport that previously lost its JSON body.
            foreach (var chunked in new[] { true, false })
            {
                var framing = chunked ? "chunked" : "content_length";
                var transportEnvelope = CreateEnvelope([Samples[0]]) with { BatchId = Guid.NewGuid() };
                var transportRequest = new ModerationClassificationRequest(transportEnvelope, []);
                var transportPrompt = compiler.CompileClassification(transportRequest);
                var payload = new
                {
                    model,
                    temperature = 0,
                    reasoning_effort = reasoning,
                    max_tokens = options.LocalAi.MaximumOutputTokens,
                    stream = false,
                    messages = new object[]
                    {
                        new { role = "system", content = transportPrompt.SystemPrompt },
                        new { role = "user", content = transportPrompt.UserPrompt },
                    },
                    response_format = transportPrompt.ResponseFormat,
                };
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                using var transportContent = chunked
                    ? (HttpContent)JsonContent.Create(payload)
                    : new ByteArrayContent(payloadBytes);
                transportContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
                {
                    Version = System.Net.HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = transportContent,
                };
                if (chunked) httpRequest.Headers.TransferEncodingChunked = true;
                using var transportResponse = await http.SendAsync(httpRequest, token);
                var transportBody = await transportResponse.Content.ReadAsStringAsync(token);
                transportResponse.EnsureSuccessStatusCode();
                using var responseDocument = JsonDocument.Parse(transportBody);
                var transportRaw = responseDocument.RootElement.GetProperty("choices")[0]
                    .GetProperty("message").GetProperty("content").GetString()!;
                var validated = validator.ValidateClassification(transportRaw, transportRequest);
                stages.Add(new("classification_" + framing, transportRaw,
                    validated.IsValid && validated.ValidDecisions.Count == 1
                        && validated.ValidDecisions.Single().Action == ModerationActionKind.None,
                    validated.Issues, transportPrompt.ResponseContractVersion));
                checks.Add(new(framing + "_transport", transportResponse.IsSuccessStatusCode,
                    new { bytes = payloadBytes.Length, usage = responseDocument.RootElement.GetProperty("usage").Clone() }));
            }
        }
        catch (Exception exception)
        {
            fatalError = exception.ToString();
        }

        stopwatch.Stop();
        var passedAll = fatalError is null && checks.All(check => check.Passed)
            && stages.Count >= 2 && stages.All(stage => stage.Passed)
            && caseResults.Count > 0 && caseResults.All(result => result.Passed);
        var report = new
        {
            passed = passedAll,
            startedAt,
            finishedAt = DateTimeOffset.UtcNow,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            gateway = http.BaseAddress,
            model,
            reasoning,
            twitchRequestsSent = 0,
            databaseWrites = 0,
            checks,
            stages,
            cases = caseResults,
            aiRunMetrics = metrics.Records,
            fatalError,
        };
        var json = JsonSerializer.Serialize(report, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, json);
        Console.WriteLine(json);
        return passedAll ? 0 : 1;
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Use --model ID --output FILE [--reasoning low] [--max-tokens 8192] [--case NAME].");
            values.Add(args[index][2..], args[index + 1]);
        }
        return values;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient http, string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token);
        var body = await response.Content.ReadAsStringAsync(token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task NegativeAsync(HttpClient http, List<CheckResult> checks,
        string name, string path, HttpContent content, CancellationToken token)
    {
        using (content)
        using (var response = await http.PostAsync(path, content, token))
        {
            var body = await response.Content.ReadAsStringAsync(token);
            var jsonError = false;
            try { using var document = JsonDocument.Parse(body); jsonError = document.RootElement.TryGetProperty("error", out _); }
            catch (JsonException) { }
            checks.Add(new(name, (int)response.StatusCode == 400 && jsonError,
                new { status = (int)response.StatusCode, body }));
        }
    }

    private static ModerationRequestEnvelopeV3 CreateEnvelope(IReadOnlyList<Sample> samples) =>
        new(
            ModerationPromptCompiler.RequestEnvelopeVersion,
            Guid.NewGuid(),
            "32982",
            "Grand Theft Auto V",
            [new(PolicySetCode.General, 1), new(PolicySetCode.Backseatgaming, 1)],
            [
                new(ModerationRuleCodes.PersonalAttack, PolicySetCode.General, "Persönlicher Angriff",
                    ModerationRuleCodes.PersonalAttackPromptInstruction, ModerationActionKind.Timeout, 600,
                    "Persönlicher Angriff.", Priority: 10),
                new(ModerationRuleCodes.Spam, PolicySetCode.General, "Spam",
                    ModerationRuleCodes.SpamPromptInstruction, ModerationActionKind.Delete, null,
                    "Chat-Flooding.", Priority: 20),
                new(ModerationRuleCodes.Backseatgaming, PolicySetCode.Backseatgaming, "Backseatgaming",
                    ModerationRuleCodes.BackseatgamingPromptInstruction, ModerationActionKind.Timeout, 600,
                    "Unaufgefordertes Backseatgaming.", Priority: 30),
            ],
            samples.Select(sample => new ModerationRequestMessageV3(sample.Id, sample.Text, [])).ToArray(),
            [],
            []);

    private static readonly Sample[] Samples =
    [
        new("harmless", "m1", "guten abend zusammen, ich wünsche euch einen schönen stream.", null),
        new("direct_insult", "m2", "@testperson du bist ein widerlicher idiot und ein dummes arschloch.", ModerationRuleCodes.PersonalAttack),
        new("backseat", "m3", "geh jetzt durch die linke tür und nimm dort den goldenen schlüssel mit.", ModerationRuleCodes.Backseatgaming),
        new("repeated_spam", "m4", "blauer kuchen blauer kuchen blauer kuchen blauer kuchen blauer kuchen blauer kuchen", ModerationRuleCodes.Spam),
        new("harmless_question", "m5", "hat jemand einen schönen tag gehabt?", null),
    ];

    private sealed class MemoryMetricsStore : ILlmRunMetricsStore
    {
        public List<LlmRunMetric> Records { get; } = [];
        public Task RecordAsync(LlmRunMetric metric, CancellationToken cancellationToken = default)
        {
            Records.Add(metric);
            return Task.CompletedTask;
        }
    }

    private sealed record Sample(string Name, string Id, string Text, string? ExpectedRule);
    private sealed record CheckResult(string Name, bool Passed, object Details);
    private sealed record StageResult(string Name, string RawResponse, bool Passed,
        IReadOnlyList<ModerationValidationIssue> ValidationIssues, string ContractVersion);
    private sealed record CaseResult(string Name, string Id, string Text, string? ExpectedRule,
        string? ActualRule, ModerationClassificationDecision? Classification,
        ValidatedModerationDecision? Confirmation, bool Passed);
}
