using TwitchAI.App.ViewModels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class ModerationCaseItemViolationCategoryTests
{
    [Fact]
    public void RuleCodeBeforeSnapshot_ProvidesProvisionalDisplayAndRemainsReviewable()
    {
        var item = CreateCase();

        item.RuleCode = ModerationRuleCodes.Spam;

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
        Assert.Equal(
            ModerationRuleCodes.Spam,
            Assert.Single(item.ViolationRuleOptions).Code);
        Assert.False(item.HasViolationRuleSnapshot);
        Assert.True(item.CanExecute);
    }

    [Fact]
    public void ReplacingOptionSnapshot_RetainsCanonicalRuleCodeAcrossNewInstances()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [InsultOption(), SpamOption()],
            ModerationRuleCodes.Spam);

        var firstSnapshot = item.ViolationRuleOptions;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), InsultOption()],
            selectedRuleCode: null);

        Assert.NotSame(firstSnapshot, item.ViolationRuleOptions);
        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
        Assert.True(item.HasViolationRuleSnapshot);
        Assert.True(item.CanExecute);

        // A recycled ComboBox may transiently push an empty SelectedValue.
        item.RuleCode = null;
        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Spam", item.ViolationCategoryDisplayName);
    }

    [Fact]
    public void SnapshotWithoutPersistedRule_KeepsVisibleUnavailableFallback()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;

        item.ReplaceViolationRuleOptions(
            [InsultOption()],
            selectedRuleCode: null);

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.StartsWith("Spam", item.ViolationCategoryDisplayName, StringComparison.Ordinal);
        Assert.Contains(
            "nicht mehr aktiv",
            item.ViolationCategoryDisplayName,
            StringComparison.Ordinal);
        Assert.Equal(
            ModerationRuleCodes.Spam,
            item.ViolationRuleOptions[0].Code);
        Assert.True(item.HasViolationRuleSnapshot);
    }

    [Fact]
    public void SelectingByCode_AppliesSelectedRuleDefaults()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), BeggingOption()],
            ModerationRuleCodes.Spam);

        var selected = item.TrySelectViolationRule(ModerationRuleCodes.Begging);

        Assert.True(selected);
        Assert.Equal(ModerationRuleCodes.Begging, item.RuleCode);
        Assert.Equal("Gebettel", item.ViolationCategoryDisplayName);
        Assert.Equal("General", item.PolicySet);
        Assert.Equal("timeout", item.Action);
        Assert.Equal(86_400, item.TimeoutSeconds);
        Assert.Equal("Bestehender Grund.", item.Reason);
    }

    [Fact]
    public void DirectReviewActions_KeepAllowStricterThanDiscard()
    {
        var item = CreateCase();

        Assert.False(item.CanAllow);
        Assert.True(item.CanDiscard);

        item.RuleCode = ModerationRuleCodes.Spam;

        Assert.True(item.CanAllow);
        Assert.True(item.CanDiscard);

        item.IsBusy = true;

        Assert.False(item.CanAllow);
        Assert.False(item.CanDiscard);
        Assert.False(item.CanExecute);
    }

    [Fact]
    public void ChatReply_DoesNotOfferAllowedLearning()
    {
        var item = CreateCase(isChatReply: true);

        Assert.False(item.CanAllow);
        Assert.True(item.CanDiscard);
        Assert.True(item.CanExecute);
    }

    [Fact]
    public void AutoModHeldCase_OffersAllowAndOnlyTimeoutOrBanExecution()
    {
        var item = CreateCase(isAutoModHeld: true);
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [new ViolationCategoryOption(
                "General",
                ModerationRuleCodes.Spam,
                "Spam",
                "Wiederholter Spam.",
                "delete",
                null)],
            ModerationRuleCodes.Spam,
            applyFallbackDefaults: true);

        Assert.Equal("timeout", item.Action);
        Assert.True(item.CanAllow);
        Assert.True(item.CanExecute);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Visible,
            item.AllowActionVisibility);
        Assert.Equal(
            Microsoft.UI.Xaml.Visibility.Collapsed,
            item.DeleteActionVisibility);

        item.Action = "delete";
        Assert.False(item.CanExecute);
        Assert.True(item.CanAllow);
    }

    [Fact]
    public async Task SelectingNewCategory_GeneratesContextSpecificReasonAndBlocksExecution()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), BeggingOption()],
            ModerationRuleCodes.Spam);
        var state = new AppState();
        state.ModerationCases.Add(item);
        var reasonService = new BlockingReviewReasonService();
        var sut = new ModerationQueueViewModel(
            state,
            executor: null!,
            feedbackService: null!,
            coordinator: null!,
            configuration: null!,
            policyStore: null!,
            chatReplyExecutor: null!,
            reviewReasonService: reasonService);

        var selection = sut.SelectViolationRuleAsync(
            item,
            ModerationRuleCodes.Begging);
        var request = await reasonService.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal("test", request.OriginalText);
        Assert.Equal("channel-1", request.BroadcasterId);
        Assert.Equal("509658", request.CategoryId);
        Assert.Equal(ModerationRuleCodes.Begging, request.RuleCode);
        Assert.Equal(ModerationActionKind.Timeout, request.Action);
        Assert.Equal(86_400, request.TimeoutSeconds);
        Assert.Empty(item.Reason);
        Assert.True(item.IsReasonGenerating);
        Assert.False(item.CanExecute);

        reasonService.Complete(
            new ReviewReasonResult(
                ModerationRuleCodes.Begging,
                "Wiederholtes Betteln nach Vorteilen.",
                AiLatencyMilliseconds: 5,
                PromptTokens: 10,
                CompletionTokens: 5,
                TotalTokens: 15));
        await selection;

        Assert.Equal("Wiederholtes Betteln nach Vorteilen.", item.Reason);
        Assert.False(item.IsReasonGenerating);
        Assert.True(item.CanExecute);
    }

    [Fact]
    public async Task RapidCategoryChanges_NeverApplyLateReasonFromPreviousCategory()
    {
        var item = CreateCase();
        item.RuleCode = ModerationRuleCodes.Spam;
        item.ReplaceViolationRuleOptions(
            [SpamOption(), BeggingOption()],
            ModerationRuleCodes.Spam);
        var state = new AppState();
        state.ModerationCases.Add(item);
        using var reasonService = new SequencedReviewReasonService();
        var sut = new ModerationQueueViewModel(
            state,
            executor: null!,
            feedbackService: null!,
            coordinator: null!,
            configuration: null!,
            policyStore: null!,
            chatReplyExecutor: null!,
            reviewReasonService: reasonService);

        var firstSelection = sut.SelectViolationRuleAsync(
            item,
            ModerationRuleCodes.Begging);
        var firstRequest = await reasonService.WaitForNextRequestAsync();
        var secondSelection = sut.SelectViolationRuleAsync(
            item,
            ModerationRuleCodes.Spam);
        var secondRequest = await reasonService.WaitForNextRequestAsync();

        Assert.Equal(ModerationRuleCodes.Begging, firstRequest.RuleCode);
        Assert.Equal(ModerationRuleCodes.Spam, secondRequest.RuleCode);

        reasonService.Complete(
            callIndex: 1,
            new ReviewReasonResult(
                ModerationRuleCodes.Spam,
                "Wiederholte störende Nachrichten im Chat.",
                AiLatencyMilliseconds: 5,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null));
        await secondSelection;
        reasonService.Complete(
            callIndex: 0,
            new ReviewReasonResult(
                ModerationRuleCodes.Begging,
                "Wiederholtes Betteln nach Vorteilen.",
                AiLatencyMilliseconds: 5,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null));
        await firstSelection;

        Assert.Equal(ModerationRuleCodes.Spam, item.RuleCode);
        Assert.Equal("Wiederholte störende Nachrichten im Chat.", item.Reason);
        Assert.False(item.IsReasonGenerating);
    }

    private static ModerationCaseItem CreateCase(
        bool isChatReply = false,
        bool isAutoModHeld = false) =>
        new()
        {
            CaseId = 1,
            IsChatReply = isChatReply,
            IsAutoModHeld = isAutoModHeld,
            ChannelId = "channel-1",
            UserName = "viewer",
            OriginalMessage = "test",
            CategoryName = "Just Chatting",
            CategoryId = "509658",
            ReceivedAt = new DateTimeOffset(
                2026,
                7,
                31,
                10,
                0,
                0,
                TimeSpan.Zero),
            MessageId = "message-1",
            UserId = "user-1",
            IdentityFingerprint = "fingerprint-1",
            RowVersion = 1,
            Action = "timeout",
            TimeoutSeconds = 600,
            Reason = "Bestehender Grund.",
            PolicySet = "General",
            Status = "Pending",
        };

    private static ViolationCategoryOption SpamOption() =>
        new(
            "General",
            ModerationRuleCodes.Spam,
            "Spam",
            "Wiederholter Spam.",
            "timeout",
            120);

    private static ViolationCategoryOption InsultOption() =>
        new(
            "General",
            "G_INSULT",
            "Beleidigung",
            "Beleidigung eines Nutzers oder Streamers.",
            "timeout",
            600);

    private static ViolationCategoryOption BeggingOption() =>
        new(
            "General",
            ModerationRuleCodes.Begging,
            "Gebettel",
            "Betteln nach Subs oder Giveaways.",
            "timeout",
            86_400);

    private sealed class BlockingReviewReasonService : IReviewReasonService
    {
        private readonly TaskCompletionSource<ReviewReasonResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReviewReasonRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ReviewReasonResult> GenerateAsync(
            ReviewReasonRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(request);
            return new(_completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete(ReviewReasonResult result) =>
            _completion.TrySetResult(result);
    }

    private sealed class SequencedReviewReasonService : IReviewReasonService, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<PendingReasonRun> _calls = [];
        private readonly SemaphoreSlim _started = new(0);

        public ValueTask<ReviewReasonResult> GenerateAsync(
            ReviewReasonRequest request,
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource<ReviewReasonResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _calls.Add(new PendingReasonRun(request, completion));
            }

            _started.Release();
            // Deliberately ignore cancellation to model a late Lokale AI response.
            return new(completion.Task);
        }

        public async Task<ReviewReasonRequest> WaitForNextRequestAsync()
        {
            if (!await _started.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("No review-reason request was started.");
            }

            lock (_gate)
            {
                return _calls[^1].Request;
            }
        }

        public void Complete(int callIndex, ReviewReasonResult result)
        {
            TaskCompletionSource<ReviewReasonResult> completion;
            lock (_gate)
            {
                completion = _calls[callIndex].Completion;
            }

            completion.TrySetResult(result);
        }

        public void Dispose() => _started.Dispose();

        private sealed record PendingReasonRun(
            ReviewReasonRequest Request,
            TaskCompletionSource<ReviewReasonResult> Completion);
    }
}
