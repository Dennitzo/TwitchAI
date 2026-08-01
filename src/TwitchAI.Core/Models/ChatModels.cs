using System.Collections.ObjectModel;
using TwitchAI.Core.Services;

namespace TwitchAI.Core.Models;

public sealed record ProcessedChatText(
    string OriginalText,
    string LowerText,
    string RuleNormalizedText,
    int NormalizationVersion,
    string OriginalSha256,
    string RuleNormalizedSha256);

public sealed record ChatMessageIdentityV1(
    string EventSubNotificationId,
    string TwitchMessageId,
    string ChannelId,
    string TwitchUserId,
    string Login,
    string DisplayName,
    string OriginalText,
    DateTimeOffset ReceivedAtUtc,
    string? SharedChatSourceChannelId,
    IReadOnlyDictionary<string, string> Badges,
    string IdentityFingerprint)
{
    public static ChatMessageIdentityV1 Create(
        string eventSubNotificationId,
        string twitchMessageId,
        string channelId,
        string twitchUserId,
        string login,
        string displayName,
        string originalText,
        DateTimeOffset receivedAtUtc,
        string? sharedChatSourceChannelId = null,
        IReadOnlyDictionary<string, string>? badges = null)
    {
        var badgeCopy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var badge in badges
                     ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            badgeCopy[badge.Key] = badge.Value;
        }

        var immutableBadges =
            new ReadOnlyDictionary<string, string>(badgeCopy);

        var candidate = new ChatMessageIdentityV1(
            Require(eventSubNotificationId, nameof(eventSubNotificationId)),
            Require(twitchMessageId, nameof(twitchMessageId)),
            Require(channelId, nameof(channelId)),
            Require(twitchUserId, nameof(twitchUserId)),
            Require(login, nameof(login)),
            Require(displayName, nameof(displayName)),
            originalText ?? throw new ArgumentNullException(nameof(originalText)),
            receivedAtUtc.ToUniversalTime(),
            sharedChatSourceChannelId,
            immutableBadges,
            string.Empty);

        return candidate with
        {
            IdentityFingerprint = ChatMessageIdentityFingerprint.Compute(candidate),
        };
    }

    private static string Require(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A Twitch identity value cannot be empty.", parameterName)
            : value;
    }
}

public sealed record ChannelContextSnapshot(
    string ChannelId,
    string? CategoryId,
    string CategoryDisplayName,
    DateTimeOffset ObservedAtUtc,
    long Revision,
    bool IsVerified)
{
    public bool HasKnownCategory => IsVerified && !string.IsNullOrWhiteSpace(CategoryId);
}

public sealed record ChatMessageSnapshot(
    ChatMessageIdentityV1 Identity,
    ProcessedChatText Text,
    ChannelContextSnapshot ChannelContext,
    long IngressSequence)
{
    public int TwitchEmoteCount { get; init; }

    public bool IsTwitchEmoteOnly { get; init; }
}
