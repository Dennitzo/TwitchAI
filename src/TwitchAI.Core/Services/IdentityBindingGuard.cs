using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed record IdentityValidationResult(bool IsValid, string Code, string Message)
{
    public static IdentityValidationResult Valid { get; } =
        new(true, "identity_valid", "The immutable Twitch identity is valid.");
}

public static class IdentityBindingGuard
{
    public static IdentityValidationResult Validate(
        ChatMessageIdentityV1 persisted,
        ChatMessageIdentityV1 selected)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(selected);

        if (!ChatMessageIdentityFingerprint.IsValid(persisted)
            || !ChatMessageIdentityFingerprint.IsValid(selected))
        {
            return new(
                false,
                "fingerprint_invalid",
                "The persisted or selected message fingerprint is invalid.");
        }

        if (!string.Equals(
                persisted.IdentityFingerprint,
                selected.IdentityFingerprint,
                StringComparison.Ordinal))
        {
            return new(
                false,
                "fingerprint_mismatch",
                "The selected row does not refer to the persisted moderation target.");
        }

        if (!string.Equals(persisted.ChannelId, selected.ChannelId, StringComparison.Ordinal)
            || !string.Equals(
                persisted.TwitchMessageId,
                selected.TwitchMessageId,
                StringComparison.Ordinal)
            || !string.Equals(
                persisted.TwitchUserId,
                selected.TwitchUserId,
                StringComparison.Ordinal))
        {
            return new(
                false,
                "twitch_identity_mismatch",
                "Channel, message, or Twitch user ID changed.");
        }

        return IdentityValidationResult.Valid;
    }
}
