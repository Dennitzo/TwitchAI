using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationReasonNormalizerTests
{
    [Fact]
    public void Normalize_MapsLongEnglishHarassmentReasonToShortGermanReason()
    {
        const string reason =
            "Harassment / profanity directed at a user. The content is disallowed under policy G and should be removed.";

        var result = ModerationReasonNormalizer.Normalize(reason, PolicySetCode.General);

        Assert.Equal("Belästigung oder Beleidigung gegen einen Nutzer.", result);
    }

    [Fact]
    public void Normalize_KeepsOnlyFirstGermanSentence()
    {
        var result = ModerationReasonNormalizer.Normalize(
            "Direkte Beleidigung eines Nutzers. Die Nachricht sollte gelöscht werden.",
            PolicySetCode.General);

        Assert.Equal("Direkte Beleidigung eines Nutzers.", result);
    }

    [Fact]
    public void Normalize_UsesPolicySpecificFallbackForUnknownEnglishReason()
    {
        var result = ModerationReasonNormalizer.Normalize(
            "This message should be removed under the applicable policy.",
            PolicySetCode.Backseatgaming);

        Assert.Equal("Unaufgeforderter Spielhinweis oder Spoiler.", result);
    }

    [Theory]
    [InlineData("G_INSULT", "Direkte Beleidigung gegen einen Nutzer.")]
    [InlineData("G_THREAT", "Gewaltandrohung gegen eine Person.")]
    [InlineData("G_PERSONAL_DATA", "Veröffentlichung persönlicher Daten.")]
    [InlineData("G_SPAM", "Wiederholter Spam.")]
    [InlineData("G_OTHER_ABUSE", "Sonstiger Verstoß gegen die Chatregeln.")]
    [InlineData("BACKSEATGAMING", "Unaufgeforderter Spielhinweis oder Spoiler.")]
    public void Normalize_UsesExactRuleSpecificGermanFallback(
        string ruleCode,
        string expected)
    {
        var result = ModerationReasonNormalizer.Normalize(
            "This content should be removed under the applicable policy.",
            ruleCode,
            ruleCode == ModerationRuleCodes.Backseatgaming
                ? PolicySetCode.Backseatgaming
                : PolicySetCode.General);

        Assert.Equal(expected, result);
        Assert.True(result.Length <= ModerationReasonNormalizer.MaximumLength);
    }

    [Fact]
    public void Normalize_PrefersConfiguredPublicReasonForInvalidModelText()
    {
        var rule = new ModerationRuleDefinition(
            "G_INSULT",
            PolicySetCode.General,
            "Beleidigung",
            "test",
            ModerationActionKind.Delete,
            null,
            "Benutzerdefinierter deutscher Grund.",
            Priority: 70);

        var result = ModerationReasonNormalizer.Normalize(
            "This content should be removed under policy G.",
            rule,
            PolicySetCode.General);

        Assert.Equal("Benutzerdefinierter deutscher Grund.", result);
    }

    [Fact]
    public void Normalize_InvalidConfiguredReasonUsesStaticSafetyFallback()
    {
        var rule = new ModerationRuleDefinition(
            "G_INSULT",
            PolicySetCode.General,
            "Beleidigung",
            "test",
            ModerationActionKind.Delete,
            null,
            "This configured reason is invalid and should be removed.",
            Priority: 70);

        var result = ModerationReasonNormalizer.Normalize(
            reason: null,
            rule,
            PolicySetCode.General);

        Assert.Equal("Direkte Beleidigung gegen einen Nutzer.", result);
    }
}
