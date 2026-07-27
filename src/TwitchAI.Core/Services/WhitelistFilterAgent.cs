using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class WhitelistFilterAgent(IWhitelistPhraseSource phraseSource)
    : IWhitelistFilterAgent
{
    public async ValueTask<bool> ShouldSkipAsync(
        ChatMessageSnapshot message,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var entries = await phraseSource
            .GetActiveEntriesAsync(
                message.Identity.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        var lowerLogin = message.Identity.Login.ToLowerInvariant();

        if (entries.LowerUserLogins.Contains(lowerLogin))
        {
            await phraseSource
                .RecordHitAsync(
                    message.Identity.ChannelId,
                    WhitelistEntryKind.UserLogin,
                    lowerLogin,
                    observedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        if (!entries.ExactLowerMessages.Contains(message.Text.LowerText))
        {
            return false;
        }

        await phraseSource
            .RecordHitAsync(
                message.Identity.ChannelId,
                WhitelistEntryKind.ExactMessage,
                message.Text.LowerText,
                observedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}
