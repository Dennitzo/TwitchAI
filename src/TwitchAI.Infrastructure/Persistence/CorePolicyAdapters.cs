using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class SqliteWhitelistPhraseSource : IWhitelistPhraseSource
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public SqliteWhitelistPhraseSource(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async ValueTask<ActiveWhitelistSnapshot> GetActiveEntriesAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_kind, normalized_value
            FROM whitelist_phrases
            WHERE broadcaster_id = @global_scope
              AND subject_kind IN ('exact_message', 'user_login')
              AND enabled = 1
              AND archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);

        var exactMessages = new HashSet<string>(StringComparer.Ordinal);
        var userLogins = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var destination = reader.GetString(0) switch
            {
                "exact_message" => exactMessages,
                "user_login" => userLogins,
                var unsupported => throw new InvalidDataException(
                    $"Unsupported whitelist subject kind '{unsupported}'."),
            };
            destination.Add(reader.GetString(1));
        }

        return new ActiveWhitelistSnapshot(exactMessages, userLogins);
    }

    public async ValueTask RecordHitAsync(
        string channelId,
        WhitelistEntryKind kind,
        string normalizedValue,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(normalizedValue);
        var subjectKind = kind switch
        {
            WhitelistEntryKind.ExactMessage => "exact_message",
            WhitelistEntryKind.UserLogin => "user_login",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE whitelist_phrases
            SET hit_count = hit_count + 1,
                last_hit_utc = @occurred_utc
            WHERE allowlist_id = (
                SELECT allowlist_id
                FROM whitelist_phrases
                WHERE broadcaster_id = @global_scope
                  AND subject_kind = @subject_kind
                  AND normalized_value = @normalized_value
                  AND enabled = 1
                  AND archived_utc IS NULL
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@subject_kind", subjectKind);
        command.Parameters.AddWithValue("@normalized_value", normalizedValue);
        command.Parameters.AddWithValue(
            "@occurred_utc",
            SqliteData.ToDatabaseTimestamp(occurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SqlitePhraseRuleSource : IPhraseRuleSource
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqlitePhraseRuleSource(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
        string channelId,
        TwitchAI.Core.Models.EffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(policy);

        var activeCodes = policy.ActivePolicySets
            .Select(item => ToDatabasePolicySet(item.Code))
            .ToHashSet(StringComparer.Ordinal);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.phrase_id, r.rule_code, s.set_code,
                   p.normalized_phrase, p.manual_override,
                   p.positive_confirmations, p.negative_confirmations,
                   p.archived_utc
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = @global_scope
              AND s.enabled = 1
              AND s.archived_utc IS NULL
              AND r.enabled = 1
              AND r.archived_utc IS NULL
              AND p.enabled = 1
              AND p.archived_utc IS NULL
              AND (
                  p.manual_override = 'active' OR
                  (
                      p.manual_override = 'auto'
                      AND p.positive_confirmations >= 1
                      AND (
                          CAST(p.positive_confirmations AS REAL) /
                          (p.positive_confirmations + p.negative_confirmations)
                      ) >= 0.60
                  )
              )
            ORDER BY s.set_code, r.rule_code, p.normalized_phrase;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);

        var rules = new List<PhraseRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var setCode = reader.GetString(2);
            if (!activeCodes.Contains(setCode))
            {
                continue;
            }

            rules.Add(new PhraseRule(
                reader.GetInt64(0),
                reader.GetString(1),
                ParsePolicySet(setCode),
                reader.GetString(3),
                ParseManualOverride(reader.GetString(4)),
                reader.GetInt32(5),
                reader.GetInt32(6),
                !reader.IsDBNull(7)));
        }

        return rules;
    }

    private static string ToDatabasePolicySet(PolicySetCode code)
    {
        return code switch
        {
            PolicySetCode.General => "general",
            PolicySetCode.Backseatgaming => "backseatgaming",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };
    }

    private static PolicySetCode ParsePolicySet(string value)
    {
        return value switch
        {
            "general" => PolicySetCode.General,
            "backseatgaming" => PolicySetCode.Backseatgaming,
            _ => throw new InvalidDataException($"Unsupported policy set '{value}'."),
        };
    }

    private static PhraseManualOverride ParseManualOverride(string value)
    {
        return value switch
        {
            "auto" => PhraseManualOverride.Auto,
            "active" => PhraseManualOverride.Active,
            "inactive" => PhraseManualOverride.Inactive,
            _ => throw new InvalidDataException($"Unsupported manual override '{value}'."),
        };
    }
}

public sealed class HelixChannelContextService : IChannelContextService
{
    private readonly IChannelStateRepository _repository;
    private readonly ITwitchHelixClient _helixClient;
    private readonly TwitchApiOptions _options;

    public HelixChannelContextService(
        IChannelStateRepository repository,
        ITwitchHelixClient helixClient,
        InfrastructureOptions options)
    {
        _repository = repository;
        _helixClient = helixClient;
        _options = options.TwitchApi;
    }

    public async ValueTask<ChannelContextSnapshot> GetCurrentAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var existing = await _repository.GetAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null &&
            DateTimeOffset.UtcNow - existing.ObservedAt <=
            _options.CategoryReconciliationInterval)
        {
            return Map(existing);
        }

        try
        {
            var channel = await _helixClient.GetChannelAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
            if (channel is null)
            {
                return Unknown(channelId, existing);
            }

            var moderatorId = !string.IsNullOrWhiteSpace(_options.ModeratorUserId)
                ? _options.ModeratorUserId
                : existing?.ModeratorUserId ??
                  (await _helixClient.GetCurrentUserAsync(cancellationToken)
                      .ConfigureAwait(false)).Id;
            await _repository.UpsertAsync(
                new ChannelStateRecord(
                    channel.BroadcasterId,
                    channel.BroadcasterLogin,
                    channel.BroadcasterName,
                    moderatorId,
                    channel.CategoryId,
                    channel.CategoryName,
                    DateTimeOffset.UtcNow,
                    existing?.Revision ?? 1,
                    IsVerified: true),
                cancellationToken).ConfigureAwait(false);
            return Map(
                await _repository.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Channel context was not persisted."));
        }
        catch (HttpRequestException)
        {
            return existing is null
                ? Unknown(channelId, null)
                : new ChannelContextSnapshot(
                    existing.BroadcasterId,
                    existing.CategoryId,
                    existing.CategoryName ?? string.Empty,
                    existing.ObservedAt,
                    existing.Revision,
                    IsVerified: false);
        }
    }

    public async Task<ChannelContextSnapshot> ObserveCategoryAsync(
        string channelId,
        string? categoryId,
        string? categoryName,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.GetAsync(channelId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return await GetCurrentAsync(channelId, cancellationToken).ConfigureAwait(false);
        }

        await _repository.UpsertAsync(
            existing with
            {
                CategoryId = categoryId,
                CategoryName = categoryName,
                ObservedAt = observedAt,
                IsVerified = true,
            },
            cancellationToken).ConfigureAwait(false);
        return Map(
            await _repository.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Channel context was not persisted."));
    }

    private static ChannelContextSnapshot Map(ChannelStateRecord state)
    {
        return new ChannelContextSnapshot(
            state.BroadcasterId,
            state.CategoryId,
            state.CategoryName ?? string.Empty,
            state.ObservedAt,
            state.Revision,
            state.IsVerified);
    }

    private static ChannelContextSnapshot Unknown(
        string channelId,
        ChannelStateRecord? existing)
    {
        return new ChannelContextSnapshot(
            channelId,
            null,
            string.Empty,
            DateTimeOffset.UtcNow,
            existing?.Revision ?? 1,
            IsVerified: false);
    }
}
