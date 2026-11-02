using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationReasonComposerTests
{
    [Theory]
    [InlineData(ModerationActionKind.Timeout)]
    [InlineData(ModerationActionKind.Ban)]
    public void TryComposeAiReason_ComposesViolationAndNeutralGuidance(
        ModerationActionKind action)
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers",
            "Chatregeln künftig bitte beachten",
            action,
            PersonalAttackRule(),
            "@mayvo du bist völlig unfähig",
            ["du bist völlig unfähig"],
            out var composition,
            out var errorCode);

        Assert.True(valid);
        Assert.Empty(errorCode);
        Assert.Equal(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            composition.ViolationReason);
        Assert.Equal(
            "Chatregeln künftig bitte beachten.",
            composition.Guidance);
        Assert.Equal(
            $"{composition.ViolationReason} {composition.Guidance}",
            composition.Reason);
        Assert.True(composition.Reason.Length <= 120);
    }

    [Theory]
    [InlineData("Künftig bitte respektvoll kommunizieren.")]
    [InlineData("Zukünftig auf einen höflichen Umgang achten.")]
    [InlineData("Fortan bitte sachlich kommunizieren.")]
    [InlineData("Bitte künftig Spielhinweise zurückhalten.")]
    public void TryComposeAiReason_AcceptsNeutralFutureGuidance(
        string guidance)
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            guidance,
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "@mayvo du bist völlig unfähig",
            ["du bist völlig unfähig"],
            out var composition,
            out var errorCode);

        Assert.True(valid);
        Assert.Empty(errorCode);
        Assert.Equal(guidance, composition.Guidance);
    }

    [Fact]
    public void TryComposeAiReason_DeleteRequiresEmptyParts()
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            string.Empty,
            string.Empty,
            ModerationActionKind.Delete,
            PersonalAttackRule(),
            "spam spam spam",
            ["spam spam spam"],
            out var composition,
            out var errorCode);

        Assert.True(valid);
        Assert.Empty(errorCode);
        Assert.Empty(composition.Reason);
        Assert.Empty(composition.ViolationReason);
        Assert.Empty(composition.Guidance);
    }

    [Theory]
    [InlineData("@mayvo künftig bitte respektvoll bleiben.")]
    [InlineData("Max künftig bitte vermeiden.")]
    [InlineData("Du solltest Beleidigungen künftig vermeiden.")]
    [InlineData("Beachte künftig bitte die Chatregeln.")]
    [InlineData("Respekt gegenüber anderen künftig wahren.")]
    [InlineData("Bei Wiederholung folgt ein Timeout.")]
    [InlineData("\"Beleidigungen\" künftig bitte vermeiden.")]
    [InlineData("Die Nachricht künftig bitte respektvoll formulieren.")]
    [InlineData("G_PERSONAL_ATTACK künftig bitte vermeiden.")]
    [InlineData("Respektvoller Ton im Chat wird erwartet.")]
    [InlineData("Spielhinweise nur auf Nachfrage geben.")]
    public void TryComposeAiReason_RejectsForbiddenGuidance(string guidance)
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            "Gezielte persönliche Herabsetzung eines Nutzers oder des Streamers.",
            guidance,
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "@Max du idiot",
            ["du idiot"],
            out _,
            out var errorCode);

        Assert.False(valid);
        Assert.Equal("guidance_invalid", errorCode);
    }

    [Fact]
    public void TryComposeAiReason_RejectsTriggerPhraseInEitherPart()
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            "Direkte Bezeichnung als Idiot.",
            "Abwertungen künftig bitte vermeiden.",
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "du idiot",
            ["idiot"],
            out _,
            out var errorCode);

        Assert.False(valid);
        Assert.Equal("reason_contains_trigger_phrase", errorCode);
    }

    [Fact]
    public void TryComposeAiReason_RejectsGenericPublicReason()
    {
        var valid = ModerationReasonComposer.TryComposeAiReason(
            PersonalAttackRule().PublicReason,
            "Chatregeln künftig bitte beachten.",
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "du idiot",
            ["du idiot"],
            out _,
            out var errorCode);

        Assert.False(valid);
        Assert.Equal("violation_reason_invalid", errorCode);
    }

    [Theory]
    [InlineData("Gezielte persönliche Herabsetzung von @Max.")]
    [InlineData("Gezielte persönliche Herabsetzung von Max.")]
    [InlineData("G_PERSONAL_ATTACK wegen gezielter persönlicher Herabsetzung.")]
    [InlineData("Targeted harassment against a user.")]
    public void TryComposeReasonOnly_RejectsIdentityCodeAndForeignLanguage(
        string candidate)
    {
        var valid = ModerationReasonComposer.TryComposeReasonOnly(
            candidate,
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "@max du bist völlig unfähig",
            ["du bist völlig unfähig"],
            out _,
            out var errorCode);

        Assert.False(valid);
        Assert.Equal("reason_invalid", errorCode);
    }

    [Fact]
    public void TryComposeAiReason_AppliesPartLimitsAfterAddingPunctuation()
    {
        const string violationPrefix = "Konkrete persönliche Herabsetzung ";
        var violationReason = violationPrefix
            + new string(
                'x',
                ModerationReasonComposer.MaximumViolationReasonLength
                - violationPrefix.Length);

        var valid = ModerationReasonComposer.TryComposeAiReason(
            violationReason,
            "Abwertungen künftig bitte vermeiden.",
            ModerationActionKind.Timeout,
            PersonalAttackRule(),
            "du idiot",
            ["du idiot"],
            out _,
            out var errorCode);

        Assert.False(valid);
        Assert.Equal("violation_reason_invalid", errorCode);
    }

    private static ModerationRuleDefinition PersonalAttackRule() =>
        new(
            ModerationRuleCodes.PersonalAttack,
            PolicySetCode.General,
            "Beleidigung und Belästigung",
            "test",
            ModerationActionKind.Timeout,
            600,
            "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            Priority: 50);
}
