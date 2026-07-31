using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class EffectivePolicyResolverTests
{
    private readonly EffectivePolicyResolver _sut = new();

    [Theory]
    [InlineData("509658", "Just Chatting")]
    [InlineData("509672", "IRL")]
    public void Resolve_NonGameplayCategory_ActivatesGeneralOnly(
        string categoryId,
        string categoryName)
    {
        var result = _sut.Resolve(
            Context(categoryId, categoryName, isVerified: true),
            generalRevision: 3,
            backseatgamingRevision: 7);

        var general = Assert.Single(result.ActivePolicySets);
        Assert.Equal(PolicySetCode.General, general.Code);
        Assert.Equal(3, general.Revision);
        Assert.False(result.Contains(PolicySetCode.Backseatgaming));
    }

    [Fact]
    public void Resolve_UsesCategoryIdInsteadOfDisplayNameForIrlExclusion()
    {
        var irlIdWithDifferentName = _sut.Resolve(
            Context("509672", "Veralteter Anzeigename", isVerified: true),
            generalRevision: 3,
            backseatgamingRevision: 7);
        var differentIdNamedIrl = _sut.Resolve(
            Context("12345", "IRL", isVerified: true),
            generalRevision: 3,
            backseatgamingRevision: 7);

        Assert.False(irlIdWithDifferentName.Contains(PolicySetCode.Backseatgaming));
        Assert.True(differentIdNamedIrl.Contains(PolicySetCode.Backseatgaming));
    }

    [Theory]
    [InlineData("32982", "Grand Theft Auto V")]
    [InlineData("12345", "Another known game")]
    public void Resolve_KnownGame_ActivatesGeneralAndBackseatgaming(
        string categoryId,
        string categoryName)
    {
        var result = _sut.Resolve(
            Context(categoryId, categoryName, isVerified: true),
            generalRevision: 3,
            backseatgamingRevision: 7);

        Assert.Equal(
            [PolicySetCode.General, PolicySetCode.Backseatgaming],
            result.ActivePolicySets.Select(item => item.Code));
        Assert.Equal(7, result.ActivePolicySets[1].Revision);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("32982", false)]
    public void Resolve_UnknownOrUnverifiedCategory_FallsBackToGeneral(
        string? categoryId,
        bool isVerified)
    {
        var result = _sut.Resolve(
            Context(categoryId, "Unbekannt", isVerified),
            generalRevision: 1,
            backseatgamingRevision: 1);

        Assert.True(result.Contains(PolicySetCode.General));
        Assert.False(result.Contains(PolicySetCode.Backseatgaming));
    }

    [Fact]
    public void Resolve_FingerprintChangesWhenRuleRevisionChanges()
    {
        var context = Context("32982", "Grand Theft Auto V", isVerified: true);

        var first = _sut.Resolve(context, 1, 1);
        var second = _sut.Resolve(context, 1, 2);

        Assert.NotEqual(first.RevisionFingerprint, second.RevisionFingerprint);
    }

    private static ChannelContextSnapshot Context(
        string? categoryId,
        string categoryName,
        bool isVerified)
    {
        return new ChannelContextSnapshot(
            "channel-1",
            categoryId,
            categoryName,
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: isVerified);
    }
}
