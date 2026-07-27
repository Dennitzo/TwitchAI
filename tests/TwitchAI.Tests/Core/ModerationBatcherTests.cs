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

    [Fact]
    public void Enqueue_CompatibleDeadlineCyclesBehindRunningBatchConsolidateToTwenty()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");

        for (var index = 1; index <= 20; index++)
        {
            sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddMilliseconds(index));
        }

        Assert.True(sut.TryDequeue(out var runningBatch));
        Assert.NotNull(runningBatch);
        Assert.Equal(20, runningBatch.Messages.Count);
        Assert.Equal(0, sut.WaitingMessageCount);

        var firstCycleOpenedAt = Baseline.AddSeconds(1);
        for (var index = 21; index <= 24; index++)
        {
            sut.Enqueue(
                Message(index, context),
                context,
                firstCycleOpenedAt.AddMilliseconds(index - 21));
        }

        var waitingTail = sut.SealIfDue(firstCycleOpenedAt.AddSeconds(10));
        Assert.NotNull(waitingTail);
        Assert.Equal(4, waitingTail.Messages.Count);
        var waitingBatchId = waitingTail.BatchId;

        BatchIngressResult? latestUpdate = null;
        for (var cycle = 1; cycle <= 4; cycle++)
        {
            var cycleOpenedAt = firstCycleOpenedAt.AddSeconds(cycle * 10);
            var firstMessageIndex = 21 + (cycle * 4);
            for (var offset = 0; offset < 4; offset++)
            {
                latestUpdate = sut.Enqueue(
                    Message(firstMessageIndex + offset, context),
                    context,
                    cycleOpenedAt.AddMilliseconds(offset));

                var updatedTail = Assert.Single(latestUpdate.UpdatedQueuedBatches);
                Assert.Equal(waitingBatchId, updatedTail.BatchId);
                Assert.Empty(latestUpdate.NewlySealedBatches);
            }

            Assert.Null(sut.SealIfDue(cycleOpenedAt.AddSeconds(10)));
        }

        Assert.NotNull(latestUpdate);
        Assert.Equal(
            20,
            Assert.Single(latestUpdate.UpdatedQueuedBatches).Messages.Count);
        Assert.Equal(20, sut.WaitingMessageCount);
        Assert.Null(sut.NextDeadlineUtc);

        Assert.True(sut.TryDequeue(out var consolidated));
        Assert.NotNull(consolidated);
        Assert.Equal(waitingBatchId, consolidated.BatchId);
        Assert.Equal(
            Enumerable.Range(21, 20).Select(index => $"m{index}"),
            consolidated.Messages.Select(message => message.TemporaryMessageId));
        Assert.Equal(0, sut.WaitingMessageCount);
        Assert.False(sut.TryDequeue(out _));
    }

    [Fact]
    public void Enqueue_CompatibleOverflowFillsSeventeenMessageTailThenKeepsFive()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");

        for (var index = 1; index <= 17; index++)
        {
            sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddMilliseconds(index));
        }

        var waitingTail = sut.SealIfDue(
            Baseline.AddSeconds(10).AddMilliseconds(1));
        Assert.NotNull(waitingTail);
        var waitingBatchId = waitingTail.BatchId;

        for (var index = 18; index <= 25; index++)
        {
            var result = sut.Enqueue(
                Message(index, context),
                context,
                Baseline.AddSeconds(11).AddMilliseconds(index));

            if (index <= 20)
            {
                var updatedTail = Assert.Single(result.UpdatedQueuedBatches);
                Assert.Equal(waitingBatchId, updatedTail.BatchId);
                Assert.Equal(index, updatedTail.Messages.Count);
                Assert.Empty(result.NewlySealedBatches);
            }
            else
            {
                Assert.Empty(result.UpdatedQueuedBatches);
            }
        }

        Assert.Equal(25, sut.WaitingMessageCount);
        var remainder = sut.SealIfDue(Baseline.AddSeconds(21).AddMilliseconds(21));
        Assert.NotNull(remainder);
        Assert.Equal(5, remainder.Messages.Count);

        Assert.True(sut.TryDequeue(out var fullBatch));
        Assert.True(sut.TryDequeue(out var remainderBatch));
        Assert.NotNull(fullBatch);
        Assert.NotNull(remainderBatch);
        Assert.Equal(waitingBatchId, fullBatch.BatchId);
        Assert.Equal(20, fullBatch.Messages.Count);
        Assert.Equal(5, remainderBatch.Messages.Count);
        Assert.Equal(
            Enumerable.Range(1, 25).Select(index => $"m{index}"),
            fullBatch.Messages
                .Concat(remainderBatch.Messages)
                .Select(message => message.TemporaryMessageId));
        Assert.Equal(0, sut.WaitingMessageCount);
        Assert.False(sut.TryDequeue(out _));
    }

    [Fact]
    public void Enqueue_DifferentCategoriesNeverMergeAcrossQueuedTail()
    {
        var sut = new ModerationBatcher();
        var justChatting = Context("509658", "general-v1");
        var game = Context("32982", "general-backseat-v1");

        EnqueuePair(sut, 1, justChatting, Baseline);
        var firstJustChatting = sut.SealIfDue(Baseline.AddSeconds(10));
        Assert.NotNull(firstJustChatting);

        var firstGameResult = sut.Enqueue(
            Message(3, game),
            game,
            Baseline.AddSeconds(11));
        Assert.Empty(firstGameResult.UpdatedQueuedBatches);
        sut.Enqueue(Message(4, game), game, Baseline.AddSeconds(11.1));
        Assert.NotNull(sut.SealIfDue(Baseline.AddSeconds(21)));

        var secondJustChattingResult = sut.Enqueue(
            Message(5, justChatting),
            justChatting,
            Baseline.AddSeconds(22));
        Assert.Empty(secondJustChattingResult.UpdatedQueuedBatches);
        sut.Enqueue(
            Message(6, justChatting),
            justChatting,
            Baseline.AddSeconds(22.1));
        Assert.NotNull(sut.SealIfDue(Baseline.AddSeconds(32)));

        var batches = DequeueAll(sut);
        Assert.Equal(3, batches.Count);
        Assert.Equal(
            ["509658", "32982", "509658"],
            batches.Select(batch => batch.Context.Channel.CategoryId));
        Assert.All(batches, batch => Assert.Equal(2, batch.Messages.Count));
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $"m{index}"),
            batches
                .SelectMany(batch => batch.Messages)
                .Select(message => message.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_DifferentPolicyRevisionsNeverMergeAcrossQueuedTail()
    {
        var sut = new ModerationBatcher();
        var revisionOne = Context("509658", "general-v1");
        var revisionTwo = Context("509658", "general-v2");

        EnqueuePair(sut, 1, revisionOne, Baseline);
        Assert.NotNull(sut.SealIfDue(Baseline.AddSeconds(10)));

        var firstRevisionTwoResult = sut.Enqueue(
            Message(3, revisionTwo),
            revisionTwo,
            Baseline.AddSeconds(11));
        Assert.Empty(firstRevisionTwoResult.UpdatedQueuedBatches);
        sut.Enqueue(
            Message(4, revisionTwo),
            revisionTwo,
            Baseline.AddSeconds(11.1));
        Assert.NotNull(sut.SealIfDue(Baseline.AddSeconds(21)));

        var secondRevisionOneResult = sut.Enqueue(
            Message(5, revisionOne),
            revisionOne,
            Baseline.AddSeconds(22));
        Assert.Empty(secondRevisionOneResult.UpdatedQueuedBatches);
        sut.Enqueue(
            Message(6, revisionOne),
            revisionOne,
            Baseline.AddSeconds(22.1));
        Assert.NotNull(sut.SealIfDue(Baseline.AddSeconds(32)));

        var batches = DequeueAll(sut);
        Assert.Equal(3, batches.Count);
        Assert.Equal(
            ["general-v1", "general-v2", "general-v1"],
            batches.Select(
                batch => batch.Context.EffectivePolicy.RevisionFingerprint));
        Assert.All(batches, batch => Assert.Equal(2, batch.Messages.Count));
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $"m{index}"),
            batches
                .SelectMany(batch => batch.Messages)
                .Select(message => message.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_QueuedTailConsolidationRespectsCharacterLimitAndFifo()
    {
        var options = new ModerationBatcherOptions(
            MaximumMessages: 20,
            MaximumCollectionDelay: TimeSpan.FromSeconds(10),
            MaximumMessageCharacters: 30,
            MaximumQueuedMessages: 500);
        var sut = new ModerationBatcher(options);
        var context = Context("509658", "general-v1");

        sut.Enqueue(Message(1, context, "12345678"), context, Baseline);
        sut.Enqueue(
            Message(2, context, "abcdefgh"),
            context,
            Baseline.AddMilliseconds(1));
        var waitingTail = sut.SealIfDue(Baseline.AddSeconds(10));
        Assert.NotNull(waitingTail);
        Assert.Equal(20, waitingTail.Messages.Sum(MessageCharacters));

        var fitting = sut.Enqueue(
            Message(3, context, "123456"),
            context,
            Baseline.AddSeconds(11));
        var updatedTail = Assert.Single(fitting.UpdatedQueuedBatches);
        Assert.Equal(waitingTail.BatchId, updatedTail.BatchId);
        Assert.Equal(28, updatedTail.Messages.Sum(MessageCharacters));

        var overflowing = sut.Enqueue(
            Message(4, context, "x"),
            context,
            Baseline.AddSeconds(12));
        Assert.Empty(overflowing.UpdatedQueuedBatches);
        Assert.Equal(Baseline.AddSeconds(22), sut.NextDeadlineUtc);
        var remainder = sut.SealIfDue(Baseline.AddSeconds(22));
        Assert.NotNull(remainder);

        var batches = DequeueAll(sut);
        Assert.Equal(2, batches.Count);
        Assert.Equal([3, 1], batches.Select(batch => batch.Messages.Count));
        Assert.All(
            batches,
            batch => Assert.InRange(
                batch.Messages.Sum(MessageCharacters),
                1,
                options.MaximumMessageCharacters));
        Assert.Equal(
            ["m1", "m2", "m3", "m4"],
            batches
                .SelectMany(batch => batch.Messages)
                .Select(message => message.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_IdenticalLowercaseMessageAcrossUsersIsSuppressed()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");

        var first = sut.Enqueue(
            Message(1, context, "Gemeinsam SPAMMEN"),
            context,
            Baseline);
        var duplicate = sut.Enqueue(
            Message(2, context, "gemeinsam spammen"),
            context,
            Baseline.AddSeconds(1));

        Assert.Equal(BatchIngressDisposition.Accepted, first.Disposition);
        Assert.Equal(
            BatchIngressDisposition.DuplicateSuppressed,
            duplicate.Disposition);
        Assert.Equal("m1", duplicate.DuplicateOfTemporaryMessageId);
        Assert.Empty(duplicate.NewlySealedBatches);
        Assert.Empty(duplicate.DroppedMessages);
        Assert.Equal(1, sut.WaitingMessageCount);
        Assert.Equal(Baseline.AddSeconds(10), duplicate.NextDeadlineUtc);
        Assert.Equal(
            ["m1"],
            sut.Flush(Baseline.AddSeconds(2))?
                .Messages
                .Select(message => message.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_DeduplicationUsesExactLowerText()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");

        sut.Enqueue(Message(1, context, "Spam"), context, Baseline);
        var casingDuplicate = sut.Enqueue(
            Message(2, context, "spam"),
            context,
            Baseline.AddMilliseconds(1));
        var leadingSpace = sut.Enqueue(
            Message(3, context, " spam"),
            context,
            Baseline.AddMilliseconds(2));
        var punctuation = sut.Enqueue(
            Message(4, context, "spam!"),
            context,
            Baseline.AddMilliseconds(3));

        Assert.Equal(
            BatchIngressDisposition.DuplicateSuppressed,
            casingDuplicate.Disposition);
        Assert.Equal(BatchIngressDisposition.Accepted, leadingSpace.Disposition);
        Assert.Equal(BatchIngressDisposition.Accepted, punctuation.Disposition);
        Assert.Equal(3, sut.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_DuplicateInAnyReadyBatchIsSuppressedButRunningIsExcluded()
    {
        var sut = new ModerationBatcher();
        var justChatting = Context("509658", "general-v1");
        var game = Context("32982", "general-backseat-v1");

        sut.Enqueue(
            Message(1, justChatting, "same queued text"),
            justChatting,
            Baseline);
        var firstBatch = sut.Flush(Baseline.AddSeconds(1));
        Assert.NotNull(firstBatch);

        sut.Enqueue(
            Message(2, game, "different context"),
            game,
            Baseline.AddSeconds(2));
        Assert.NotNull(sut.Flush(Baseline.AddSeconds(3)));

        var whileWaiting = sut.Enqueue(
            Message(3, justChatting, "SAME QUEUED TEXT"),
            justChatting,
            Baseline.AddSeconds(4));

        Assert.Equal(
            BatchIngressDisposition.DuplicateSuppressed,
            whileWaiting.Disposition);
        Assert.Equal("m1", whileWaiting.DuplicateOfTemporaryMessageId);

        Assert.True(sut.TryDequeue(out var running));
        Assert.Equal(firstBatch.BatchId, running?.BatchId);

        var whileFirstBatchIsRunning = sut.Enqueue(
            Message(4, justChatting, "same queued text"),
            justChatting,
            Baseline.AddSeconds(5));

        Assert.Equal(
            BatchIngressDisposition.Accepted,
            whileFirstBatchIsRunning.Disposition);
        Assert.Equal(2, sut.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_IdenticalTextInDifferentContextsIsNotSuppressed()
    {
        var sut = new ModerationBatcher();
        var justChatting = Context("509658", "general-v1");
        var game = Context("32982", "general-backseat-v1");
        var newGamePolicy = Context("32982", "general-backseat-v2");
        var otherChannel = Context(
            "32982",
            "general-backseat-v2",
            channelId: "channel-2");

        sut.Enqueue(
            Message(1, justChatting, "same text"),
            justChatting,
            Baseline);
        var categoryChanged = sut.Enqueue(
            Message(2, game, "same text"),
            game,
            Baseline.AddSeconds(1));
        var policyChanged = sut.Enqueue(
            Message(3, newGamePolicy, "same text"),
            newGamePolicy,
            Baseline.AddSeconds(2));
        var channelChanged = sut.Enqueue(
            Message(4, otherChannel, "same text"),
            otherChannel,
            Baseline.AddSeconds(3));

        Assert.NotEqual(
            BatchIngressDisposition.DuplicateSuppressed,
            categoryChanged.Disposition);
        Assert.NotEqual(
            BatchIngressDisposition.DuplicateSuppressed,
            policyChanged.Disposition);
        Assert.NotEqual(
            BatchIngressDisposition.DuplicateSuppressed,
            channelChanged.Disposition);
        Assert.Equal(4, sut.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_ReanalysisIsNeitherSuppressedNorARepresentative()
    {
        var context = Context("509658", "general-v1");
        var reanalysis = Message(1, context, "same text") with
        {
            ReanalysisCaseId = 42,
            ReanalysisCaseRowVersion = 3,
        };

        var reanalysisFirst = new ModerationBatcher();
        reanalysisFirst.Enqueue(reanalysis, context, Baseline);
        var liveAfterReanalysis = reanalysisFirst.Enqueue(
            Message(2, context, "SAME TEXT"),
            context,
            Baseline.AddMilliseconds(1));

        var liveFirst = new ModerationBatcher();
        liveFirst.Enqueue(
            Message(3, context, "same text"),
            context,
            Baseline);
        var reanalysisAfterLive = liveFirst.Enqueue(
            reanalysis with { TemporaryMessageId = "m4" },
            context,
            Baseline.AddMilliseconds(1));

        Assert.Equal(
            BatchIngressDisposition.Accepted,
            liveAfterReanalysis.Disposition);
        Assert.Equal(
            BatchIngressDisposition.Accepted,
            reanalysisAfterLive.Disposition);
        Assert.Equal(2, reanalysisFirst.WaitingMessageCount);
        Assert.Equal(2, liveFirst.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_RuntimeSettingAppliesImmediately()
    {
        var runtimeSettings = new ModerationQueueRuntimeSettings(
            removeIdenticalMessages: false);
        var sut = new ModerationBatcher(runtimeSettings);
        var context = Context("509658", "general-v1");

        sut.Enqueue(Message(1, context, "spam"), context, Baseline);
        var allowedWhileDisabled = sut.Enqueue(
            Message(2, context, "SPAM"),
            context,
            Baseline.AddMilliseconds(1));

        runtimeSettings.RemoveIdenticalMessages = true;
        var suppressedWhileEnabled = sut.Enqueue(
            Message(3, context, "spam"),
            context,
            Baseline.AddMilliseconds(2));

        runtimeSettings.RemoveIdenticalMessages = false;
        var allowedAgain = sut.Enqueue(
            Message(4, context, "spam"),
            context,
            Baseline.AddMilliseconds(3));

        Assert.Equal(
            BatchIngressDisposition.Accepted,
            allowedWhileDisabled.Disposition);
        Assert.Equal(
            BatchIngressDisposition.DuplicateSuppressed,
            suppressedWhileEnabled.Disposition);
        Assert.Equal(
            BatchIngressDisposition.Accepted,
            allowedAgain.Disposition);
        Assert.Equal(3, sut.WaitingMessageCount);
    }

    [Fact]
    public void Enqueue_DuplicateAtQueueLimitDoesNotEvictOldestMessage()
    {
        var options = new ModerationBatcherOptions(
            MaximumMessages: 2,
            MaximumCollectionDelay: TimeSpan.FromSeconds(10),
            MaximumMessageCharacters: 6_000,
            MaximumQueuedMessages: 2);
        var sut = new ModerationBatcher(options);
        var context = Context("509658", "general-v1");

        sut.Enqueue(Message(1, context, "spam"), context, Baseline);
        sut.Enqueue(
            Message(2, context, "keep me"),
            context,
            Baseline.AddMilliseconds(1));
        var duplicate = sut.Enqueue(
            Message(3, context, "SPAM"),
            context,
            Baseline.AddMilliseconds(2));

        Assert.Equal(
            BatchIngressDisposition.DuplicateSuppressed,
            duplicate.Disposition);
        Assert.Equal(0, duplicate.DroppedOldestMessages);
        Assert.Empty(duplicate.DroppedMessages);
        Assert.Equal(2, sut.WaitingMessageCount);
        Assert.True(sut.TryDequeue(out var batch));
        Assert.Equal(
            ["m1", "m2"],
            batch?.Messages.Select(message => message.TemporaryMessageId));
    }

    [Fact]
    public void Enqueue_DuplicatesDoNotCountTowardTwentyMessageLimit()
    {
        var sut = new ModerationBatcher();
        var context = Context("509658", "general-v1");
        ModerationBatch? sealedBatch = null;

        for (var index = 1; index <= 20; index++)
        {
            var unique = sut.Enqueue(
                Message((index * 2) - 1, context, $"unique-{index}"),
                context,
                Baseline.AddMilliseconds(index * 2));
            var duplicate = sut.Enqueue(
                Message(index * 2, context, $"UNIQUE-{index}"),
                context,
                Baseline.AddMilliseconds((index * 2) + 1));

            Assert.Equal(
                BatchIngressDisposition.DuplicateSuppressed,
                duplicate.Disposition);
            if (index < 20)
            {
                Assert.Empty(unique.NewlySealedBatches);
            }
            else
            {
                sealedBatch = Assert.Single(unique.NewlySealedBatches);
            }
        }

        Assert.NotNull(sealedBatch);
        Assert.Equal(20, sealedBatch.Messages.Count);
        Assert.Equal(20, sut.WaitingMessageCount);
    }

    [Fact]
    public void RuntimeSettings_DefaultsToRemovingIdenticalMessages()
    {
        var sut = new ModerationQueueRuntimeSettings();

        Assert.True(sut.RemoveIdenticalMessages);
    }

    private static void EnqueuePair(
        ModerationBatcher sut,
        int firstIndex,
        ModerationBatchContext context,
        DateTimeOffset openedAtUtc)
    {
        sut.Enqueue(Message(firstIndex, context), context, openedAtUtc);
        sut.Enqueue(
            Message(firstIndex + 1, context),
            context,
            openedAtUtc.AddMilliseconds(1));
    }

    private static List<ModerationBatch> DequeueAll(ModerationBatcher sut)
    {
        var batches = new List<ModerationBatch>();
        while (sut.TryDequeue(out var batch))
        {
            Assert.NotNull(batch);
            batches.Add(batch);
        }

        return batches;
    }

    private static int MessageCharacters(ModerationBatchMessage message) =>
        message.PromptCharacterCount;

    private static ModerationBatchContext Context(
        string categoryId,
        string policyFingerprint,
        string channelId = "channel-1")
    {
        var channel = new ChannelContextSnapshot(
            channelId,
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
        string? text = null)
    {
        text ??= $"hello-{index}";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{index}",
            $"twitch-message-{index}",
            context.Channel.ChannelId,
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
