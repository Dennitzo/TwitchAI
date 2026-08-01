using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class TwitchClipUrlClassifierTests
{
    [Theory]
    [InlineData("https://www.twitch.tv/channel/clip")]
    [InlineData("https://www.twitch.tv/Some_Channel/clip/FiveWordsForClipSlug")]
    [InlineData("https://twitch.tv/anotherchannel/clip/AnimatedOptimisticWasabiVoteNay")]
    [InlineData("https://clips.twitch.tv/AnimatedOptimisticWasabiVoteNay")]
    [InlineData("  https://clips.twitch.tv/FunPoisedGiraffeGingerPower-KDy2fwLNuUEHU  ")]
    [InlineData("https://www.twitch.tv/channel/clip/slug?filter=clips&range=7d")]
    public void StandaloneOfficialClip_IsRecognized(string text)
    {
        Assert.True(TwitchClipUrlClassifier.IsStandaloneAllowedTwitchClipUrl(text));
    }

    [Theory]
    [InlineData("schaut meinen kanal https://www.twitch.tv/channel/clip/slug")]
    [InlineData("https://www.twitch.tv/channel/clip/one https://clips.twitch.tv/two")]
    [InlineData("https://www.twitch.tv/channel")]
    [InlineData("https://www.twitch.tv/channel/videos")]
    [InlineData("https://www.twitch.tv/channel/clipbait/slug")]
    [InlineData("http://www.twitch.tv/channel/clip/slug")]
    [InlineData("https://www.twitch.tv.evil.example/channel/clip/slug")]
    [InlineData("https://clips.twitch.tv.evil.example/slug")]
    public void OtherContent_IsNotRecognizedAsStandaloneClip(string text)
    {
        Assert.False(TwitchClipUrlClassifier.IsStandaloneAllowedTwitchClipUrl(text));
    }
}
