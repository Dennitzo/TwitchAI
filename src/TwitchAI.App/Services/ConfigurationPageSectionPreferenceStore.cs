namespace TwitchAI.App.Services;

/// <summary>
/// Selectable sections shared by the configuration pages that show both
/// manually maintained phrases and semantic learning examples.
/// </summary>
public enum ConfigurationPageSection
{
    Phrases,
    Learning,
}

public static class ConfigurationPageSectionScopes
{
    public const string Blacklist = "blacklist";

    public const string Backseatgaming = "backseatgaming";

    public const string Whitelist = "whitelist";
}

public interface IConfigurationPageSectionPreferenceStore
{
    ConfigurationPageSection GetOrDefault(
        string scope,
        ConfigurationPageSection defaultSection =
            ConfigurationPageSection.Phrases);

    void Save(
        string scope,
        ConfigurationPageSection section);
}

/// <summary>
/// Persists the last selected phrases/learning section independently for each
/// configuration page in the package-independent application settings.
/// </summary>
public sealed class ConfigurationPageSectionPreferenceStore(
    ILocalAppStorage storage)
    : IConfigurationPageSectionPreferenceStore
{
    private const string SettingsKeyPrefix =
        "ConfigurationPageSection.v1.";

    private readonly ILocalAppStorage _storage = storage;

    public ConfigurationPageSection GetOrDefault(
        string scope,
        ConfigurationPageSection defaultSection =
            ConfigurationPageSection.Phrases)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (!Enum.IsDefined(defaultSection))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultSection));
        }

        var storedValue = _storage.GetString(GetSettingsKey(scope));
        return Enum.TryParse<ConfigurationPageSection>(
                storedValue,
                ignoreCase: false,
                out var storedSection)
            && Enum.IsDefined(storedSection)
                ? storedSection
                : defaultSection;
    }

    public void Save(
        string scope,
        ConfigurationPageSection section)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        try
        {
            _storage.SetString(
                GetSettingsKey(scope),
                section.ToString());
        }
        catch (IOException)
        {
            // The selector remains usable if local settings cannot be written.
        }
        catch (UnauthorizedAccessException)
        {
            // The selector remains usable if local settings cannot be written.
        }
    }

    private static string GetSettingsKey(string scope) =>
        $"{SettingsKeyPrefix}{scope}";
}
