namespace TwitchAI.Infrastructure.Persistence;

public interface IWhitelistTextNormalizer
{
    string Normalize(string value);
}

public sealed class WhitelistTextNormalizer : IWhitelistTextNormalizer
{
    public string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToLowerInvariant();
    }
}
