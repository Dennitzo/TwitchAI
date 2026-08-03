using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class HumanModerationPriorityTests
{
    private const string Message = "ich liebe dich";

    [Fact]
    public void Resolve_ExactAllowLearningOverridesMatchingBlacklistPhrase()
    {
        var result = HumanModerationPriority.Resolve(
            Message,
            CreatePolicy(),
            [Match()],
            [Example(isPositive: false, ModerationActionKind.None, timeoutSeconds: null)]);

        Assert.NotNull(result);
        Assert.True(result.IsAllow);
        Assert.Null(result.Rule);
        Assert.Equal(ModerationActionKind.None, result.Action);
        Assert.Null(result.TimeoutSeconds);
        Assert.Empty(result.TriggerPhrases);
        Assert.Equal("exact_allow_learning", result.Source);
    }

    [Fact]
    public void Resolve_ExactAllowWinsOverEarlierExactModerateLearning()
    {
        var result = HumanModerationPriority.Resolve(
            Message,
            CreatePolicy(),
            [Match()],
            [
                Example(isPositive: true, ModerationActionKind.Timeout, 600),
                Example(isPositive: false, ModerationActionKind.None, timeoutSeconds: null),
            ]);

        Assert.NotNull(result);
        Assert.True(result.IsAllow);
        Assert.Equal(ModerationActionKind.None, result.Action);
        Assert.Equal("exact_allow_learning", result.Source);
    }

    [Fact]
    public void Resolve_ExactPositiveLearningDoesNotInventEvidenceKind()
    {
        var result = HumanModerationPriority.Resolve(
            Message,
            CreatePolicy(),
            localMatches: [],
            [Example(isPositive: true, ModerationActionKind.Timeout, 600)]);

        Assert.NotNull(result);
        Assert.False(result.IsAllow);
        Assert.Equal(ModerationRuleCodes.PersonalAttack, result.Rule?.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, result.Action);
        Assert.Equal(600, result.TimeoutSeconds);
        Assert.Equal("exact_moderate_learning", result.Source);
        Assert.Null(result.EvidenceKind);
    }

    [Fact]
    public void Resolve_ActiveIchLiebeDichPhraseProducesPersonalAttackTimeout()
    {
        var result = HumanModerationPriority.Resolve(
            Message,
            CreatePolicy(),
            [Match()],
            examples: []);

        Assert.NotNull(result);
        Assert.False(result.IsAllow);
        Assert.Equal(ModerationRuleCodes.PersonalAttack, result.Rule?.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, result.Action);
        Assert.Equal(600, result.TimeoutSeconds);
        Assert.Equal([Message], result.TriggerPhrases);
        Assert.Equal("manual_blacklist_phrase", result.Source);
        Assert.Equal("unwanted_intimate_approach", result.EvidenceKind);
    }

    [Fact]
    public void Resolve_ExactPositiveLearningUsesExplicitActivePhraseEvidence()
    {
        var result = HumanModerationPriority.Resolve(
            Message,
            CreatePolicy(),
            [Match()],
            [Example(isPositive: true, ModerationActionKind.Timeout, 600)]);

        Assert.NotNull(result);
        Assert.Equal("exact_moderate_learning", result.Source);
        Assert.Equal("unwanted_intimate_approach", result.EvidenceKind);
    }

    [Fact]
    public void Resolve_GenericPersonalAttackPhraseDoesNotGuessEvidenceKind()
    {
        const string lowerText = "du idiot";
        var result = HumanModerationPriority.Resolve(
            lowerText,
            CreatePolicy(),
            [new BlacklistMatch(
                PhraseId: 174,
                RuleCode: ModerationRuleCodes.PersonalAttack,
                PolicySet: PolicySetCode.General,
                MatchedPhrase: lowerText)],
            examples: []);

        Assert.NotNull(result);
        Assert.Equal("manual_blacklist_phrase", result.Source);
        Assert.Null(result.EvidenceKind);
    }

    [Fact]
    public void Resolve_ExactModerateLearningDoesNotRequireCurrentPhraseMatch()
    {
        const string lowerText = "visit my channel";
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "policy")
        {
            ActiveRules =
            [
                new ModerationRuleDefinition(
                    ModerationRuleCodes.SelfPromotion,
                    PolicySetCode.General,
                    "Eigenwerbung",
                    ModerationRuleCodes.SelfPromotionPromptInstruction,
                    ModerationActionKind.Timeout,
                    86_400,
                    "Unaufgeforderte Eigenwerbung.",
                    Priority: 90),
            ],
        };
        var example = new RetrievalExample(
            lowerText,
            ModerationActionKind.Timeout,
            86_400,
            PolicySetCode.General,
            "Unaufgeforderte Eigenwerbung.",
            IsPositive: true,
            ModerationRuleCodes.SelfPromotion)
        {
            IsExactHumanDecision = true,
        };

        var result = HumanModerationPriority.Resolve(
            lowerText,
            policy,
            localMatches: [],
            [example]);

        Assert.NotNull(result);
        Assert.False(result.IsAllow);
        Assert.Equal("exact_moderate_learning", result.Source);
        Assert.Equal(ModerationRuleCodes.SelfPromotion, result.Rule?.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, result.Action);
        Assert.Equal(86_400, result.TimeoutSeconds);
        Assert.Empty(result.TriggerPhrases);
    }

    [Fact]
    public void ResolveConsensus_RequiresThreeStrongSameRuleExamplesAndMargin()
    {
        RetrievalExample Positive(double score) => new(
            "ähnliche anweisung",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.Backseatgaming,
            "Backseatgaming.",
            true,
            ModerationRuleCodes.Backseatgaming)
        {
            SimilarityScore = score,
        };
        var allow = new RetrievalExample(
            "erlaubtes beispiel",
            ModerationActionKind.None,
            null,
            PolicySetCode.General,
            string.Empty,
            false)
        {
            SimilarityScore = 0.55d,
        };

        var result = HumanModerationPriority.ResolveConsensus(
            [Positive(0.92d), Positive(0.88d), Positive(0.84d), allow]);

        Assert.NotNull(result);
        Assert.Equal("moderate", result.Decision);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, result.RuleCode);
        Assert.True(result.Margin >= 0.20d);
    }

    private static EffectivePolicySnapshot CreatePolicy() =>
        new(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "policy")
        {
            ActiveRules =
            [
                new ModerationRuleDefinition(
                    ModerationRuleCodes.PersonalAttack,
                    PolicySetCode.General,
                    "Beleidigung & Belästigung",
                    ModerationRuleCodes.PersonalAttackPromptInstruction,
                    ModerationActionKind.Timeout,
                    600,
                    "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
                    Priority: 50),
            ],
        };

    private static BlacklistMatch Match() =>
        new(
            PhraseId: 173,
            RuleCode: ModerationRuleCodes.PersonalAttack,
            PolicySet: PolicySetCode.General,
            MatchedPhrase: Message);

    private static RetrievalExample Example(
        bool isPositive,
        ModerationActionKind action,
        int? timeoutSeconds) =>
        new(
            Message,
            action,
            timeoutSeconds,
            PolicySetCode.General,
            isPositive
                ? "Unangebrachte persönliche Annäherung."
                : "Im konkreten Kontext erlaubt.",
            isPositive,
            ModerationRuleCodes.PersonalAttack)
        {
            IsExactHumanDecision = true,
        };
}
