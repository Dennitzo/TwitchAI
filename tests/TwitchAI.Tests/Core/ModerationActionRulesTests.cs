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
    public void ActionsWithoutDuration_OnlyAcceptNullTimeout()
    {
        Assert.True(ModerationActionRules.IsValid(ModerationActionKind.Delete, null));
        Assert.True(ModerationActionRules.IsValid(ModerationActionKind.None, null));
        Assert.True(ModerationActionRules.IsValid(ModerationActionKind.Ban, null));
        Assert.False(ModerationActionRules.IsValid(ModerationActionKind.Delete, 1));
        Assert.False(ModerationActionRules.IsValid(ModerationActionKind.None, 1));
        Assert.False(ModerationActionRules.IsValid(ModerationActionKind.Ban, 1));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(120, true)]
    [InlineData(600, true)]
    [InlineData(86_400, true)]
    [InlineData(1, false)]
    [InlineData(300, false)]
    [InlineData(7_200, false)]
    [InlineData(1_209_600, false)]
    public void AiTimeoutSuggestion_OnlyAcceptsDefinedModerationTiers(
        int seconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            ModerationActionRules.IsValidAiSuggestion(
                ModerationActionKind.Timeout,
                seconds));
    }

    [Theory]
    [InlineData(
        ModerationRuleCodes.OtherAbuse,
        ModerationActionKind.Delete,
        null)]
    [InlineData(
        ModerationRuleCodes.Spam,
        ModerationActionKind.Timeout,
        ModerationActionRules.LightTimeoutSeconds)]
    [InlineData(
        ModerationRuleCodes.Advertising,
        ModerationActionKind.Ban,
        null)]
    [InlineData(
        ModerationRuleCodes.Begging,
        ModerationActionKind.Timeout,
        ModerationActionRules.SevereTimeoutSeconds)]
    [InlineData(
        ModerationRuleCodes.Disruption,
        ModerationActionKind.Timeout,
        ModerationActionRules.PurgeTimeoutSeconds)]
    [InlineData(
        ModerationRuleCodes.Backseatgaming,
        ModerationActionKind.Timeout,
        ModerationActionRules.DefaultTimeoutSeconds)]
    [InlineData(
        "G_INSULT",
        ModerationActionKind.Timeout,
        ModerationActionRules.DefaultTimeoutSeconds)]
    public void AiDeleteSuggestion_IsRestrictedToLowContextOtherAbuse(
        string ruleCode,
        ModerationActionKind expectedAction,
        int? expectedTimeoutSeconds)
    {
        var normalized = ModerationActionRules.NormalizeAiSuggestionForRule(
            ModerationActionKind.Delete,
            timeoutSeconds: null,
            ruleCode);

        Assert.Equal(expectedAction, normalized.Action);
        Assert.Equal(expectedTimeoutSeconds, normalized.TimeoutSeconds);
    }

    [Theory]
    [InlineData(ModerationActionKind.Delete, null)]
    [InlineData(
        ModerationActionKind.Timeout,
        ModerationActionRules.LightTimeoutSeconds)]
    [InlineData(ModerationActionKind.Ban, null)]
    public void Advertising_AnyActionableAiSuggestionBecomesBan(
        ModerationActionKind action,
        int? timeoutSeconds)
    {
        Assert.Equal(
            (ModerationActionKind.Ban, (int?)null),
            ModerationActionRules.NormalizeAiSuggestionForRule(
                action,
                timeoutSeconds,
                ModerationRuleCodes.Advertising));
    }

    [Theory]
    [InlineData(ModerationActionKind.Delete, null)]
    [InlineData(
        ModerationActionKind.Timeout,
        ModerationActionRules.DefaultTimeoutSeconds)]
    [InlineData(ModerationActionKind.Ban, null)]
    public void Begging_AnyActionableAiSuggestionBecomesDayTimeout(
        ModerationActionKind action,
        int? timeoutSeconds)
    {
        Assert.Equal(
            (
                ModerationActionKind.Timeout,
                (int?)ModerationActionRules.SevereTimeoutSeconds),
            ModerationActionRules.NormalizeAiSuggestionForRule(
                action,
                timeoutSeconds,
                ModerationRuleCodes.Begging));
    }

    [Theory]
    [InlineData(ModerationRuleCodes.Advertising)]
    [InlineData(ModerationRuleCodes.Begging)]
    public void NoneSuggestion_RemainsNoneForStrictRuleMappings(
        string ruleCode)
    {
        Assert.Equal(
            (ModerationActionKind.None, (int?)null),
            ModerationActionRules.NormalizeAiSuggestionForRule(
                ModerationActionKind.None,
                timeoutSeconds: null,
                ruleCode));
    }

    [Fact]
    public void AiTimeoutAndBanSuggestions_AreNotRewritten()
    {
        Assert.Equal(
            (ModerationActionKind.Timeout, (int?)86_400),
            ModerationActionRules.NormalizeAiSuggestionForRule(
                ModerationActionKind.Timeout,
                86_400,
                "G_INSULT"));
        Assert.Equal(
            (ModerationActionKind.Ban, (int?)null),
            ModerationActionRules.NormalizeAiSuggestionForRule(
                ModerationActionKind.Ban,
                timeoutSeconds: null,
                "G_INSULT"));
    }

    [Theory]
    [InlineData(ModerationRuleCodes.OtherAbuse, false)]
    [InlineData(ModerationRuleCodes.Advertising, true)]
    [InlineData(ModerationRuleCodes.Begging, false)]
    [InlineData(ModerationRuleCodes.Backseatgaming, false)]
    [InlineData("G_SPAM", false)]
    [InlineData("G_DISRUPTION", false)]
    [InlineData("G_INSULT", true)]
    [InlineData("G_HARASSMENT", true)]
    [InlineData("G_THREAT", true)]
    public void AiBanSuggestion_RequiresBanWorthyRuleGroup(
        string ruleCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            ModerationActionRules.IsValidAiRuleAction(
                ModerationActionKind.Ban,
                ruleCode));
    }

    [Fact]
    public void RuleActionCheckLeavesNonBanValidationToTheActionNormalizer()
    {
        Assert.True(
            ModerationActionRules.IsValidAiRuleAction(
                ModerationActionKind.Timeout,
                ModerationRuleCodes.Advertising));
        Assert.True(
            ModerationActionRules.IsValidAiRuleAction(
                ModerationActionKind.Delete,
                ModerationRuleCodes.Backseatgaming));
    }

    [Fact]
    public void PhraseGatedAdvertisingUsesStrictContextualActionMatrix()
    {
        Assert.True(ModerationActionRules.IsValidAiRuleSuggestion(
            ModerationActionKind.Timeout,
            86_400,
            ModerationRuleCodes.SelfPromotion));
        Assert.True(ModerationActionRules.IsValidAiRuleSuggestion(
            ModerationActionKind.Ban,
            null,
            ModerationRuleCodes.SelfPromotion));
        Assert.False(ModerationActionRules.IsValidAiRuleSuggestion(
            ModerationActionKind.Timeout,
            600,
            ModerationRuleCodes.SelfPromotion));
        Assert.True(ModerationActionRules.IsValidAiRuleSuggestion(
            ModerationActionKind.Ban,
            null,
            ModerationRuleCodes.BotScam));
        Assert.False(ModerationActionRules.IsValidAiRuleSuggestion(
            ModerationActionKind.Timeout,
            86_400,
            ModerationRuleCodes.BotScam));
    }
}
