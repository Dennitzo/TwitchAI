using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreModerationCaseRepositoryDuplicateTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameUserAndCanonicalTextCreatesOnlyOneActiveCase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory);
        var first = Message(
            index: 1,
            userId: "user-1",
            originalText: "Bitte   aufhören");
        var duplicate = Message(
            index: 2,
            userId: "user-1",
            originalText: "BITTE AUFHÖREN");

        var firstResult = await AddAsync(repository, first);
        var duplicateResult = await AddAsync(repository, duplicate);

        Assert.True(firstResult.IsCreated);
        Assert.False(duplicateResult.IsCreated);
        Assert.Equal(firstResult.CaseId, duplicateResult.CaseId);
        Assert.Equal((1L, 1L), await CountCasesAndSnapshotsAsync(database));
        var persisted = await repository.GetAsync(firstResult.CaseId);
        Assert.Equal(first.Identity.TwitchMessageId, persisted?.Message.Identity.TwitchMessageId);
    }

    [Fact]
    public async Task DifferentUserOrCanonicalTextCreatesSeparateCases()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory);

        var original = await AddAsync(
            repository,
            Message(index: 1, userId: "user-1", originalText: "gleicher text"));
        var otherUser = await AddAsync(
            repository,
            Message(index: 2, userId: "user-2", originalText: "GLEICHER TEXT"));
        var otherText = await AddAsync(
            repository,
            Message(index: 3, userId: "user-1", originalText: "anderer text"));

        Assert.All(
            [original, otherUser, otherText],
            result => Assert.True(result.IsCreated));
        Assert.Equal(3, new[]
        {
            original.CaseId,
            otherUser.CaseId,
            otherText.CaseId,
        }.Distinct().Count());
        Assert.Equal((3L, 3L), await CountCasesAndSnapshotsAsync(database));
    }

    [Fact]
    public async Task ParallelDuplicateAddsAreSerializedAtomically()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory);
        var tasks = Enumerable
            .Range(1, 16)
            .Select(index => AddAsync(
                    repository,
                    Message(
                        index,
                        userId: "parallel-user",
                        originalText: index % 2 == 0
                            ? "PARALLEL   MESSAGE"
                            : "parallel message"))
                .AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Single(results, result => result.IsCreated);
        Assert.Single(results.Select(result => result.CaseId).Distinct());
        Assert.Equal((1L, 1L), await CountCasesAndSnapshotsAsync(database));
    }

    [Fact]
    public async Task TerminalCaseReleasesDuplicateKeyForLaterReview()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory);
        var first = await AddAsync(
            repository,
            Message(index: 1, userId: "user-1", originalText: "wiederholung"));
        Assert.True(
            await repository.TryTransitionAsync(
                first.CaseId,
                expectedRowVersion: 1,
                CoreModerationCaseStatus.Pending,
                CoreModerationCaseStatus.Discarded));

        var later = await AddAsync(
            repository,
            Message(index: 2, userId: "user-1", originalText: "WIEDERHOLUNG"));

        Assert.True(later.IsCreated);
        Assert.NotEqual(first.CaseId, later.CaseId);
        Assert.Equal((2L, 2L), await CountCasesAndSnapshotsAsync(database));
    }

    [Fact]
    public async Task OutcomeUnknownKeepsDuplicateKeyProtected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory);
        var first = await AddAsync(
            repository,
            Message(index: 1, userId: "user-1", originalText: "unklares ergebnis"));
        Assert.True(
            await repository.TryTransitionAsync(
                first.CaseId,
                expectedRowVersion: 1,
                CoreModerationCaseStatus.Pending,
                CoreModerationCaseStatus.OutcomeUnknown));

        var later = await AddAsync(
            repository,
            Message(index: 2, userId: "user-1", originalText: "UNKLARES ERGEBNIS"));

        Assert.False(later.IsCreated);
        Assert.Equal(first.CaseId, later.CaseId);
        Assert.Equal((1L, 1L), await CountCasesAndSnapshotsAsync(database));
    }

    private static ValueTask<ModerationCaseAddResult> AddAsync(
        CoreModerationCaseRepository repository,
        ChatMessageSnapshot message)
    {
        return repository.AddIfNoActiveDuplicateAsync(
            message,
            Decision(),
            policyRevisionFingerprint: "policy-v1",
            ModerationCaseCreationMetadata.Empty);
    }

    private static ValidatedModerationDecision Decision()
    {
        return new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            TimeoutSeconds: 600,
            PolicySetCode.General,
            "Sonstiger Verstoß gegen die Chatregeln.",
            ["test"],
            Confidence: 0.9,
            RuleCode: ModerationRuleCodes.GeneralPhrase);
    }

    private static ChatMessageSnapshot Message(
        int index,
        string userId,
        string originalText)
    {
        const string channelId = "channel-1";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{index}",
            $"message-{index}",
            channelId,
            userId,
            $"login-{userId}",
            $"User {userId}",
            originalText,
            Baseline.AddMilliseconds(index));
        var context = new ChannelContextSnapshot(
            channelId,
            EffectivePolicyResolver.BootstrapJustChattingCategoryId,
            "Just Chatting",
            Baseline,
            Revision: 1,
            IsVerified: true);
        return new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(originalText),
            context,
            IngressSequence: index);
    }

    private static async Task<(long Cases, long Snapshots)> CountCasesAndSnapshotsAsync(
        TestDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM moderation_cases),
                (SELECT COUNT(*) FROM chat_message_snapshots);
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
