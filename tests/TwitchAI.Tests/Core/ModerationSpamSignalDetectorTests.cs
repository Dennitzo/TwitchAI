using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationSpamSignalDetectorTests
{
    [Theory]
    [InlineData("Jaaa <3")]
    [InlineData("Wäre ich dabei")]
    [InlineData("tschüssi @chat, @mods und shlo")]
    [InlineData("uhh geilo")]
    [InlineData("ok wo anmeldung")]
    [InlineData("und 1 Shloschlik")]
    [InlineData("Geil Bin dabei KEKW")]
    public void HasDeterministicSignal_RejectsObservedOrdinaryBatchMessages(
        string message)
    {
        Assert.False(
            ModerationSpamSignalDetector.HasDeterministicSignal(
                message,
                twitchEmoteCount: 0));
    }

    [Fact]
    public void IsRepeatedPhraseFlood_DetectsFiveRepeatedMultiwordPhrases()
    {
        const string message =
            "I cant stop laughing MegaLUL I cant stop laughing MegaLUL "
            + "I cant stop laughing MegaLUL I cant stop laughing MegaLUL "
            + "I cant stop laughing MegaLUL";

        Assert.True(
            ModerationSpamSignalDetector.IsRepeatedPhraseFlood(message));
    }

    [Theory]
    [InlineData("!!!!!!!!!!!!!!!!!!!!abc", true)]
    [InlineData("!!!!!!!!!!!!!!!!!!!abc", false)]
    [InlineData("!!!!!!!!!!!!!!!!!!!!abcdefghijkl", false)]
    [InlineData("Das ist ein normaler Satz!", false)]
    public void IsSymbolFlood_RequiresCountAndDensity(
        string message,
        bool expected)
    {
        Assert.Equal(
            expected,
            ModerationSpamSignalDetector.IsSymbolFlood(message));
    }
}
