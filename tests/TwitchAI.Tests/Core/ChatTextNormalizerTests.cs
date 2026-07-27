using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ChatTextNormalizerTests
{
    private readonly ChatTextNormalizer _sut = new();

    [Fact]
    public void Process_PreservesOriginalAndBuildsIndependentLowercaseForms()
    {
        const string original = "  ＫＥＫＷ\u200B \tNEXT  ";

        var result = _sut.Process(original);

        Assert.Equal(original, result.OriginalText);
        Assert.Equal(original.ToLowerInvariant(), result.LowerText);
        Assert.Equal("kekw next", result.RuleNormalizedText);
        Assert.Equal(ChatTextNormalizer.CurrentVersion, result.NormalizationVersion);
    }

    [Fact]
    public void Process_IsInvariantUnderTurkishCurrentCulture()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

            Assert.Equal("i", _sut.Process("I").LowerText);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Process_HashesAreDeterministicAndRepresentDifferentForms()
    {
        var first = _sut.Process(" KEKW ");
        var second = _sut.Process(" KEKW ");

        Assert.Equal(64, first.OriginalSha256.Length);
        Assert.Equal(64, first.RuleNormalizedSha256.Length);
        Assert.Equal(first.OriginalSha256, second.OriginalSha256);
        Assert.Equal(first.RuleNormalizedSha256, second.RuleNormalizedSha256);
        Assert.NotEqual(first.OriginalSha256, first.RuleNormalizedSha256);
    }
}
