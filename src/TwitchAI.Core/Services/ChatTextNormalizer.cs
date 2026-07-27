using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ChatTextNormalizer : IChatTextNormalizer
{
    public const int CurrentVersion = 1;

    public int NormalizationVersion => CurrentVersion;

    public ProcessedChatText Process(string originalText)
    {
        ArgumentNullException.ThrowIfNull(originalText);

        var lowerText = originalText.ToLowerInvariant();
        var ruleNormalizedText = NormalizeForRules(originalText);

        return new ProcessedChatText(
            originalText,
            lowerText,
            ruleNormalizedText,
            CurrentVersion,
            Sha256(originalText),
            Sha256(ruleNormalizedText));
    }

    private static string NormalizeForRules(string originalText)
    {
        var normalized = originalText.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        var whitespacePending = false;
        Span<char> lowered = stackalloc char[2];

        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format
                or UnicodeCategory.Control
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned)
            {
                if (!Rune.IsWhiteSpace(rune))
                {
                    continue;
                }
            }

            if (Rune.IsWhiteSpace(rune))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }

            var lowerRune = Rune.ToLowerInvariant(rune);
            var length = lowerRune.EncodeToUtf16(lowered);
            builder.Append(lowered[..length]);
        }

        return builder.ToString();
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
