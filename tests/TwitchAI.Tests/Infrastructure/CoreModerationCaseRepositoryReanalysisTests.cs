using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreModerationCaseRepositoryReanalysisTests
{
    [Theory]
    [InlineData(true, CoreModerationCaseStatus.Allowlisted)]
    [InlineData(false, CoreModerationCaseStatus.Discarded)]
    public async Task CompleteWithoutAction_UsesRowVersionAndCreatesNoFeedback(
        bool wasWhitelisted,
        CoreModerationCaseStatus expectedStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer());
        var message = Message(
            $"reanalysis-{wasWhitelisted.ToString().ToLowerInvariant()}");
        var caseId = await repository.AddAsync(
            message,
            new ValidatedModerationDecision(
                "m1",
                ModerationActionKind.Timeout,
                TimeoutSeconds: 300,
                PolicySetCode.General,
                "stale proposal",
                ["stale"],
                Confidence: 0.8,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            "old-policy");
        Assert.True(
            await repository.TryTransitionAsync(
                caseId,
                expectedRowVersion: 1,
                CoreModerationCaseStatus.Pending,
                CoreModerationCaseStatus.StalePolicy));

        var stale = await repository.GetAsync(caseId);
        Assert.NotNull(stale);
        Assert.Equal(2, stale.RowVersion);

        Assert.False(
            await repository.TryCompleteReanalysisWithoutActionAsync(
                caseId,
                expectedRowVersion: 1,
                wasWhitelisted));
        var afterWrongVersion = await repository.GetAsync(caseId);
        Assert.NotNull(afterWrongVersion);
        Assert.Equal(CoreModerationCaseStatus.StalePolicy, afterWrongVersion.Status);
        Assert.Equal(2, afterWrongVersion.RowVersion);

        Assert.True(
            await repository.TryCompleteReanalysisWithoutActionAsync(
                caseId,
                expectedRowVersion: 2,
                wasWhitelisted));
        Assert.False(
            await repository.TryCompleteReanalysisWithoutActionAsync(
                caseId,
                expectedRowVersion: 2,
                wasWhitelisted));

        var completed = await repository.GetAsync(caseId);
        Assert.NotNull(completed);
        Assert.Equal(expectedStatus, completed.Status);
        Assert.Equal(3, completed.RowVersion);
        Assert.Equal(ModerationActionKind.None, completed.ProposedAction);
        Assert.Null(completed.ProposedTimeoutSeconds);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM moderation_feedback
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static ChatMessageSnapshot Message(string suffix)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        const string original = "stale reanalysis";
        var channelId = $"channel-{suffix}";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{suffix}",
            $"message-{suffix}",
            channelId,
            $"user-{suffix}",
            "viewer",
            "Viewer",
            original,
            receivedAt);
        return new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(original),
            new ChannelContextSnapshot(
                channelId,
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                "Just Chatting",
                receivedAt,
                Revision: 1,
                IsVerified: true),
            IngressSequence: 1);
    }
}
