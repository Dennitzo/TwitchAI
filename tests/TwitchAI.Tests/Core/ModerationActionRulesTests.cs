using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationActionRulesTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(300, true)]
    [InlineData(1_209_600, true)]
    [InlineData(0, false)]
    [InlineData(1_209_601, false)]
    public void Timeout_EnforcesTwitchDurationRange(int seconds, bool expected)
    {
        Assert.Equal(
            expected,
            ModerationActionRules.IsValid(ModerationActionKind.Timeout, seconds));
    }

    [Fact]
    public void DeleteAndNone_DoNotAcceptTimeoutDuration()
    {
        Assert.True(ModerationActionRules.IsValid(ModerationActionKind.Delete, null));
        Assert.True(ModerationActionRules.IsValid(ModerationActionKind.None, null));
        Assert.False(ModerationActionRules.IsValid(ModerationActionKind.Delete, 1));
        Assert.False(ModerationActionRules.IsValid(ModerationActionKind.None, 1));
    }
}
