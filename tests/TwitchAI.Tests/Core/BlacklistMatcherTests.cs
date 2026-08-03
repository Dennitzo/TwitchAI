using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class BlacklistMatcherTests
{
    [Fact]
    public void Match_UsesRuleNormalizedLowercaseAndOnlyActivePolicyRules()
    {
        var text = new ChatTextNormalizer().Process("DU MUSST\u200B  LINKS FAHREN!");
        var policy = new EffectivePolicySnapshot(
        [
            new PolicySetRevision(PolicySetCode.General, 1),
            new PolicySetRevision(PolicySetCode.Backseatgaming, 1),
        ],
        "policy");
        PhraseRule[] rules =
        [
            Rule(1, "backseat", PolicySetCode.Backseatgaming, "musst links", positives: 1),
            Rule(2, "disabled", PolicySetCode.General, "fahren", positives: 0),
        ];

        var matches = new BlacklistMatcher().Match(text, policy, rules);

        var match = Assert.Single(matches);
        Assert.Equal(1, match.PhraseId);
        Assert.Equal(PolicySetCode.Backseatgaming, match.PolicySet);
    }

    [Fact]
    public void Match_IgnoresBackseatRuleWhenPolicyIsInactive()
    {
        var text = new ChatTextNormalizer().Process("du musst links fahren");
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "policy");

        var matches = new BlacklistMatcher().Match(
            text,
            policy,
            [Rule(1, "backseat", PolicySetCode.Backseatgaming, "musst links", positives: 1)]);

        Assert.Empty(matches);
    }

    [Theory]
    [InlineData("ihr schweine", true)]
    [InlineData("schweinebraten", false)]
    public void Match_RequiresLexicalBoundariesForPhraseMatches(
        string message,
        bool expectedMatch)
    {
        var text = new ChatTextNormalizer().Process(message);
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "policy");

        var matches = new BlacklistMatcher().Match(
            text,
            policy,
            [Rule(1, ModerationRuleCodes.Insult, PolicySetCode.General, "schweine", 1)]);

        Assert.Equal(expectedMatch, matches.Count == 1);
    }

    [Fact]
    public void Match_TreatsUnderscoreAsPartOfAWord()
    {
        var text = new ChatTextNormalizer().Process("@x_shadow");
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "policy");
        var matcher = new BlacklistMatcher();

        var partial = matcher.Match(
            text,
            policy,
            [Rule(1, ModerationRuleCodes.Insult, PolicySetCode.General, "shadow", 1)]);
        var complete = matcher.Match(
            text,
            policy,
            [Rule(2, ModerationRuleCodes.Insult, PolicySetCode.General, "x_shadow", 1)]);

        Assert.Empty(partial);
        Assert.Equal(2, Assert.Single(complete).PhraseId);
    }

    [Fact]
    public void Match_RecognizesLongPhraseWithSmallTypoAndReportsFuzzyScore()
    {
        var text = new ChatTextNormalizer().Process("du musss links fahren");
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.Backseatgaming, 1)],
            "policy");

        var match = Assert.Single(new BlacklistMatcher().Match(
            text,
            policy,
            [Rule(1, ModerationRuleCodes.Backseatgaming, PolicySetCode.Backseatgaming, "musst links", 1)]));

        Assert.Equal("fuzzy_phrase", match.MatchKind);
        Assert.InRange(match.Score, 0.78d, 0.92d);
    }

    [Theory]
    [InlineData(PhraseManualOverride.Auto, 1, 0, true)]
    [InlineData(PhraseManualOverride.Auto, 3, 2, true)]
    [InlineData(PhraseManualOverride.Auto, 2, 2, false)]
    [InlineData(PhraseManualOverride.Active, 0, 10, true)]
    [InlineData(PhraseManualOverride.Inactive, 10, 0, false)]
    public void PhraseRule_ActivationRespectsFeedbackThresholdAndOverride(
        PhraseManualOverride manualOverride,
        int positives,
        int negatives,
        bool expected)
    {
        var rule = Rule(
            1,
            "rule",
            PolicySetCode.General,
            "phrase",
            positives,
            negatives,
            manualOverride);

        Assert.Equal(expected, rule.IsActive);
    }

    private static PhraseRule Rule(
        long id,
        string code,
        PolicySetCode policySet,
        string phrase,
        int positives,
        int negatives = 0,
        PhraseManualOverride manualOverride = PhraseManualOverride.Auto)
    {
        return new PhraseRule(
            id,
            code,
            policySet,
            phrase,
            manualOverride,
            positives,
            negatives,
            IsArchived: false);
    }
}
