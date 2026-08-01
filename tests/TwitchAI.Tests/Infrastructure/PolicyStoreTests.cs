using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class PolicyStoreTests
{
    [Theory]
    [InlineData("509658")]
    [InlineData("509672")]
    public async Task NonGameplayCategory_UsesGeneralPolicyOnly(string categoryId)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetEffectivePolicyAsync("channel-1", categoryId);

        Assert.Equal(10, policy.GeneralRevision);
        Assert.Equal(0, policy.CategoryRevision);
        Assert.Null(policy.CategoryId);
        Assert.Equal(19, policy.Rules.Count);
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == GlobalPolicyStorage.GeneralRuleCode);
        var spamRule = Assert.Single(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Spam);
        Assert.Equal(
            TwitchAI.Core.Models.ModerationRuleCodes.SpamPromptInstruction,
            spamRule.PromptInstruction);
        var insultRule = Assert.Single(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Insult);
        Assert.Equal(
            TwitchAI.Core.Models.ModerationRuleCodes.InsultPromptInstruction,
            insultRule.PromptInstruction);
        Assert.InRange(insultRule.PromptInstruction.Length, 1, 650);
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Advertising
                && rule.Title == "Werbung"
                && rule.DefaultAction == ModerationRuleAction.Timeout
                && rule.DefaultTimeoutSeconds == 120);
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Begging
                && rule.Title == "Gebettel"
                && rule.DefaultAction == ModerationRuleAction.Timeout
                && rule.DefaultTimeoutSeconds == 120);
        Assert.DoesNotContain(
            policy.Rules,
            rule => rule.RuleCode == "G_STREAMER_INSTRUCTION");
        Assert.DoesNotContain(
            policy.Rules,
            rule => string.Equals(
                rule.RuleCode,
                GlobalPolicyStorage.BackseatgamingRuleCode,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task KnownGame_AddsGeneralBackseatgamingPolicy()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetEffectivePolicyAsync("channel-1", "32982");

        Assert.Equal("32982", policy.CategoryId);
        Assert.Equal(10, policy.GeneralRevision);
        Assert.Equal(4, policy.CategoryRevision);
        Assert.Equal(20, policy.Rules.Count);
        var rule = Assert.Single(
            policy.Rules,
            item => item.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);
        Assert.Contains(
            policy.Rules,
            item => item.RuleCode == GlobalPolicyStorage.GeneralRuleCode);
        Assert.Equal(GlobalPolicyStorage.BackseatgamingRuleCode, rule.RuleCode);
        Assert.Equal(ModerationRuleAction.Timeout, rule.DefaultAction);
        Assert.Equal(600, rule.DefaultTimeoutSeconds);
        Assert.Equal(
            TwitchAI.Core.Models.ModerationRuleCodes.BackseatgamingPromptInstruction,
            rule.PromptInstruction);
        Assert.NotEmpty(policy.Phrases);
    }

    [Fact]
    public async Task LearningPolicy_AlwaysLoadsGeneralAndBackseatgamingOffline()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetLearningPolicyAsync();

        Assert.Equal(GlobalPolicyStorage.BroadcasterId, policy.BroadcasterId);
        Assert.Equal(10, policy.GeneralRevision);
        Assert.Equal(4, policy.CategoryRevision);
        Assert.Contains(
            policy.Rules,
            item => item.RuleCode == GlobalPolicyStorage.GeneralRuleCode);
        Assert.Contains(
            policy.Rules,
            item => item.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);
        Assert.DoesNotContain(
            policy.Rules,
            item => item.RuleCode == "G_STREAMER_INSTRUCTION");
    }

    [Fact]
    public async Task UnknownCategory_FallsBackToGeneralOnly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetEffectivePolicyAsync("channel-1", "unknown");

        Assert.Null(policy.CategoryId);
        Assert.Equal(0, policy.CategoryRevision);
        Assert.Equal(
        [
            "G_PERSONAL_DATA", "G_HARM", "G_HATE", "G_SEXUAL_CONTENT",
            "G_PERSONAL_ATTACK", "G_BEGGING", "G_SPAM",
            "G_SELF_PROMOTION", "G_BOT_SCAM",
        ], policy.Rules.Select(rule => rule.RuleCode));
    }

    [Fact]
    public async Task BroadcasterSeed_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        await sut.EnsureBroadcasterSeededAsync("channel-1");
        await sut.EnsureBroadcasterSeededAsync("channel-1");

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM policy_sets WHERE broadcaster_id = '*';";
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(2L, Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GlobalPolicy_IsSharedAcrossChannelsAndGlobalInvalidationClearsEveryCacheKey()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var channelOne = await sut.GetEffectivePolicyAsync("channel-1", "32982");
        var channelTwo = await sut.GetEffectivePolicyAsync("channel-2", "32982");

        Assert.Equal("channel-1", channelOne.BroadcasterId);
        Assert.Equal("channel-2", channelTwo.BroadcasterId);
        Assert.Equal(channelOne.PolicyHash, channelTwo.PolicyHash);

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE blacklist_rules
                SET prompt_instruction = prompt_instruction || ' global-change'
                WHERE scope_id IN (
                    SELECT scope_id
                    FROM policy_sets
                    WHERE broadcaster_id = '*'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        sut.Invalidate(
            new PolicyChangeSet(
                GlobalPolicyStorage.BroadcasterId,
                ScopeKind: null,
                CategoryId: null,
                AllowlistChanged: false,
                NewRevision: null));

        var refreshedOne = await sut.GetEffectivePolicyAsync("channel-1", "32982");
        var refreshedTwo = await sut.GetEffectivePolicyAsync("channel-2", "32982");
        Assert.NotEqual(channelOne.PolicyHash, refreshedOne.PolicyHash);
        Assert.NotEqual(channelTwo.PolicyHash, refreshedTwo.PolicyHash);
        Assert.Equal(refreshedOne.PolicyHash, refreshedTwo.PolicyHash);
    }

    [Fact]
    public async Task EveryKnownNonExcludedCategory_UsesBackseatgaming()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO twitch_categories(
                    category_id, display_name, last_seen_utc)
                VALUES('custom-game', 'Custom Game', @now);
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var policy = await CreateStore(database)
            .GetEffectivePolicyAsync("any-channel", "custom-game");

        Assert.Equal("custom-game", policy.CategoryId);
        Assert.Contains(
            policy.Rules,
            item => item.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);
    }

    private static SqlitePolicyStore CreateStore(TestDatabase database)
    {
        return new SqlitePolicyStore(database.ConnectionFactory, new ChatTextNormalizer());
    }
}
