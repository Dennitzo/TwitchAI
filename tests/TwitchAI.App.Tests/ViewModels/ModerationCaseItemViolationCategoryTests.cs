using TwitchAI.App.ViewModels;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class ModerationCaseItemViolationCategoryTests
{
    [Fact]
    public void RuleCodeBeforeSnapshot_ProvidesProvisionalDisplayAndRemainsReviewable()
    {
        var item = CreateCase();

        item.RuleCode = ModerationRuleCodes.Spam;

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
        Assert.Equal(
            ModerationRuleCodes.Spam,
            Assert.Single(item.ViolationRuleOptions).Code);
        Assert.False(item.HasViolationRuleSnapshot);
        Assert.True(item.CanExecute);
    }

    [Fact]
    public void ReplacingOptionSnapshot_RetainsCanonicalRuleCodeAcrossNewInstances()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [InsultOption(), SpamOption()],
            ModerationRuleCodes.Spam);

        var firstSnapshot = item.ViolationRuleOptions;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), InsultOption()],
            selectedRuleCode: null);

        Assert.NotSame(firstSnapshot, item.ViolationRuleOptions);
        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
        Assert.True(item.HasViolationRuleSnapshot);
        Assert.True(item.CanExecute);

        // A recycled ComboBox may transiently push an empty SelectedValue.
        item.RuleCode = null;
        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
    }

    [Fact]
    public void SnapshotWithoutPersistedRule_KeepsVisibleUnavailableFallback()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;

        item.ReplaceViolationRuleOptions(
            [InsultOption()],
            selectedRuleCode: null);

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.StartsWith("Spam", item.ViolationCategoryDisplayName, StringComparison.Ordinal);
        Assert.Contains(
            "nicht mehr aktiv",
            item.ViolationCategoryDisplayName,
            StringComparison.Ordinal);
        Assert.Equal(
            ModerationRuleCodes.Spam,
            item.ViolationRuleOptions[0].Code);
        Assert.True(item.HasViolationRuleSnapshot);
    }

    [Fact]
    public void SelectingByCode_AppliesSelectedRuleDefaults()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), BeggingOption()],
            ModerationRuleCodes.Spam);

        var selected = item.TrySelectViolationRule(ModerationRuleCodes.Begging);

        Assert.True(selected);
        Assert.Equal(ModerationRuleCodes.Begging, item.RuleCode);
        Assert.Equal("Gebettel", item.ViolationCategoryDisplayName);
        Assert.Equal("General", item.PolicySet);
        Assert.Equal("timeout", item.Action);
        Assert.Equal(86_400, item.TimeoutSeconds);
        Assert.Equal("Betteln nach Subs oder Giveaways.", item.Reason);
    }

    [Fact]
    public void DirectReviewActions_KeepAllowStricterThanDiscard()
    {
        var item = CreateCase();

        Assert.False(item.CanAllow);
        Assert.True(item.CanDiscard);

        item.RuleCode = ModerationRuleCodes.Spam;

        Assert.True(item.CanAllow);
        Assert.True(item.CanDiscard);

        item.IsBusy = true;

        Assert.False(item.CanAllow);
        Assert.False(item.CanDiscard);
        Assert.False(item.CanExecute);
    }

    [Fact]
    public void ChatReply_DoesNotOfferAllowedLearning()
    {
        var item = CreateCase(isChatReply: true);

        Assert.False(item.CanAllow);
        Assert.True(item.CanDiscard);
        Assert.True(item.CanExecute);
    }

    private static ModerationCaseItem CreateCase(bool isChatReply = false) =>
        new()
        {
            CaseId = 1,
            IsChatReply = isChatReply,
            ChannelId = "channel-1",
            UserName = "viewer",
            OriginalMessage = "test",
            CategoryName = "Just Chatting",
            CategoryId = "509658",
            ReceivedAt = new DateTimeOffset(
                2026,
                7,
                31,
                10,
                0,
                0,
                TimeSpan.Zero),
            MessageId = "message-1",
            UserId = "user-1",
            IdentityFingerprint = "fingerprint-1",
            RowVersion = 1,
            Action = "timeout",
            TimeoutSeconds = 600,
            Reason = "Bestehender Grund.",
            PolicySet = "General",
            Status = "Pending",
        };

    private static ViolationCategoryOption SpamOption() =>
        new(
            "General",
            ModerationRuleCodes.Spam,
            "Spam",
            "Wiederholter Spam.",
            "timeout",
            120);

    private static ViolationCategoryOption InsultOption() =>
        new(
            "General",
            "G_INSULT",
            "Beleidigung",
            "Beleidigung eines Nutzers oder Streamers.",
            "timeout",
            600);

    private static ViolationCategoryOption BeggingOption() =>
        new(
            "General",
            ModerationRuleCodes.Begging,
            "Gebettel",
            "Betteln nach Subs oder Giveaways.",
            "timeout",
            86_400);
}
