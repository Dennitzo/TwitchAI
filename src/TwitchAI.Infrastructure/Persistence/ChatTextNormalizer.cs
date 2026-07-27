using System.Security.Cryptography;
using System.Text;

namespace TwitchAI.Infrastructure.Persistence;

public interface IChatTextNormalizer
{
    int Version { get; }

    string Normalize(string value);

    string ComputeSha256(string normalizedValue);
}

public sealed class ChatTextNormalizer : IChatTextNormalizer
{
    public const int CurrentVersion = 1;

    public int Version => CurrentVersion;

    public string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var source = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(source.Length);
        var whitespacePending = false;

        foreach (var character in source)
        {
            if (char.IsWhiteSpace(character))
            {
                whitespacePending = builder.Length > 0;
                continue;
            }

            if (whitespacePending)
            {
                builder.Append(' ');
                whitespacePending = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    public string ComputeSha256(string normalizedValue)
    {
        ArgumentNullException.ThrowIfNull(normalizedValue);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue)));
    }
}
