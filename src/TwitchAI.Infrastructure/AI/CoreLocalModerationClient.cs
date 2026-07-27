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

    public CoreLocalModerationClient(ILmStudioClient client)
    {
        _client = client;
    }

    public async ValueTask<string> ModerateAsync(
        ModerationRequestEnvelopeV3 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        var policyFingerprint = string.Join(
            "|",
            request.ActivePolicySets.Select(item => $"{item.Code}:{item.Revision}"));
        var completion = await _client.CompleteModerationAsync(
            new LmStudioModerationRequest(
                request.BatchId.ToString("D"),
                policyFingerprint,
                request.Messages.Count,
                ModerationRequestFactory.CreateSystemPrompt(request),
                json,
                request.ActiveRules
                    .Select(item => item.RuleCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()),
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
