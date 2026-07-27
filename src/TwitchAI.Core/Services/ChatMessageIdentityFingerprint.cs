using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ChatMessageIdentityFingerprint
{
    public static string Compute(ChatMessageIdentityV1 identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var canonicalBadges = identity.Badges
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new[] { item.Key, item.Value })
            .ToArray();

        var canonical = JsonSerializer.Serialize(
            new IdentityFingerprintPayload(
                identity.EventSubNotificationId,
                identity.TwitchMessageId,
                identity.ChannelId,
                identity.TwitchUserId,
                identity.Login,
                identity.DisplayName,
                identity.OriginalText,
                identity.ReceivedAtUtc.ToUniversalTime().ToString("O"),
                identity.SharedChatSourceChannelId,
                canonicalBadges));

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool IsValid(ChatMessageIdentityV1 identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var expected = Compute(identity);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(identity.IdentityFingerprint);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private sealed record IdentityFingerprintPayload(
        string EventSubNotificationId,
        string TwitchMessageId,
        string ChannelId,
        string TwitchUserId,
        string Login,
        string DisplayName,
        string OriginalText,
        string ReceivedAtUtc,
        string? SharedChatSourceChannelId,
        string[][] Badges);
}
