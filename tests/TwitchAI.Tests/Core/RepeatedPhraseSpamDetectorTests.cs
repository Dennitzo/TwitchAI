using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class RepeatedPhraseSpamDetectorTests
{
    [Fact]
    public void IsSpam_DetectsPhraseRepeatedFiveTimes()
    {
        const string text = "I cant stop laughing MegaLUL I cant stop laughing MegaLUL "
            + "I cant stop laughing MegaLUL I cant stop laughing MegaLUL "
            + "I cant stop laughing MegaLUL";

        Assert.True(RepeatedPhraseSpamDetector.IsSpam(text));
    }

    [Fact]
    public void IsSpam_DoesNotFlagFourPhraseRepetitions()
    {
        const string text = "please stop now please stop now please stop now please stop now";

        Assert.False(RepeatedPhraseSpamDetector.IsSpam(text));
    }

    [Fact]
    public void IsSpam_DoesNotTreatSingleRepeatedTokenAsPhraseSpam()
    {
        Assert.False(RepeatedPhraseSpamDetector.IsSpam("MegaLUL MegaLUL MegaLUL MegaLUL MegaLUL"));
    }
}
