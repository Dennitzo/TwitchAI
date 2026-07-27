using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationBatcherTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enqueue_TwentiethMessageSealsImmediately()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");

        for (var index = 1; index < 20; index++)
        {
            var result = sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddMilliseconds(index));
            Assert.Equal(BatchIngressDisposition.Accepted, result.Disposition);
            Assert.Empty(result.NewlySealedBatches);
        }

        var twentieth = sut.Enqueue(
            Message(20, context),
            context,
            Baseline.AddSeconds(1));

        Assert.Equal(BatchIngressDisposition.AcceptedAndSealed, twentieth.Disposition);
        var batch = Assert.Single(twentieth.NewlySealedBatches);
        Assert.Equal(20, batch.Messages.Count);
        Assert.Equal(BatchSealReason.MessageLimit, batch.SealReason);
        Assert.Null(sut.NextDeadlineUtc);
    }

    [Fact]
    public void SealIfDue_UsesFixedDeadlineFromFirstMessage()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");
        sut.Enqueue(Message(1, context), context, Baseline);
        sut.Enqueue(
            Message(2, context),
            context,
            Baseline.AddSeconds(9).AddMilliseconds(999));

        Assert.Equal(Baseline.AddSeconds(10), sut.NextDeadlineUtc);
        Assert.Null(sut.SealIfDue(Baseline.AddSeconds(10).AddTicks(-1)));

        var batch = sut.SealIfDue(Baseline.AddSeconds(10));

        Assert.NotNull(batch);
        Assert.Equal(2, batch.Messages.Count);
        Assert.Equal(Baseline, batch.OpenedAtUtc);
        Assert.Equal(BatchSealReason.Deadline, batch.SealReason);
    }

    [Fact]
    public void Flush_WhenEmptyNeverCreatesBatch()
    {
        var sut = new ModerationBatcher();

        Assert.Null(sut.Flush(Baseline));
        Assert.False(sut.TryDequeue(out var batch));
        Assert.Null(batch);
        Assert.Equal(0, sut.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_CategoryChangeSealsOldCategoryBeforeAcceptingNewMessage()
    {
        var sut = new ModerationBatcher();
        var justChatting = Context("509658", "general-v1");
        var gta = Context("32982", "general-backseat-v1");
        sut.Enqueue(Message(1, justChatting), justChatting, Baseline);

        var result = sut.Enqueue(
            Message(2, gta),
            gta,
            Baseline.AddSeconds(1));
        var oldBatch = Assert.Single(result.NewlySealedBatches);
        var newBatch = sut.Flush(Baseline.AddSeconds(2));

        Assert.Equal(BatchSealReason.CategoryChanged, oldBatch.SealReason);
        Assert.Equal("509658", oldBatch.Context.Channel.CategoryId);
        Assert.All(
            oldBatch.Messages,
            item => Assert.Equal("509658", item.Message.ChannelContext.CategoryId));
        Assert.NotNull(newBatch);
        Assert.Equal("32982", newBatch.Context.Channel.CategoryId);
        Assert.All(
            newBatch.Messages,
            item => Assert.Equal("32982", item.Message.ChannelContext.CategoryId));
    }

    [Fact]
    public void Enqueue_PolicyRevisionChangeSealsOldRevision()
    {
        var sut = new ModerationBatcher();
        var revisionOne = Context("32982", "policy-v1");
        var revisionTwo = Context("32982", "policy-v2");
        sut.Enqueue(Message(1, revisionOne), revisionOne, Baseline);

        var result = sut.Enqueue(
            Message(2, revisionTwo),
            revisionTwo,
            Baseline.AddSeconds(1));

        var batch = Assert.Single(result.NewlySealedBatches);
        Assert.Equal(BatchSealReason.PolicyRevisionChanged, batch.SealReason);
        Assert.Equal("policy-v1", batch.Context.EffectivePolicy.RevisionFingerprint);
        Assert.Equal("policy-v2", sut.Flush(Baseline.AddSeconds(2))?
            .Context.EffectivePolicy.RevisionFingerprint);
    }

    [Fact]
    public void Enqueue_CharacterBudgetSealsBeforeOverflowingMessage()
    {
        var options = new ModerationBatcherOptions(
            MaximumMessages: 20,
            MaximumCollectionDelay: TimeSpan.FromSeconds(10),
            MaximumMessageCharacters: 12,
            MaximumQueuedMessages: 500);
        var sut = new ModerationBatcher(options);
        var context = Context("509658", "policy");
        sut.Enqueue(Message(1, context, "1234567"), context, Baseline);

        var result = sut.Enqueue(
            Message(2, context, "123"),
            context,
            Baseline.AddSeconds(1));

        var sealedBatch = Assert.Single(result.NewlySealedBatches);
        Assert.Equal(BatchSealReason.CharacterLimit, sealedBatch.SealReason);
        Assert.Single(sealedBatch.Messages);
        Assert.Single(sut.Flush(Baseline.AddSeconds(2))?.Messages ?? []);
    }

    [Fact]
    public void Enqueue_AtQueueLimitDropsOldestWaitingMessage()
    {
        var options = new ModerationBatcherOptions(
            MaximumMessages: 2,
            MaximumCollectionDelay: TimeSpan.FromSeconds(10),
            MaximumMessageCharacters: 6_000,
            MaximumQueuedMessages: 4);
        var sut = new ModerationBatcher(options);
        var context = Context("509658", "policy");

        for (var index = 1; index <= 4; index++)
        {
            sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddMilliseconds(index));
        }

        var overflow = sut.Enqueue(
            Message(5, context),
            context,
            Baseline.AddSeconds(1));

        Assert.Equal(1, overflow.DroppedOldestMessages);
        Assert.Equal(4, sut.WaitingMessageCount);
        Assert.True(sut.TryDequeue(out var oldestRemaining));
        Assert.NotNull(oldestRemaining);
        Assert.Equal(["m2"], oldestRemaining.Messages.Select(item => item.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_DefaultQueueLimitIsExactlyFiveHundredMessages()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "policy");

        for (var index = 1; index <= 500; index++)
        {
            var result = sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddMilliseconds(index));
            Assert.Equal(0, result.DroppedOldestMessages);
        }

        var overflow = sut.Enqueue(
            Message(501, context),
            context,
            Baseline.AddSeconds(1));

        Assert.Equal(1, overflow.DroppedOldestMessages);
        Assert.Equal(500, sut.WaitingMessageCount);
    }

    private static ModerationBatchContext Context(
        string categoryId,
        string policyFingerprint)
    {
        var channel = new ChannelContextSnapshot(
            "channel-1",
            categoryId,
            categoryId == "509658" ? "Just Chatting" : "Grand Theft Auto V",
            Baseline,
            Revision: 1,
            IsVerified: true);
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            policyFingerprint);
        return new ModerationBatchContext(channel, policy);
    }

    private static ModerationBatchMessage Message(
        int index,
        ModerationBatchContext context,
        string text = "hello")
    {
        var identity = ChatMessageIdentityV1.Create(
            $"event-{index}",
            $"twitch-message-{index}",
            "channel-1",
            $"user-{index}",
            $"user-{index}",
            $"User {index}",
            text,
            Baseline.AddMilliseconds(index));
        var snapshot = new ChatMessageSnapshot(
            identity,
            new ChatTextNormalizer().Process(text),
            context.Channel,
            IngressSequence: index);
        return new ModerationBatchMessage($"m{index}", snapshot, []);
    }
}
