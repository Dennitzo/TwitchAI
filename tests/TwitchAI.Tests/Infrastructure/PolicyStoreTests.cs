using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class PolicyStoreTests
{
    private static readonly string[] CurrentGeneralRuleCodes =
    [
        "G_PERSONAL_DATA", "G_HARM", "G_HATE", "G_SEXUAL_CONTENT",
        "G_PERSONAL_ATTACK", "G_BEGGING", "G_SPAM",
        "G_SELF_PROMOTION", "G_BOT_SCAM",
    ];

    [Theory]
    [InlineData("509658")]
    [InlineData("509672")]
    public async Task NonGameplayCategory_UsesGeneralPolicyOnly(string categoryId)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetEffectivePolicyAsync("channel-1", categoryId);

        Assert.Equal(await ReadStoredRevisionAsync(database, "general"), policy.GeneralRevision);
        Assert.Equal(0, policy.CategoryRevision);
        Assert.Null(policy.CategoryId);
        Assert.Equal(CurrentGeneralRuleCodes, policy.Rules.Select(rule => rule.RuleCode));
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == GlobalPolicyStorage.GeneralRuleCode);
        var spamRule = Assert.Single(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Spam);
        Assert.Equal(
            TwitchAI.Core.Models.ModerationRuleCodes.SpamPromptInstruction,
            spamRule.PromptInstruction);
        var personalAttackRule = Assert.Single(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.PersonalAttack);
        Assert.Contains("identifizierbarer Zielperson", personalAttackRule.PromptInstruction);
        Assert.Equal(ModerationRuleAction.Timeout, personalAttackRule.DefaultAction);
        Assert.Equal(600, personalAttackRule.DefaultTimeoutSeconds);
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.SelfPromotion
                && rule.Title == "Eigenwerbung"
                && rule.DefaultAction == ModerationRuleAction.Timeout
                && rule.DefaultTimeoutSeconds == 86400);
        Assert.Contains(
            policy.Rules,
            rule => rule.RuleCode == TwitchAI.Core.Models.ModerationRuleCodes.Begging
                && rule.Title == "Gebettel"
                && rule.DefaultAction == ModerationRuleAction.Timeout
                && rule.DefaultTimeoutSeconds == 86400);
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
        Assert.Equal(await ReadStoredRevisionAsync(database, "general"), policy.GeneralRevision);
        Assert.Equal(await ReadStoredRevisionAsync(database, "backseatgaming"), policy.CategoryRevision);
        Assert.Equal(
            CurrentGeneralRuleCodes.Append(GlobalPolicyStorage.BackseatgamingRuleCode),
            policy.Rules.Select(rule => rule.RuleCode));
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
            await ReadStoredPromptAsync(database, GlobalPolicyStorage.BackseatgamingRuleCode),
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
        Assert.Equal(await ReadStoredRevisionAsync(database, "general"), policy.GeneralRevision);
        Assert.Equal(await ReadStoredRevisionAsync(database, "backseatgaming"), policy.CategoryRevision);
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
    public async Task KnownCategoryChange_KeepsTheRequestedCategoryInCachedSnapshots()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO twitch_categories(category_id, display_name, last_seen_utc)
                VALUES('second-game', 'Second Game', @now);
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        var sut = CreateStore(database);

        var first = await sut.GetEffectivePolicyAsync("channel-1", "32982");
        var second = await sut.GetEffectivePolicyAsync("channel-1", "second-game");
        var firstAgain = await sut.GetEffectivePolicyAsync("channel-1", "32982");

        Assert.Equal("32982", first.CategoryId);
        Assert.Equal("second-game", second.CategoryId);
        Assert.Equal("32982", firstAgain.CategoryId);
        Assert.Contains(
            second.Rules,
            rule => rule.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE blacklist_rules
                SET prompt_instruction = prompt_instruction || ' updated-backseat-policy'
                WHERE rule_code = 'BACKSEATGAMING';
                """;
            await command.ExecuteNonQueryAsync();
        }
        sut.Invalidate(new PolicyChangeSet(
            GlobalPolicyStorage.BroadcasterId,
            PolicyScopeKind.Category,
            CategoryId: null,
            AllowlistChanged: false,
            NewRevision: null));

        foreach (var categoryId in new[] { "32982", "second-game" })
        {
            var refreshed = await sut.GetEffectivePolicyAsync("channel-1", categoryId);
            Assert.Equal(categoryId, refreshed.CategoryId);
            Assert.Contains(
                "updated-backseat-policy",
                Assert.Single(refreshed.Rules,
                    rule => rule.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode)
                    .PromptInstruction);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GlobalChannelAndOfflineLearning_DoNotShareCategoryContext(
        bool loadLearningFirst)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);
        if (loadLearningFirst)
        {
            await sut.GetLearningPolicyAsync();
        }

        var channel = await sut.GetEffectivePolicyAsync(
            GlobalPolicyStorage.BroadcasterId, "32982");
        var learning = await sut.GetLearningPolicyAsync();

        Assert.Equal("32982", channel.CategoryId);
        Assert.Null(learning.CategoryId);
        Assert.Contains(
            channel.Rules,
            rule => rule.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);
        Assert.Contains(
            learning.Rules,
            rule => rule.RuleCode == GlobalPolicyStorage.BackseatgamingRuleCode);
    }

    [Fact]
    public async Task ReactivatedRetiredStreamerInstruction_IsNeverLoaded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE blacklist_rules
                SET enabled = 1, archived_utc = NULL
                WHERE rule_code = 'G_STREAMER_INSTRUCTION';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var policy = await CreateStore(database)
            .GetEffectivePolicyAsync("channel-1", "509658");

        Assert.DoesNotContain(
            policy.Rules,
            item => GlobalPolicyStorage.IsRetiredRuleCode(item.RuleCode));
    }

    [Fact]
    public async Task UnknownCategory_FallsBackToGeneralOnly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = CreateStore(database);

        var policy = await sut.GetEffectivePolicyAsync("channel-1", "unknown");

        Assert.Null(policy.CategoryId);
        Assert.Equal(0, policy.CategoryRevision);
        Assert.Equal(CurrentGeneralRuleCodes, policy.Rules.Select(rule => rule.RuleCode));
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

    private static async Task<long> ReadStoredRevisionAsync(
        TestDatabase database,
        string setCode)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT revision FROM policy_sets
            WHERE broadcaster_id = '*' AND set_code = @set_code
              AND enabled = 1 AND archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue("@set_code", setCode);
        var revision = Assert.IsType<long>(await command.ExecuteScalarAsync());
        Assert.True(revision > 0);
        return revision;
    }

    private static async Task<string> ReadStoredPromptAsync(
        TestDatabase database,
        string ruleCode)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT rule.prompt_instruction
            FROM blacklist_rules rule
            INNER JOIN policy_sets policy ON policy.scope_id = rule.scope_id
            WHERE policy.broadcaster_id = '*'
              AND rule.rule_code = @rule_code
              AND rule.enabled = 1 AND rule.archived_utc IS NULL
              AND policy.enabled = 1 AND policy.archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        var instruction = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.False(string.IsNullOrWhiteSpace(instruction));
        return instruction;
    }
}
