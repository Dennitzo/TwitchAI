using System.Text;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;

namespace TwitchAI.Infrastructure.Pipeline;

public interface IRetrievalExampleStore
{
    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        IReadOnlyList<string> queryLowerTexts,
        CancellationToken cancellationToken = default) =>
        GetRelevantAsync(channelId, categoryId, policy, cancellationToken);

    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantForLearningTestAsync(
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default) =>
        GetRelevantAsync(
            GlobalPolicyStorage.BroadcasterId,
            categoryId: null,
            policy,
            cancellationToken);

    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantForLearningTestAsync(
        CoreEffectivePolicySnapshot policy,
        string queryLowerText,
        CancellationToken cancellationToken = default) =>
        GetRelevantForLearningTestAsync(policy, cancellationToken);

    async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<RetrievalExample>>>
        GetRelevantPerMessageAsync(
            string channelId,
            string? categoryId,
            CoreEffectivePolicySnapshot policy,
            IReadOnlyList<string> queryLowerTexts,
            CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<RetrievalExample>>(
            StringComparer.Ordinal);
        foreach (var query in queryLowerTexts.Distinct(StringComparer.Ordinal))
        {
            result[query] = await GetRelevantAsync(
                    channelId,
                    categoryId,
                    policy,
                    [query],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}

public sealed class SqliteRetrievalExampleStore(ISqliteConnectionFactory connectionFactory)
    : IRetrievalExampleStore
{
    private const int MaximumCandidates = 1_000;
    private const int MaximumRankedExamples = 24;
    private const double MinimumSimilarity = 0.20d;

    public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default) =>
        GetRelevantAsync(
            channelId,
            categoryId,
            policy,
            [],
            cancellationToken);

    public async ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        IReadOnlyList<string> queryLowerTexts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(queryLowerTexts);

        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = await ReadCandidatesAsync(
                connection,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return Rank(candidates, queryLowerTexts);
    }

    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<RetrievalExample>>>
        GetRelevantPerMessageAsync(
            string channelId,
            string? categoryId,
            CoreEffectivePolicySnapshot policy,
            IReadOnlyList<string> queryLowerTexts,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(queryLowerTexts);
        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = await ReadCandidatesAsync(
                connection,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return queryLowerTexts
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                query => query,
                query => (IReadOnlyList<RetrievalExample>)Rank(candidates, [query]),
                StringComparer.Ordinal);
    }

    public ValueTask<IReadOnlyList<RetrievalExample>>
        GetRelevantForLearningTestAsync(
            CoreEffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default) =>
        GetRelevantForLearningTestAsync(policy, string.Empty, cancellationToken);

    public async ValueTask<IReadOnlyList<RetrievalExample>>
        GetRelevantForLearningTestAsync(
            CoreEffectivePolicySnapshot policy,
            string queryLowerText,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(queryLowerText);
        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = await ReadCandidatesAsync(
                connection,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return Rank(candidates, [queryLowerText]);
    }

    private static async Task<IReadOnlyList<RetrievalCandidate>> ReadCandidatesAsync(
        SqliteConnection connection,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var activeRules = policy.ActiveRules
            .Where(rule => policy.Contains(rule.PolicySet))
            .Select(rule => new
            {
                PolicySet = SqliteLearningDatasetService.ToDatabasePolicySet(
                    rule.PolicySet),
                rule.RuleCode,
            })
            .DistinctBy(
                rule => (rule.PolicySet, rule.RuleCode))
            .ToArray();
        if (activeRules.Length == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var activeRuleValues = string.Join(
            ",\n                ",
            activeRules.Select(
                (_, index) => $"(@active_policy_{index}, @active_rule_{index})"));
        command.CommandText = SqliteLearningDatasetService.EffectiveExamplesCte + ",\n" +
            $$"""
            active_rules(policy_set, rule_code) AS (
                VALUES {{activeRuleValues}}
            ),
            taxonomy_examples AS (
                SELECT source_kind, source_id, original_text, lower_text,
                       learning_decision, policy_set,
                       CASE rule_code
                           WHEN 'G_SEXUALIZATION' THEN 'G_SEXUAL_CONTENT'
                           WHEN 'G_SEXUAL_EXPLICIT' THEN 'G_SEXUAL_CONTENT'
                           WHEN 'G_SEXISM' THEN 'G_SEXUAL_CONTENT'
                           WHEN 'G_INSULT' THEN 'G_PERSONAL_ATTACK'
                           WHEN 'G_HARASSMENT' THEN 'G_PERSONAL_ATTACK'
                           WHEN 'G_RACISM' THEN 'G_HATE'
                           WHEN 'G_LGBTQ_HATE' THEN 'G_HATE'
                           WHEN 'G_ABLEISM' THEN 'G_HATE'
                           WHEN 'G_HATE_OTHER' THEN 'G_HATE'
                           WHEN 'G_THREAT' THEN 'G_HARM'
                           WHEN 'G_VIOLENCE' THEN 'G_HARM'
                           WHEN 'G_SELF_HARM_INCITEMENT' THEN 'G_HARM'
                           ELSE rule_code
                       END AS rule_code,
                       action, timeout_seconds, reason, is_removed,
                       row_version, effective_utc
                FROM effective_examples
                WHERE rule_code NOT IN (
                    'G_ADVERTISING','G_SCAM','G_OTHER_ABUSE','G_DISRUPTION',
                    'G_STREAMER_INSTRUCTION','LOCAL_BLACKLIST')
            ),
            newest_human_decisions AS (
                SELECT source_kind, source_id, lower_text, action,
                       timeout_seconds, policy_set, reason, learning_decision,
                       rule_code, effective_utc,
                       ROW_NUMBER() OVER (
                           PARTITION BY lower_text
                           ORDER BY julianday(effective_utc) DESC,
                                    effective_utc DESC,
                                    source_kind DESC,
                                    source_id DESC) AS decision_rank
                FROM taxonomy_examples
                WHERE is_removed = 0
                  AND learning_decision = 'allow'
            )
            """ + "\n" +
            """
            SELECT decision.source_kind, decision.source_id,
                   decision.lower_text, decision.action,
                   decision.timeout_seconds, decision.policy_set,
                   decision.reason, decision.learning_decision,
                   decision.rule_code, decision.effective_utc
            FROM newest_human_decisions decision
            INNER JOIN active_rules active
                ON active.policy_set = decision.policy_set
               AND active.rule_code = decision.rule_code
            WHERE decision.decision_rank = 1
              AND decision.learning_decision = 'allow'
            ORDER BY julianday(decision.effective_utc) DESC,
                     decision.effective_utc DESC,
                     decision.source_kind DESC,
                     decision.source_id DESC
            LIMIT @limit;
            """;
        for (var index = 0; index < activeRules.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"@active_policy_{index}",
                activeRules[index].PolicySet);
            command.Parameters.AddWithValue(
                $"@active_rule_{index}",
                activeRules[index].RuleCode);
        }

        command.Parameters.AddWithValue("@limit", MaximumCandidates);

        var candidates = new List<RetrievalCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var policySet = SqliteLearningDatasetService.ParsePolicySet(
                reader.GetString(5));
            var ruleCode = reader.GetString(8);
            var decision = SqliteLearningDatasetService.ParseDecision(
                reader.GetString(7));
            var action = SqliteLearningDatasetService.ParseAction(
                reader.GetString(3));
            var lowerText = reader.GetString(2).ToLowerInvariant();
            candidates.Add(
                new RetrievalCandidate(
                    SqliteLearningDatasetService.ParseSourceKind(reader.GetString(0)),
                    reader.GetInt64(1),
                    new RetrievalExample(
                        lowerText,
                        action,
                        action == ModerationActionKind.Timeout && !reader.IsDBNull(4)
                            ? reader.GetInt32(4)
                            : null,
                        policySet,
                        reader.GetString(6),
                        decision == LearningDecision.Moderate,
                        ruleCode),
                    SqliteData.GetDateTimeOffset(reader, 9)));
        }

        return candidates;
    }

    private static RetrievalExample[] Rank(
        IReadOnlyList<RetrievalCandidate> candidates,
        IReadOnlyList<string> queryLowerTexts)
    {
        var queries = queryLowerTexts
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Select(item => new TextFeatures(
                item,
                Tokenize(item),
                CharacterNgrams(item)))
            .ToArray();

        // Only allow examples reach retrieval. If several allow records contain
        // identical text, the newest effective record supplies its metadata.
        var distinct = candidates
            .OrderByDescending(item => item.EffectiveAtUtc)
            .ThenByDescending(item => item.SourceId)
            .DistinctBy(item => item.Example.LowerText, StringComparer.Ordinal)
            .Select(item => new ScoredCandidate(item, Similarity(item.Example.LowerText, queries)))
            .Where(item => item.Score >= MinimumSimilarity)
            .ToArray();

        var allowed = SortCandidates(
            distinct.Where(item => !item.Candidate.Example.IsPositive));
        return allowed
            .Take(MaximumRankedExamples)
            .Select(item => item.Candidate.Example with
            {
                IsExactHumanDecision = queries.Any(query =>
                    StringComparer.Ordinal.Equals(
                        item.Candidate.Example.LowerText,
                        query.Text)),
                SimilarityScore = item.Score,
                Source = item.Candidate.SourceKind.ToString(),
                EffectiveAtUtc = item.Candidate.EffectiveAtUtc,
            })
            .ToArray();
    }

    private static List<ScoredCandidate> SortCandidates(
        IEnumerable<ScoredCandidate> candidates) =>
        candidates
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.EffectiveAtUtc)
            .ThenByDescending(item => item.Candidate.SourceId)
            .ToList();

    private static double Similarity(
        string candidate,
        IReadOnlyList<TextFeatures> queries)
    {
        var maximum = 0d;
        var candidateTokens = Tokenize(candidate);
        var candidateGrams = CharacterNgrams(candidate);
        foreach (var query in queries)
        {
            if (StringComparer.Ordinal.Equals(candidate, query.Text))
            {
                return 1d;
            }

            var tokenScore = Jaccard(candidateTokens, query.Tokens);
            var gramScore = Dice(candidateGrams, query.CharacterNgrams);
            maximum = Math.Max(maximum, (tokenScore * 0.65d) + (gramScore * 0.35d));
        }

        return maximum;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static HashSet<string> CharacterNgrams(string value)
    {
        var normalized = NormalizeCharacters(value);
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Length <= 3)
        {
            if (normalized.Length > 0)
            {
                grams.Add(normalized);
            }

            return grams;
        }

        for (var index = 0; index <= normalized.Length - 3; index++)
        {
            grams.Add(normalized.Substring(index, 3));
        }

        return grams;
    }

    private static string NormalizeCharacters(string value)
    {
        var result = new StringBuilder(value.Length);
        var lastWasSpace = true;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                result.Append(' ');
                lastWasSpace = true;
            }
        }

        if (result.Length > 0 && result[^1] == ' ')
        {
            result.Length--;
        }

        return result.ToString();
    }

    private static double Jaccard(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0d;
        }

        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    private static double Dice(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0d;
        }

        var intersection = left.Count(right.Contains);
        return (2d * intersection) / (left.Count + right.Count);
    }

    private sealed record RetrievalCandidate(
        LearningSourceKind SourceKind,
        long SourceId,
        RetrievalExample Example,
        DateTimeOffset EffectiveAtUtc);

    private sealed record ScoredCandidate(
        RetrievalCandidate Candidate,
        double Score);

    private sealed record TextFeatures(
        string Text,
        HashSet<string> Tokens,
        HashSet<string> CharacterNgrams);
}
