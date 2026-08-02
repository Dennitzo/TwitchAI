using TwitchAI.App.ViewModels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class LearningReviewViewModelTests
{
    [Fact]
    public void ModerateSpamResult_KeepsSpamCategoryVisibleAndReviewable()
    {
        var result = Result(
            ModerationRuleCodes.Spam,
            PolicySetCode.General,
            ModerationActionKind.Timeout,
            timeoutSeconds: 120,
            reason: "Wiederholter Spam.");

        var review = new LearningReviewViewModel(result);

        Assert.Equal(ModerationRuleCodes.Spam, review.SelectedRuleCode);
        Assert.Equal("Spam", review.SelectedRuleDisplayName);
        var saved = review.CreateReview();
        Assert.Equal(ModerationRuleCodes.Spam, saved.RuleCode);
        Assert.Equal(PolicySetCode.General, saved.PolicySet);
        Assert.Equal(ModerationActionKind.Timeout, saved.Action);
        Assert.Equal(120, saved.TimeoutSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TransientEmptySelection_DoesNotClearSpamCategory(
        string? transientValue)
    {
        var review = new LearningReviewViewModel(
            Result(
                ModerationRuleCodes.Spam,
                PolicySetCode.General,
                ModerationActionKind.Timeout,
                timeoutSeconds: 120,
                reason: "Wiederholter Spam."));

        var selected = review.TrySelectRule(transientValue);

        Assert.False(selected);
        Assert.Equal(ModerationRuleCodes.Spam, review.SelectedRuleCode);
        Assert.Equal("Spam", review.SelectedRuleDisplayName);
        Assert.Equal(ModerationRuleCodes.Spam, review.CreateReview().RuleCode);
    }

    [Fact]
    public async Task SelectRuleAsync_RequestsAndAppliesContextualAiReason()
    {
        var testService = new RecordingLearningTestService();
        var viewModel = new LearningViewModel(
            testService,
            new NoOpLearningFeedbackService())
        {
            Review = new LearningReviewViewModel(
                Result(
                    ModerationRuleCodes.Spam,
                    PolicySetCode.General,
                    ModerationActionKind.Timeout,
                    timeoutSeconds: 120,
                    reason: "Wiederholter Spam.")),
        };

        var selected = await viewModel.SelectRuleAsync(
            ModerationRuleCodes.Backseatgaming);

        Assert.True(selected);
        var request = Assert.Single(testService.ReasonRequests);
        Assert.Equal(ModerationRuleCodes.Backseatgaming, request.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, request.Action);
        Assert.Equal(600, request.TimeoutSeconds);
        Assert.Equal(
            "Die Nachricht gibt dem Streamer ungefragt eine Spielanweisung.",
            viewModel.Review!.Reason);
        Assert.False(viewModel.IsBusy);
    }

    private static LearningTestResult Result(
        string ruleCode,
        PolicySetCode policySet,
        ModerationActionKind action,
        int? timeoutSeconds,
        string reason)
    {
        var rules = new[]
        {
            Rule(
                ModerationRuleCodes.OtherAbuse,
                PolicySetCode.General,
                "Sonstiger General-Verstoß",
                "Sonstiger Verstoß gegen die Chatregeln.",
                ModerationActionKind.Timeout,
                600),
            Rule(
                ModerationRuleCodes.Spam,
                PolicySetCode.General,
                "Spam",
                "Wiederholter Spam.",
                ModerationActionKind.Timeout,
                120),
            Rule(
                ModerationRuleCodes.Backseatgaming,
                PolicySetCode.Backseatgaming,
                "Backseatgaming",
                "Unaufgeforderter Spielhinweis.",
                ModerationActionKind.Timeout,
                600),
        };
        return new LearningTestResult(
            Guid.Parse("e5e25de8-397b-4201-9861-fc9386549856"),
            "SPAM SPAM",
            "spam spam",
            LearningVerdict.Moderate,
            action,
            timeoutSeconds,
            policySet,
            ruleCode,
            reason,
            ["spam spam"],
            0.95,
            [ModerationRuleCodes.Spam],
            250,
            "policy-1",
            rules,
            "openai/gpt-oss-20b",
            100,
            20,
            120,
            new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero),
            UsedRuleFallback: false);
    }

    private static ModerationRuleDefinition Rule(
        string code,
        PolicySetCode policySet,
        string title,
        string reason,
        ModerationActionKind action,
        int? timeoutSeconds) =>
        new(
            code,
            policySet,
            title,
            $"Erkenne {title}.",
            action,
            timeoutSeconds,
            reason);

    private sealed class RecordingLearningTestService : ILearningTestService
    {
        public List<LearningReasonRequest> ReasonRequests { get; } = [];

        public ValueTask<LearningTestResult> AnalyzeAsync(
            LearningTestRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<bool> IsPolicyCurrentAsync(
            LearningTestResult result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<LearningReasonResult> GenerateReasonAsync(
            LearningReasonRequest request,
            CancellationToken cancellationToken = default)
        {
            ReasonRequests.Add(request);
            return ValueTask.FromResult(
                new LearningReasonResult(
                    request.RuleCode,
                    "Die Nachricht gibt dem Streamer ungefragt eine Spielanweisung.",
                    10,
                    20,
                    5,
                    25));
        }
    }

    private sealed class NoOpLearningFeedbackService : ILearningFeedbackService
    {
        public ValueTask<LearningFeedbackResult> SaveAsync(
            LearningReviewItem review,
            LearningFeedbackDecision decision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
