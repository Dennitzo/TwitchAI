using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationTriggerEvidenceNormalizerTests
{
    [Fact]
    public void Normalize_InsultKeepsCompleteTargetedEvidence()
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "du bist ein idiot",
            ModerationRuleCodes.Insult,
            ["du bist ein idiot"]);

        Assert.Equal(["du bist ein idiot"], result);
    }

    [Fact]
    public void Normalize_InsultCombinesTargetAndInsultFragmentsIntoSourceClause()
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "du bist ein idiot",
            ModerationRuleCodes.Insult,
            ["du bist", "idiot"]);

        Assert.Equal(["du bist ein idiot"], result);
    }

    [Fact]
    public void Normalize_InsultRejectsHarmlessInstructionFragments()
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "@dennisith dann halt sie fest kappa",
            ModerationRuleCodes.Insult,
            ["dann halt", "sie fest"]);

        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_InsultRejectsNeutralComparisonFragment()
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "@x_shadow ist auch net besser lul",
            ModerationRuleCodes.Insult,
            ["ist auch net besser"]);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("@x_shadow")]
    [InlineData("x_shadow")]
    public void Normalize_InsultRejectsMentionOrUnderscoredUsernameAlone(
        string candidate)
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "@x_shadow du bist ein idiot",
            ModerationRuleCodes.Insult,
            [candidate]);

        Assert.Empty(result);
    }

    [Fact]
    public void Normalize_GenericRuleReturnsOnlyOneContextualEvidencePhrase()
    {
        var result = ModerationTriggerEvidenceNormalizer.Normalize(
            "spam spam spam jetzt sofort",
            ModerationRuleCodes.Spam,
            ["spam", "spam spam", "spam spam spam", "jetzt sofort"]);

        var evidence = Assert.Single(result);
        Assert.True(evidence is "spam spam" or "spam spam spam");
    }
}
