namespace TwitchAI.Infrastructure.Twitch;

public static class TwitchChannelSelection
{
    public const int MaximumModerationChannels = 60;

    public const int MaximumReadOnlyChannels = 150;

    public static IReadOnlyList<string> ParseLogins(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(login => login.Trim().TrimStart('#', '@').ToLowerInvariant())
            .Where(login => login.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string FormatLogins(IEnumerable<string> logins)
    {
        ArgumentNullException.ThrowIfNull(logins);
        return string.Join(
            ", ",
            logins
                .Select(login => login.Trim().TrimStart('#', '@').ToLowerInvariant())
                .Where(login => login.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> ParseBroadcasterIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static int GetMaximumChannelCount(bool isReadOnly) =>
        isReadOnly ? MaximumReadOnlyChannels : MaximumModerationChannels;

    public static void EnsureWithinEventSubLimit(int channelCount, bool isReadOnly)
    {
        var maximum = GetMaximumChannelCount(isReadOnly);
        if (channelCount > maximum)
        {
            throw new InvalidOperationException(
                $"At most {maximum} Twitch channels can be used in "
                + (isReadOnly ? "read-only mode." : "moderation mode."));
        }
    }
}
