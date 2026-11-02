using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Infrastructure.AI;

public sealed class CoreLocalModerationClient : ILocalModerationClient
{
    private readonly ILocalAiClient _client;
    private readonly IModerationPromptCompiler _promptCompiler;

    public CoreLocalModerationClient(
        ILocalAiClient client,
        InfrastructureOptions? options = null,
        IModerationPromptCompiler? promptCompiler = null)
    {
        _client = client;
        _promptCompiler = promptCompiler ?? new ModerationPromptCompiler();
    }

    public async ValueTask PrepareAsync(CancellationToken cancellationToken = default) =>
        await _client.EnsureModelLoadedAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<string> ModerateAsync(
        ModerationRequestEnvelopeV3 request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ModerateAsync(_promptCompiler.Compile(request), cancellationToken)
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
        var completion = await _client.CompleteModerationAsync(
            new LocalAiModerationRequest(
                prompt.CorrelationId ?? prompt.Envelope.BatchId.ToString("D"),
                prompt.PolicyHash,
                prompt.MessageIds.Count,
                prompt.SystemPrompt,
                prompt.UserPrompt,
                prompt.ActiveRuleCodes,
                prompt.ReplyTemplateIds,
                LocalAiResponseSchemaKind.ModerationV6,
                LocalAiRunKind.Moderation,
                prompt.ResponseFormat,
                SchedulingKey: schedulingKey),
            cancellationToken).ConfigureAwait(false);
        return completion.RawEnvelope;
    }
}
