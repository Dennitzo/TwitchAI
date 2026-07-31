using TwitchAI.App.ViewModels;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class LearningReviewViewModelTests
{
    [Fact]
    public void ModerateSpamResult_KeepsSpamCategoryVisibleAndReviewable()
    {
        var result = Result(
            ModerationRuleCodes.Spam,
            PolicySetCode.General,
            ModerationActionKind.Timeout,
            timeoutSeconds: 120,
            reason: "Wiederholter Spam.");

        var review = new LearningReviewViewModel(result);

        Assert.Equal(ModerationRuleCodes.Spam, review.SelectedRuleCode);
        Assert.Equal("Spam", review.SelectedRuleDisplayName);
        var saved = review.CreateReview();
        Assert.Equal(ModerationRuleCodes.Spam, saved.RuleCode);
        Assert.Equal(PolicySetCode.General, saved.PolicySet);
        Assert.Equal(ModerationActionKind.Timeout, saved.Action);
        Assert.Equal(120, saved.TimeoutSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TransientEmptySelection_DoesNotClearSpamCategory(
        string? transientValue)
    {
        var review = new LearningReviewViewModel(
            Result(
                ModerationRuleCodes.Spam,
                PolicySetCode.General,
                ModerationActionKind.Timeout,
                timeoutSeconds: 120,
                reason: "Wiederholter Spam."));

        var selected = review.TrySelectRule(transientValue);

        Assert.False(selected);
        Assert.Equal(ModerationRuleCodes.Spam, review.SelectedRuleCode);
        Assert.Equal("Spam", review.SelectedRuleDisplayName);
        Assert.Equal(ModerationRuleCodes.Spam, review.CreateReview().RuleCode);
    }

    private static LearningTestResult Result(
        string ruleCode,
        PolicySetCode policySet,
        ModerationActionKind action,
        int? timeoutSeconds,
        string reason)
    {
        var rules = new[]
        {
            Rule(
                ModerationRuleCodes.OtherAbuse,
                PolicySetCode.General,
                "Sonstiger General-Verstoß",
                "Sonstiger Verstoß gegen die Chatregeln.",
                ModerationActionKind.Timeout,
                600),
            Rule(
                ModerationRuleCodes.Spam,
                PolicySetCode.General,
                "Spam",
                "Wiederholter Spam.",
                ModerationActionKind.Timeout,
                120),
            Rule(
                ModerationRuleCodes.Backseatgaming,
                PolicySetCode.Backseatgaming,
                "Backseatgaming",
                "Unaufgeforderter Spielhinweis.",
                ModerationActionKind.Timeout,
                600),
        };
        return new LearningTestResult(
            Guid.Parse("e5e25de8-397b-4201-9861-fc9386549856"),
            "SPAM SPAM",
            "spam spam",
            LearningVerdict.Moderate,
            action,
            timeoutSeconds,
            policySet,
            ruleCode,
            reason,
            ["spam spam"],
            0.95,
            [ModerationRuleCodes.Spam],
            250,
            "policy-1",
            rules,
            "openai/gpt-oss-20b",
            100,
            20,
            120,
            new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero),
            UsedRuleFallback: false);
    }

    private static ModerationRuleDefinition Rule(
        string code,
        PolicySetCode policySet,
        string title,
        string reason,
        ModerationActionKind action,
        int? timeoutSeconds) =>
        new(
            code,
            policySet,
            title,
            $"Erkenne {title}.",
            action,
            timeoutSeconds,
            reason);
}
