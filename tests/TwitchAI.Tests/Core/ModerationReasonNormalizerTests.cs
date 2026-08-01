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

        Assert.Equal(
            "Belästigung oder Beleidigung eines Nutzers oder Streamers.",
            result);
    }

    [Fact]
    public void Normalize_KeepsOnlyFirstGermanSentence()
    {
        var result = ModerationReasonNormalizer.Normalize(
            "Direkte Beleidigung eines Nutzers. Die Nachricht sollte gelöscht werden.",
            PolicySetCode.General);

        Assert.Equal(
            "Direkte Beleidigung eines Nutzers oder Streamers.",
            result);
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
    [InlineData(
        "G_INSULT",
        "Direkte Beleidigung eines Nutzers oder Streamers.")]
    [InlineData(
        "G_HARASSMENT",
        "Gezielte Belästigung eines Nutzers oder Streamers.")]
    [InlineData("G_THREAT", "Gewaltandrohung gegen eine Person.")]
    [InlineData("G_PERSONAL_DATA", "Veröffentlichung persönlicher Daten.")]
    [InlineData("G_SPAM", "Wiederholter Spam.")]
    [InlineData("G_ADVERTISING", "Unaufgeforderte Werbung oder Eigenwerbung.")]
    [InlineData("G_BEGGING", "Betteln nach Subs, Geschenken oder Giveaways.")]
    [InlineData(
        "G_STREAMER_INSTRUCTION",
        "Unaufgeforderte Anweisung; der Streamer entscheidet selbst über seinen Content.")]
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

        Assert.Equal(
            "Direkte Beleidigung eines Nutzers oder Streamers.",
            result);
    }

    [Fact]
    public void Normalize_ShortEnglishBanReasonUsesGermanRuleFallback()
    {
        var result = ModerationReasonNormalizer.Normalize(
            "Severe insult.",
            "G_INSULT",
            PolicySetCode.General);

        Assert.Equal(
            "Direkte Beleidigung eines Nutzers oder Streamers.",
            result);
    }

    [Theory]
    [InlineData(
        "Beleidigung gegen einen Nutzer.",
        "Beleidigung gegen einen Nutzer oder Streamer.")]
    [InlineData(
        "Beleidigung eines Nutzers.",
        "Beleidigung eines Nutzers oder Streamers.")]
    [InlineData(
        "Gezielte Belästigung eines Nutzers.",
        "Gezielte Belästigung eines Nutzers oder Streamers.")]
    [InlineData(
        "Beleidigung eines Nutzers oder Streamers.",
        "Beleidigung eines Nutzers oder Streamers.")]
    public void Normalize_AddsStreamerToGermanTargetReasons(
        string reason,
        string expected)
    {
        var result = ModerationReasonNormalizer.Normalize(
            reason,
            "G_INSULT",
            PolicySetCode.General);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_AddsStreamerToConfiguredLegacyReason()
    {
        var rule = new ModerationRuleDefinition(
            "G_INSULT",
            PolicySetCode.General,
            "Beleidigung",
            "test",
            ModerationActionKind.Timeout,
            600,
            "Direkte Beleidigung gegen einen Nutzer.",
            Priority: 70);

        var result = ModerationReasonNormalizer.Normalize(
            "This content violates the active rule.",
            rule,
            PolicySetCode.General);

        Assert.Equal(
            "Direkte Beleidigung gegen einen Nutzer oder Streamer.",
            result);
    }

    [Theory]
    [InlineData(
        "Advertising.",
        ModerationRuleCodes.Advertising,
        "Unaufgeforderte Werbung oder Eigenwerbung.")]
    [InlineData(
        "Begging.",
        ModerationRuleCodes.Begging,
        "Betteln nach Subs, Geschenken oder Giveaways.")]
    [InlineData(
        "Instruction.",
        ModerationRuleCodes.StreamerInstruction,
        "Unaufgeforderte Anweisung; der Streamer entscheidet selbst über seinen Content.")]
    public void Normalize_OneWordEnglishReasonUsesGermanRuleFallback(
        string reason,
        string ruleCode,
        string expected)
    {
        var result = ModerationReasonNormalizer.Normalize(
            reason,
            ruleCode,
            PolicySetCode.General);

        Assert.Equal(expected, result);
    }
}
