using TwitchAI.App.ViewModels;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class LearningDatasetItemTests
{
    [Fact]
    public void MissingOption_StillShowsStableRuleCodeFallback()
    {
        var item = CreateItem(
            LearningDecision.Moderate,
            PolicySetCode.General,
            ModerationRuleCodes.Spam,
            ModerationActionKind.Timeout,
            120,
            [Rule("G_INSULT", PolicySetCode.General, "Beleidigung")]);

        Assert.Equal("Spam", item.CategoryTitle);
        Assert.Contains(
            item.RuleOptions,
            option => option.Code == ModerationRuleCodes.Spam);
    }

    [Fact]
    public void StableRuleCodeSelection_UpdatesPolicyWithoutObjectIdentity()
    {
        var item = CreateItem(
            LearningDecision.Allow,
            PolicySetCode.General,
            ModerationRuleCodes.Spam,
            ModerationActionKind.None,
            null,
            [
                Rule(ModerationRuleCodes.Spam, PolicySetCode.General, "Spam"),
                Rule(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming,
                    "Backseatgaming"),
            ]);

        item.RuleCode = ModerationRuleCodes.Backseatgaming;

        var draft = item.CreateDraft();
        Assert.Equal(PolicySetCode.Backseatgaming, draft.PolicySet);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, draft.RuleCode);
        Assert.Equal(ModerationActionKind.None, draft.Action);
        Assert.Null(draft.TimeoutSeconds);
    }

    [Fact]
    public void AllowedLearning_AlwaysCreatesNonModeratingDraft()
    {
        var item = CreateItem(
            LearningDecision.Allow,
            PolicySetCode.General,
            ModerationRuleCodes.Spam,
            ModerationActionKind.None,
            null,
            [Rule(ModerationRuleCodes.Spam, PolicySetCode.General, "Spam")]);

        item.ActionCode = "timeout";
        item.TimeoutSeconds = 900;

        var draft = item.CreateDraft();
        Assert.Equal(ModerationActionKind.None, draft.Action);
        Assert.Null(draft.TimeoutSeconds);
    }

    [Fact]
    public void Restore_RevertsRuleCodeAndItsPolicyTogether()
    {
        var item = CreateItem(
            LearningDecision.Moderate,
            PolicySetCode.General,
            ModerationRuleCodes.Spam,
            ModerationActionKind.Timeout,
            120,
            [
                Rule(ModerationRuleCodes.Spam, PolicySetCode.General, "Spam"),
                Rule(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming,
                    "Backseatgaming"),
            ]);

        item.RuleCode = ModerationRuleCodes.Backseatgaming;
        Assert.Equal(PolicySetCode.Backseatgaming, item.PolicySet);

        item.Restore();

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal(PolicySetCode.General, item.PolicySet);
    }

    private static LearningDatasetItem CreateItem(
        LearningDecision decision,
        PolicySetCode policySet,
        string ruleCode,
        ModerationActionKind action,
        int? timeoutSeconds,
        IReadOnlyList<LearningRuleOption> rules) =>
        new(
            new LearningDatasetRow(
                new LearningSourceKey(LearningSourceKind.ManualTest, 7),
                "L-7",
                "Beispiel",
                "beispiel",
                decision,
                policySet,
                ruleCode,
                action,
                timeoutSeconds,
                "Begründung.",
                "Lokaler Lerntest",
                new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero),
                0),
            rules,
            [
                new LearningActionOption("delete", "Nachricht löschen"),
                new LearningActionOption("timeout", "Timeout"),
                new LearningActionOption("ban", "Ban"),
            ]);

    private static LearningRuleOption Rule(
        string code,
        PolicySetCode policySet,
        string title) =>
        new(code, policySet, title);
}
