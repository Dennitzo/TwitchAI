using System.Text;
using System.Text.Json;

namespace TwitchAI.App.Services;

public interface ILocalAppStorage
{
    string LocalFolderPath { get; }

    string? GetString(string key);

    void SetString(string key, string value);

    void SetStrings(IReadOnlyDictionary<string, string> values);
}

internal static class LocalAppSettingKeys
{
    public const string UseCompactModerationContractV6 =
        "UseCompactModerationContractV6";

    public const string RemoveIdenticalAiQueueMessages =
        "RemoveIdenticalAiQueueMessages";

    public const string ReadOnlyMode = "ReadOnlyMode";
}

internal static class LocalAppStorageExtensions
{
    public static bool GetBooleanOrDefault(
        this ILocalAppStorage storage,
        string key,
        bool defaultValue)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return bool.TryParse(storage.GetString(key), out var storedValue)
            ? storedValue
            : defaultValue;
    }
}

/// <summary>
/// Package-independent local storage for both MSIX and unpackaged builds.
/// Secrets remain in the Windows credential vault and are never written here.
/// </summary>
public sealed class JsonLocalAppStorage : ILocalAppStorage
{
    private const string SettingsFileName = "settings.json";
    private const string SettingsMutexName = @"Local\TwitchAI.Settings.v1";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly Dictionary<string, string> _settings;

    public JsonLocalAppStorage()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Der lokale Windows-Anwendungsdatenordner ist nicht verfügbar.");
        }

        LocalFolderPath = Path.Combine(localAppData, "TwitchAI");
        Directory.CreateDirectory(LocalFolderPath);
        _settingsPath = Path.Combine(LocalFolderPath, SettingsFileName);
        _settings = LoadSettings(_settingsPath);
    }

    public string LocalFolderPath { get; }

    public string? GetString(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return _settings.GetValueOrDefault(key);
        }
    }

    public void SetString(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        SetStrings(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key] = value,
        });
    }

    public void SetStrings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (key, value) in values)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
        }

        lock (_gate)
        {
            using var settingsMutex = new Mutex(
                initiallyOwned: false,
                SettingsMutexName);
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = settingsMutex.WaitOne(TimeSpan.FromSeconds(5));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    throw new IOException(
                        "Die lokalen Einstellungen werden bereits von einem anderen Prozess geschrieben.");
                }

                var mergedSettings = LoadSettings(_settingsPath);
                foreach (var (key, value) in values)
                {
                    mergedSettings[key] = value;
                }

                SaveSettings(mergedSettings);
                _settings.Clear();
                foreach (var (key, value) in mergedSettings)
                {
                    _settings[key] = value;
                }
            }
            finally
            {
                if (ownsMutex)
                {
                    settingsMutex.ReleaseMutex();
                }
            }
        }
    }

    private static Dictionary<string, string> LoadSettings(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path, Encoding.UTF8),
                SerializerOptions);
            return loaded is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(loaded, StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void SaveSettings(IReadOnlyDictionary<string, string> settings)
    {
        var temporaryPath = Path.Combine(
            LocalFolderPath,
            $"{SettingsFileName}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json, Utf8WithoutBom);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
