using TwitchAI.App.Services;

namespace TwitchAI.App.Tests.Services;

public sealed class ConfigurationPageSectionPreferenceStoreTests
{
    [Theory]
    [InlineData(ConfigurationPageSectionScopes.Blacklist)]
    [InlineData(ConfigurationPageSectionScopes.Backseatgaming)]
    [InlineData(ConfigurationPageSectionScopes.Whitelist)]
    public void GetOrDefault_WithoutSavedPreference_ReturnsPhrases(
        string scope)
    {
        var sut = new ConfigurationPageSectionPreferenceStore(
            new MemoryLocalAppStorage());

        var result = sut.GetOrDefault(scope);

        Assert.Equal(ConfigurationPageSection.Phrases, result);
    }

    [Fact]
    public void Save_PersistsEveryPageIndependentlyAcrossStoreInstances()
    {
        var storage = new MemoryLocalAppStorage();
        var firstInstance = new ConfigurationPageSectionPreferenceStore(
            storage);

        firstInstance.Save(
            ConfigurationPageSectionScopes.Blacklist,
            ConfigurationPageSection.Learning);
        firstInstance.Save(
            ConfigurationPageSectionScopes.Backseatgaming,
            ConfigurationPageSection.Phrases);
        firstInstance.Save(
            ConfigurationPageSectionScopes.Whitelist,
            ConfigurationPageSection.Learning);

        var restartedInstance = new ConfigurationPageSectionPreferenceStore(
            storage);
        Assert.Equal(
            ConfigurationPageSection.Learning,
            restartedInstance.GetOrDefault(
                ConfigurationPageSectionScopes.Blacklist));
        Assert.Equal(
            ConfigurationPageSection.Phrases,
            restartedInstance.GetOrDefault(
                ConfigurationPageSectionScopes.Backseatgaming));
        Assert.Equal(
            ConfigurationPageSection.Learning,
            restartedInstance.GetOrDefault(
                ConfigurationPageSectionScopes.Whitelist));
    }

    [Theory]
    [InlineData("")]
    [InlineData("learning ")]
    [InlineData("LEARNING")]
    [InlineData("42")]
    public void GetOrDefault_WithInvalidStoredValue_UsesRequestedFallback(
        string storedValue)
    {
        var storage = new MemoryLocalAppStorage(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ConfigurationPageSection.v1.blacklist"] = storedValue,
            });
        var sut = new ConfigurationPageSectionPreferenceStore(storage);

        var result = sut.GetOrDefault(
            ConfigurationPageSectionScopes.Blacklist,
            ConfigurationPageSection.Learning);

        Assert.Equal(ConfigurationPageSection.Learning, result);
    }

    [Fact]
    public void Save_WhenStorageWriteFails_DoesNotBreakSelectorInteraction()
    {
        var sut = new ConfigurationPageSectionPreferenceStore(
            new ThrowingLocalAppStorage());

        var exception = Record.Exception(() => sut.Save(
            ConfigurationPageSectionScopes.Whitelist,
            ConfigurationPageSection.Learning));

        Assert.Null(exception);
    }

    private sealed class MemoryLocalAppStorage(
        Dictionary<string, string>? values = null)
        : ILocalAppStorage
    {
        private readonly Dictionary<string, string> _values =
            values ?? new Dictionary<string, string>(StringComparer.Ordinal);

        public string LocalFolderPath => string.Empty;

        public string? GetString(string key) =>
            _values.GetValueOrDefault(key);

        public void SetString(string key, string value) =>
            _values[key] = value;

        public void SetStrings(IReadOnlyDictionary<string, string> values)
        {
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }
        }
    }

    private sealed class ThrowingLocalAppStorage : ILocalAppStorage
    {
        public string LocalFolderPath => string.Empty;

        public string? GetString(string key) => null;

        public void SetString(string key, string value) =>
            throw new IOException("settings unavailable");

        public void SetStrings(IReadOnlyDictionary<string, string> values) =>
            throw new IOException("settings unavailable");
    }
}
