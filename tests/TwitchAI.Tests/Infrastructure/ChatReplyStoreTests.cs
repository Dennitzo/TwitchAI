using System.Globalization;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ChatReplyStoreTests
{
    [Fact]
    public async Task TemplateRoundTrip_UpdatesAndFiltersEnabledRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteChatReplyStore(database.ConnectionFactory);

        var templateId = await store.AddTemplateAsync(
            "Wie lange geht das Event noch?",
            "!event @Username");

        var created = Assert.Single(await store.LoadTemplatesAsync());
        Assert.Equal(templateId, created.TemplateId);
        Assert.Equal(1, created.RowVersion);
        Assert.Equal("Wie lange geht das Event noch?", created.ExampleMessage);
        Assert.Equal("wie lange geht das event noch?", created.NormalizedExample);
        Assert.Equal("!event @Username", created.ResponseMessage);
        Assert.True(created.IsEnabled);
        Assert.Equal(0, created.HitCount);
        Assert.Null(created.LastHitAt);

        var updatedVersion = await store.UpdateTemplateAsync(
            templateId,
            created.RowVersion,
            "Wann endet das Event?",
            "!event-info @Username");
        Assert.Equal(2, updatedVersion);

        var updated = Assert.Single(await store.LoadActiveTemplatesAsync());
        Assert.Equal("Wann endet das Event?", updated.ExampleMessage);
        Assert.Equal("wann endet das event?", updated.NormalizedExample);
        Assert.Equal("!event-info @Username", updated.ResponseMessage);
        Assert.Equal(updatedVersion, updated.RowVersion);

        var disabledVersion = await store.SetTemplateEnabledAsync(
            templateId,
            updated.RowVersion,
            isEnabled: false);
        Assert.Equal(3, disabledVersion);
        Assert.Empty(await store.LoadActiveTemplatesAsync());

        var disabled = Assert.Single(await store.LoadTemplatesAsync());
        Assert.False(disabled.IsEnabled);
        Assert.Equal(disabledVersion, disabled.RowVersion);
    }

    [Theory]
    [InlineData(
        "!event @Username",
        "viewer_name",
        "!event @viewer_name")]
    [InlineData(
        "@Username: !event für @Username",
        "mixed_case",
        "@mixed_case: !event für @mixed_case")]
    [InlineData(
        "!event @username",
        "viewer_name",
        "!event @username")]
    public void RenderResponse_ReplacesOnlyTheDocumentedPlaceholder(
        string configuredResponse,
        string twitchLogin,
        string expected)
    {
        Assert.Equal(
            expected,
            SqliteChatReplyStore.RenderResponse(configuredResponse, twitchLogin));
    }

    [Fact]
    public async Task AddProposal_DeduplicatesSourceMessageAndCountsOneHit()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteChatReplyStore(database.ConnectionFactory);
        var templateId = await store.AddTemplateAsync(
            "Wie lange geht das Event noch?",
            "!event @Username");
        var template = Assert.Single(await store.LoadActiveTemplatesAsync());
        var message = CreateMessage(
            twitchMessageId: "message-1",
            login: "viewer_name",
            originalText: "Wie lange läuft das Event?");
        var rendered = SqliteChatReplyStore.RenderResponse(
            template.ResponseMessage,
            message.Identity.Login);

        var first = await store.AddProposalAsync(
            message,
            template,
            rendered,
            confidence: 0.92,
            batchId: Guid.NewGuid(),
            aiLatencyMilliseconds: 145);
        var duplicate = await store.AddProposalAsync(
            message,
            template,
            rendered,
            confidence: 0.81,
            batchId: Guid.NewGuid(),
            aiLatencyMilliseconds: 200);

        Assert.NotNull(first);
        Assert.True(first.IsCreated);
        Assert.NotNull(duplicate);
        Assert.False(duplicate.IsCreated);
        Assert.Equal(first.Proposal.ProposalId, duplicate.Proposal.ProposalId);
        Assert.Equal(templateId, first.Proposal.TemplateId);
        Assert.Equal("!event @viewer_name", first.Proposal.RenderedResponse);
        Assert.Equal("pending", first.Proposal.Status);

        var afterMatch = Assert.Single(await store.LoadTemplatesAsync());
        Assert.Equal(1, afterMatch.HitCount);
        Assert.NotNull(afterMatch.LastHitAt);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chat_reply_proposals;";
        Assert.Equal(
            1L,
            Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AddProposal_RejectsAStaleOrDisabledTemplateSnapshot()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteChatReplyStore(database.ConnectionFactory);
        await store.AddTemplateAsync("Eventdauer?", "!event @Username");
        var template = Assert.Single(await store.LoadActiveTemplatesAsync());
        await store.SetTemplateEnabledAsync(
            template.TemplateId,
            template.RowVersion,
            isEnabled: false);

        var result = await store.AddProposalAsync(
            CreateMessage("message-disabled", "viewer", "Wie lange noch?"),
            template,
            "!event @viewer",
            confidence: 0.9,
            batchId: Guid.NewGuid(),
            aiLatencyMilliseconds: null);

        Assert.Null(result);
    }

    private static ChatMessageSnapshot CreateMessage(
        string twitchMessageId,
        string login,
        string originalText)
    {
        var receivedAt = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var identity = ChatMessageIdentityV1.Create(
            $"event-{twitchMessageId}",
            twitchMessageId,
            "channel-1",
            $"user-{login}",
            login,
            "Viewer Name",
            originalText,
            receivedAt);
        var context = new ChannelContextSnapshot(
            "channel-1",
            TwitchAI.Core.Services.EffectivePolicyResolver
                .BootstrapJustChattingCategoryId,
            "Just Chatting",
            receivedAt,
            Revision: 1,
            IsVerified: true);
        return new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(originalText),
            context,
            IngressSequence: 1);
    }
}
