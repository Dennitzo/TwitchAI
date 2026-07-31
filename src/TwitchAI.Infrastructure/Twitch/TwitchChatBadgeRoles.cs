using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Twitch;

internal static class TwitchChatBadgeRoles
{
    internal const string Broadcaster = "broadcaster";
    internal const string Moderator = "moderator";
    internal const string LeadModerator = "lead_moderator";
    internal const string Vip = "vip";

    // Twitch has no general is_bot user field. This is the current global
    // Chat Bot badge set emitted only for API-authenticated chat bots; bots
    // without that badge remain covered by moderator roles or the manual
    // username whitelist.
    internal const string ChatBot = "bot-badge";

    internal static bool HasAutomaticModerationBypass(
        IEnumerable<string> badgeSetIds)
    {
        ArgumentNullException.ThrowIfNull(badgeSetIds);
        return badgeSetIds.Any(GrantsAutomaticModerationBypass);
    }

    internal static bool HasAutomaticModerationBypass(
        ChatMessageIdentityV1 identity,
        TwitchApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);

        var userId = identity.TwitchUserId;
        return string.Equals(userId, identity.ChannelId, StringComparison.Ordinal)
            || string.Equals(
                userId,
                options.BroadcasterId,
                StringComparison.Ordinal)
            || string.Equals(
                userId,
                options.ModeratorUserId,
                StringComparison.Ordinal)
            || string.Equals(
                userId,
                identity.SharedChatSourceChannelId,
                StringComparison.Ordinal)
            || HasAutomaticModerationBypass(identity.Badges.Keys);
    }

    internal static bool GrantsAutomaticModerationBypass(string badgeSetId)
    {
        return IsBadge(badgeSetId, Broadcaster)
            || IsModeratorRole(badgeSetId)
            || IsBadge(badgeSetId, Vip)
            || IsBadge(badgeSetId, ChatBot);
    }

    internal static bool HasModeratorRole(IEnumerable<string> badgeSetIds)
    {
        ArgumentNullException.ThrowIfNull(badgeSetIds);
        return badgeSetIds.Any(IsModeratorRole);
    }

    internal static bool IsModeratorRole(string badgeSetId)
    {
        return IsBadge(badgeSetId, Moderator)
            || IsBadge(badgeSetId, LeadModerator);
    }

    private static bool IsBadge(string value, string expected)
    {
        return string.Equals(
            value,
            expected,
            StringComparison.OrdinalIgnoreCase);
    }
}
