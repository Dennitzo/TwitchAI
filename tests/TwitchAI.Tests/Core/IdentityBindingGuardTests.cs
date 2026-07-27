using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class IdentityBindingGuardTests
{
    [Fact]
    public void Validate_SameImmutableIdentity_IsValid()
    {
        var persisted = Identity("message-1", "user-1", "Viewer");

        var result = IdentityBindingGuard.Validate(persisted, persisted);

        Assert.True(result.IsValid);
        Assert.Equal("identity_valid", result.Code);
    }

    [Fact]
    public void Validate_ReboundRowWithInternallyValidOtherUser_IsBlocked()
    {
        var persisted = Identity("message-1", "user-1", "Viewer");
        var selected = Identity("message-1", "user-2", "Other Viewer");

        var result = IdentityBindingGuard.Validate(persisted, selected);

        Assert.False(result.IsValid);
        Assert.Equal("fingerprint_mismatch", result.Code);
    }

    [Fact]
    public void Validate_FieldManipulationWithoutFingerprintUpdate_IsBlocked()
    {
        var persisted = Identity("message-1", "user-1", "Viewer");
        var selected = persisted with
        {
            DisplayName = "Manipulated",
        };

        var result = IdentityBindingGuard.Validate(persisted, selected);

        Assert.False(result.IsValid);
        Assert.Equal("fingerprint_invalid", result.Code);
    }

    private static ChatMessageIdentityV1 Identity(
        string messageId,
        string userId,
        string displayName)
    {
        return ChatMessageIdentityV1.Create(
            $"event-{userId}",
            messageId,
            "channel-1",
            userId,
            userId,
            displayName,
            "same text",
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero));
    }
}
