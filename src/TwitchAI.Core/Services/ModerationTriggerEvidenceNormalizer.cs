using System.Globalization;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

/// <summary>
/// Turns model-provided trigger fragments into a small, exact evidence span.
/// This class validates evidence only; it never decides whether a message is a
/// policy violation.
/// </summary>
public static class ModerationTriggerEvidenceNormalizer
{
    public const int MaximumEvidenceCharacters = 160;
    public const int MaximumEvidencePhrases = 1;

    private static readonly HashSet<string> DirectTargets = new(StringComparer.Ordinal)
    {
        "du", "dich", "dir", "dein", "deine", "deiner", "deinem", "deinen",
        "ihr", "euch", "euer", "eure", "you", "your", "u",
        "nutzer", "nutzerin", "user", "streamer", "streamerin", "moderator",
        "moderatorin", "mod",
    };

    private static readonly HashSet<string> AttributionWords = new(StringComparer.Ordinal)
    {
        "bist", "seid", "sind", "ist", "wirkst", "wirkt", "nervst", "nervt",
        "so", "bleibst", "bleibt",
    };

    private static readonly HashSet<string> NonEvidenceWords = new(StringComparer.Ordinal)
    {
        "aber", "alle", "also", "auch", "besser", "das", "dass", "dann", "der",
        "die", "dies", "diese", "ein", "eine", "einer", "es", "fest", "für", "halt",
        "heute", "hier", "ich", "im", "in", "ja", "kappa", "kein", "keine", "mal",
        "man", "mit", "nach", "net", "nicht", "noch", "nur", "oder", "sie", "sonst",
        "und", "vom", "von", "was", "wie", "wieder", "zu", "zum", "zur",
        "chat", "content", "game", "gameplay", "musik", "spiel", "stream",
    };

    private static readonly HashSet<string> HighConfidenceInsultCues = new(StringComparer.Ordinal)
    {
        "arschloch", "dumm", "dummkopf", "hurensohn", "idiot", "idiotin", "loser",
        "missgeburt", "opfer", "schwachkopf", "spast", "trottel", "wichser",
        "asshole", "dumbass", "idiot", "moron",
    };

    public static IReadOnlyList<string> Normalize(
        string lowerSourceText,
        string? ruleCode,
        IEnumerable<string?> candidates)
    {
        ArgumentNullException.ThrowIfNull(lowerSourceText);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!ModerationRuleCodes.AllowsTriggerLearning(ruleCode))
        {
            return [];
        }

        var exactCandidates = CollectExactCandidates(lowerSourceText, candidates);
        if (exactCandidates.Count == 0)
        {
            return [];
        }

        if (string.Equals(ruleCode, ModerationRuleCodes.Insult, StringComparison.Ordinal))
        {
            return SelectInsultEvidence(lowerSourceText, exactCandidates);
        }

        var best = exactCandidates
            .Where(item => LearningTriggerPhraseFilter.IsContextual(item.Text))
            .OrderBy(item => LearningTriggerPhraseFilter.CountLexicalTokens(item.Text))
            .ThenBy(item => item.Text.Length)
            .ThenBy(item => item.Start)
            .Select(item => item.Text)
            .FirstOrDefault();
        return best is null ? [] : [best];
    }

    private static List<EvidenceCandidate> CollectExactCandidates(
        string source,
        IEnumerable<string?> candidates)
    {
        var result = new List<EvidenceCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Take(32))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate.ToLowerInvariant().Trim();
            if (normalized.Length is 0 or > MaximumEvidenceCharacters
                || !seen.Add(normalized))
            {
                continue;
            }

            var searchStart = 0;
            while (searchStart <= source.Length - normalized.Length)
            {
                var index = source.IndexOf(normalized, searchStart, StringComparison.Ordinal);
                if (index < 0)
                {
                    break;
                }

                result.Add(new EvidenceCandidate(index, normalized));
                searchStart = index + Math.Max(1, normalized.Length);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SelectInsultEvidence(
        string source,
        IReadOnlyList<EvidenceCandidate> candidates)
    {
        var possible = new List<EvidenceCandidate>(candidates.Count * 2);
        possible.AddRange(candidates);

        // Models occasionally split target and insult into separate list items.
        // Reconstruct only a short, same-clause span from the original message.
        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
            {
                var start = Math.Min(candidates[leftIndex].Start, candidates[rightIndex].Start);
                var end = Math.Max(candidates[leftIndex].End, candidates[rightIndex].End);
                if (end - start > MaximumEvidenceCharacters)
                {
                    continue;
                }

                var combined = source[start..end].Trim();
                if (combined.Length == 0 || HasClauseBoundary(combined))
                {
                    continue;
                }

                var normalizedStart = source.IndexOf(combined, start, StringComparison.Ordinal);
                possible.Add(new EvidenceCandidate(normalizedStart, combined));
            }
        }

        var best = possible
            .Where(item => IsTargetedInsultEvidence(item.Text))
            .DistinctBy(item => item.Text, StringComparer.Ordinal)
            .OrderBy(item => LearningTriggerPhraseFilter.CountLexicalTokens(item.Text))
            .ThenBy(item => item.Text.Length)
            .ThenBy(item => item.Start)
            .Select(item => item.Text)
            .FirstOrDefault();
        return best is null ? [] : [best];
    }

    private static bool IsTargetedInsultEvidence(string evidence)
    {
        var tokens = Tokenize(evidence);
        if (tokens.Count < 2)
        {
            return false;
        }

        var hasDirectTarget = tokens.Any(token =>
            token.IsMention || DirectTargets.Contains(token.Value));
        var hasSubstantivePredicate = tokens.Any(token =>
            !token.IsMention
            && !DirectTargets.Contains(token.Value)
            && !AttributionWords.Contains(token.Value)
            && !NonEvidenceWords.Contains(token.Value));
        if (hasDirectTarget && hasSubstantivePredicate)
        {
            return true;
        }

        // Also accept a compact named-target construction such as
        // "nico ist ein idiot". Articles and content nouns are deliberately not
        // treated as plausible person names.
        if (tokens.Count >= 3
            && IsPlausibleNamedTarget(tokens[0])
            && tokens.Skip(1).Take(2).Any(token => AttributionWords.Contains(token.Value))
            && tokens.Skip(1).Any(token =>
                !AttributionWords.Contains(token.Value)
                && !NonEvidenceWords.Contains(token.Value)))
        {
            return true;
        }

        return tokens.Count == 2
            && IsPlausibleNamedTarget(tokens[0])
            && HighConfidenceInsultCues.Contains(tokens[1].Value);
    }

    private static bool IsPlausibleNamedTarget(EvidenceToken token) =>
        !token.IsMention
        && !DirectTargets.Contains(token.Value)
        && !AttributionWords.Contains(token.Value)
        && !NonEvidenceWords.Contains(token.Value);

    private static List<EvidenceToken> Tokenize(string value)
    {
        var result = new List<EvidenceToken>();
        var index = 0;
        while (index < value.Length)
        {
            var isMention = value[index] == '@'
                && index + 1 < value.Length
                && IsTokenCharacter(value[index + 1]);
            if (!isMention && !IsTokenCharacter(value[index]))
            {
                index++;
                continue;
            }

            var start = index;
            if (isMention)
            {
                index++;
            }

            while (index < value.Length && IsTokenCharacter(value[index]))
            {
                index++;
            }

            result.Add(new EvidenceToken(
                value[start..index].TrimStart('@'),
                isMention));
        }

        return result;
    }

    private static bool HasClauseBoundary(string value) =>
        value.IndexOfAny(['\r', '\n', '.', '!', '?', ';']) >= 0;

    private static bool IsTokenCharacter(char value)
    {
        if (char.IsLetterOrDigit(value) || value == '_')
        {
            return true;
        }

        return CharUnicodeInfo.GetUnicodeCategory(value) is
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.ConnectorPunctuation;
    }

    private sealed record EvidenceCandidate(int Start, string Text)
    {
        public int End => Start + Text.Length;
    }

    private sealed record EvidenceToken(string Value, bool IsMention);
}
