using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class AllowLearningPhraseMatcherTests
{
    [Fact]
    public void TryMatch_ExactAllowIsContextAndNotAnAutomaticDecision()
    {
        var example = Allow("wie heißt das spiel?", 1d) with
        {
            IsExactHumanDecision = true,
        };

        var found = AllowLearningPhraseMatcher.TryMatch(
            "wie heißt das spiel?",
            example,
            out var match);

        Assert.True(found);
        Assert.True(match.IsExact);
        Assert.Equal("wie heißt das spiel?", match.MatchedPhrase);
    }

    [Fact]
    public void TryMatch_ReturnsSharedContiguousPhrase()
    {
        var found = AllowLearningPhraseMatcher.TryMatch(
            "ich glaube das ist nur ein scherz im chat",
            Allow("das ist nur ein scherz", 0.82d),
            out var match);

        Assert.True(found);
        Assert.Equal("das ist nur ein scherz", match.MatchedPhrase);
        Assert.False(match.IsExact);
    }

    [Fact]
    public void TryMatch_RejectsWeakSingleWordOverlap()
    {
        var found = AllowLearningPhraseMatcher.TryMatch(
            "du bist heute schlecht",
            Allow("heute läuft das spiel gut", 0.41d),
            out _);

        Assert.False(found);
    }

    [Fact]
    public void TryMatch_IgnoresModerateExamples()
    {
        var moderate = Allow("geh nach links", 1d) with
        {
            IsPositive = true,
            Action = ModerationActionKind.Timeout,
        };

        Assert.False(AllowLearningPhraseMatcher.TryMatch(
            "geh nach links",
            moderate,
            out _));
    }

    private static RetrievalExample Allow(string text, double score) =>
        new(
            text,
            ModerationActionKind.None,
            null,
            PolicySetCode.General,
            string.Empty,
            IsPositive: false,
            ModerationRuleCodes.PersonalAttack)
        {
            SimilarityScore = score,
            Source = "manual_learning",
        };
}
