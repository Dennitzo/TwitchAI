using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ChatTextNormalizerTests
{
    private readonly ChatTextNormalizer _sut = new();

    [Fact]
    public void Normalize_UsesInvariantLowercaseNfkcAndCollapsedWhitespace()
    {
        var actual = _sut.Normalize("  ＨＥＬＬＯ\t\nWÖRLD  ");

        Assert.Equal("hello wörld", actual);
        Assert.Equal(ChatTextNormalizer.CurrentVersion, _sut.Version);
    }

    [Fact]
    public void Normalize_IsCultureIndependent()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        var originalUiCulture = System.Globalization.CultureInfo.CurrentUICulture;

        try
        {
            var turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            System.Globalization.CultureInfo.CurrentCulture = turkish;
            System.Globalization.CultureInfo.CurrentUICulture = turkish;

            Assert.Equal("i", _sut.Normalize("I"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
            System.Globalization.CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void ComputeSha256_IsStableAndCaseSensitiveToItsInput()
    {
        var first = _sut.ComputeSha256("kekw");
        var second = _sut.ComputeSha256("kekw");
        var differentCase = _sut.ComputeSha256("KEKW");

        Assert.Equal(64, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, differentCase);
    }

    [Fact]
    public void Normalize_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _sut.Normalize(null!));
    }
}
