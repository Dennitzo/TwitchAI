using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class WhitelistFilterAgent(IWhitelistPhraseSource phraseSource)
    : IWhitelistFilterAgent
{
    public async ValueTask<bool> ShouldSkipAsync(
        string channelId,
        ProcessedChatText text,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(text);

        var phrases = await phraseSource
            .GetActiveLowerPhrasesAsync(channelId, cancellationToken)
            .ConfigureAwait(false);

        if (!phrases.Contains(text.LowerText))
        {
            return false;
        }

        await phraseSource
            .RecordHitAsync(channelId, text.LowerText, observedAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
