using System.Collections.Concurrent;

namespace TwitchAI.Infrastructure.Persistence;

public interface IWhitelistStore
{
    Task<WhitelistMatch> MatchAsync(
        string broadcasterId,
        string chatterUserId,
        string chatterLogin,
        string normalizedMessage,
        CancellationToken cancellationToken = default);

    void Invalidate(string broadcasterId);
}

public sealed class SqliteWhitelistStore : IWhitelistStore, IPolicyCacheInvalidator
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IWhitelistTextNormalizer _normalizer;
    private readonly ConcurrentDictionary<string, Lazy<Task<WhitelistSnapshot>>> _cache =
        new(StringComparer.Ordinal);

    public SqliteWhitelistStore(
        ISqliteConnectionFactory connectionFactory,
        IWhitelistTextNormalizer normalizer,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _normalizer = normalizer;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<WhitelistMatch> MatchAsync(
        string broadcasterId,
        string chatterUserId,
        string chatterLogin,
        string normalizedMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatterUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatterLogin);
        ArgumentNullException.ThrowIfNull(normalizedMessage);

        var lazy = _cache.GetOrAdd(
            broadcasterId,
            key => new Lazy<Task<WhitelistSnapshot>>(
                () => LoadAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        WhitelistSnapshot snapshot;
        try
        {
            snapshot = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(broadcasterId, out _);
            throw;
        }

        if (snapshot.UserIds.TryGetValue(chatterUserId, out var userAllowlistId))
        {
            await RecordHitAsync(userAllowlistId, cancellationToken).ConfigureAwait(false);
            return new WhitelistMatch(true, userAllowlistId, AllowlistSubjectKind.UserId);
        }

        var normalizedLogin = chatterLogin.ToLowerInvariant();
        if (snapshot.UserLogins.TryGetValue(
                normalizedLogin,
                out var loginAllowlistId))
        {
            await RecordHitAsync(loginAllowlistId, cancellationToken).ConfigureAwait(false);
            return new WhitelistMatch(
                true,
                loginAllowlistId,
                AllowlistSubjectKind.UserLogin);
        }

        var whitelistKey = _normalizer.Normalize(normalizedMessage);
        if (snapshot.ExactMessages.TryGetValue(whitelistKey, out var messageAllowlistId))
        {
            await RecordHitAsync(messageAllowlistId, cancellationToken).ConfigureAwait(false);
            return new WhitelistMatch(true, messageAllowlistId, AllowlistSubjectKind.ExactMessage);
        }

        return new WhitelistMatch(false, null, null);
    }

    public void Invalidate(string broadcasterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        if (StringComparer.Ordinal.Equals(
                broadcasterId,
                GlobalPolicyStorage.BroadcasterId))
        {
            _cache.Clear();
            return;
        }

        _cache.TryRemove(broadcasterId, out _);
    }

    public void Invalidate(PolicyChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        if (changeSet.AllowlistChanged)
        {
            Invalidate(changeSet.BroadcasterId);
        }
    }

    private async Task<WhitelistSnapshot> LoadAsync(
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT allowlist_id, subject_kind, normalized_value
            FROM whitelist_phrases
            WHERE broadcaster_id = @global_scope
              AND enabled = 1
              AND archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);

        var userIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var userLogins = new Dictionary<string, long>(StringComparer.Ordinal);
        var exactMessages = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            var kind = reader.GetString(1);
            var value = reader.GetString(2);
            switch (kind)
            {
                case "user_id":
                    userIds.TryAdd(value, id);
                    break;
                case "user_login":
                    userLogins.TryAdd(value, id);
                    break;
                case "exact_message":
                    exactMessages.TryAdd(value, id);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported allowlist subject kind '{kind}'.");
            }
        }

        return new WhitelistSnapshot(userIds, userLogins, exactMessages);
    }

    private async Task RecordHitAsync(long allowlistId, CancellationToken cancellationToken)
    {
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
                last_hit_utc = @last_hit_utc
            WHERE allowlist_id = @allowlist_id
              AND enabled = 1
              AND archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "@last_hit_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@allowlist_id", allowlistId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record WhitelistSnapshot(
        IReadOnlyDictionary<string, long> UserIds,
        IReadOnlyDictionary<string, long> UserLogins,
        IReadOnlyDictionary<string, long> ExactMessages);
}
