using System.Text.Json;
using System.Text.Json.Serialization;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Infrastructure.AI;

public sealed class CoreLocalModerationClient : ILocalModerationClient
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly ILmStudioClient _client;
    private readonly LmStudioOptions _options;
    private readonly IModerationPromptCompiler _promptCompiler;

    public CoreLocalModerationClient(
        ILmStudioClient client,
        InfrastructureOptions? options = null,
        IModerationPromptCompiler? promptCompiler = null)
    {
        _client = client;
        _options = options?.LmStudio ?? new LmStudioOptions();
        _promptCompiler = promptCompiler ?? new ModerationPromptCompiler();
    }

    public async ValueTask<string> ModerateAsync(
        ModerationRequestEnvelopeV3 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_options.UseCompactModerationContractV6)
        {
            return await ModerateAsync(
                    _promptCompiler.Compile(request),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ModerateLegacyAsync(request, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string> ModerateAsync(
        CompiledModerationPrompt prompt,
        CancellationToken cancellationToken = default)
        => await ModerateAsync(prompt, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> ModerateAsync(
        CompiledModerationPrompt prompt,
        string? schedulingKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!_options.UseCompactModerationContractV6)
        {
            return await ModerateLegacyAsync(
                    prompt.Envelope,
                    schedulingKey,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var completion = await _client.CompleteModerationAsync(
            new LmStudioModerationRequest(
                prompt.Envelope.BatchId.ToString("D"),
                prompt.PolicyHash,
                prompt.MessageIds.Count,
                prompt.SystemPrompt,
                prompt.UserPrompt,
                prompt.ActiveRuleCodes,
                prompt.ReplyTemplateIds,
                LmStudioResponseSchemaKind.ModerationV6,
                LocalAiRunKind.Moderation,
                prompt.ResponseFormat,
                SchedulingKey: schedulingKey),
            cancellationToken).ConfigureAwait(false);
        return completion.RawEnvelope;
    }

    private async ValueTask<string> ModerateLegacyAsync(
        ModerationRequestEnvelopeV3 request,
        string? schedulingKey,
        CancellationToken cancellationToken)
    {
        var legacyRequest = request with
        {
            EnvelopeVersion = ModerationRequestFactory.LegacyRequestEnvelopeVersion,
        };
        var json = JsonSerializer.Serialize(legacyRequest, SerializerOptions);
        var policyFingerprint = string.Join(
            "|",
            legacyRequest.ActivePolicySets.Select(item => $"{item.Code}:{item.Revision}"));
        var completion = await _client.CompleteModerationAsync(
            new LmStudioModerationRequest(
                legacyRequest.BatchId.ToString("D"),
                policyFingerprint,
                legacyRequest.Messages.Count,
                ModerationRequestFactory.CreateSystemPrompt(legacyRequest),
                json,
                legacyRequest.ActiveRules
                    .Select(item => item.RuleCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                legacyRequest.ReplyTemplates
                    .Select(item => item.TemplateId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                SchedulingKey: schedulingKey),
            cancellationToken).ConfigureAwait(false);
        return completion.RawEnvelope;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new PolicySetCodeJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
