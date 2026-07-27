using System.Text.Json;

namespace TwitchAI.App.Services;

public sealed record TableSortPreference(
    string Column,
    bool Descending);

public interface ITableSortPreferenceStore
{
    bool TryGet(
        string scope,
        out TableSortPreference preference);

    TableSortPreference GetOrDefault(
        string scope,
        string defaultColumn,
        bool defaultDescending,
        IReadOnlySet<string>? validColumns = null);

    void Save(
        string scope,
        string column,
        bool descending);
}

/// <summary>
/// Persists every table sort choice under its own package-independent settings key.
/// Per-scope keys prevent concurrently running Debug, Release, or Portable builds
/// from overwriting unrelated table preferences with an older aggregate snapshot.
/// </summary>
public sealed class TableSortPreferenceStore : ITableSortPreferenceStore
{
    private const string SettingsKeyPrefix = "TableSortPreference.v1.";
    private const string LegacyAggregateSettingsKey = "TableSortPreferences.v1";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly ILocalAppStorage _storage;
    private readonly Dictionary<string, TableSortPreference>
        _legacyPreferences;

    public TableSortPreferenceStore(ILocalAppStorage storage)
    {
        _storage = storage;
        _legacyPreferences = LoadLegacyPreferences(
            storage.GetString(LegacyAggregateSettingsKey));
    }

    public bool TryGet(
        string scope,
        out TableSortPreference preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            if (TryDeserializePreference(
                    _storage.GetString(GetSettingsKey(scope)),
                    out var stored)
                || _legacyPreferences.TryGetValue(scope, out stored))
            {
                preference = stored;
                return true;
            }
        }

        preference = null!;
        return false;
    }

    public TableSortPreference GetOrDefault(
        string scope,
        string defaultColumn,
        bool defaultDescending,
        IReadOnlySet<string>? validColumns = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultColumn);

        if (TryGet(scope, out var preference)
            && (validColumns is null ||
                validColumns.Contains(preference.Column)))
        {
            return preference;
        }

        return new TableSortPreference(defaultColumn, defaultDescending);
    }

    public void Save(
        string scope,
        string column,
        bool descending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        lock (_gate)
        {
            var json = JsonSerializer.Serialize(
                new TableSortPreference(column, descending),
                SerializerOptions);
            try
            {
                _storage.SetString(GetSettingsKey(scope), json);
            }
            catch (IOException)
            {
                // Sorting must remain usable when local settings cannot be written.
            }
            catch (UnauthorizedAccessException)
            {
                // Sorting must remain usable when local settings cannot be written.
            }
        }
    }

    private static string GetSettingsKey(string scope) =>
        $"{SettingsKeyPrefix}{scope}";

    private static bool TryDeserializePreference(
        string? json,
        out TableSortPreference preference)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<TableSortPreference>(
                    json,
                    SerializerOptions);
                if (loaded is not null
                    && !string.IsNullOrWhiteSpace(loaded.Column))
                {
                    preference = loaded;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Invalid or obsolete values fall back to the legacy/default path.
            }
        }

        preference = null!;
        return false;
    }

    private static Dictionary<string, TableSortPreference>
        LoadLegacyPreferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, TableSortPreference>(
                StringComparer.Ordinal);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<
                Dictionary<string, TableSortPreference>>(
                json,
                SerializerOptions);
            if (loaded is null)
            {
                return new Dictionary<string, TableSortPreference>(
                    StringComparer.Ordinal);
            }

            return loaded
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Key) &&
                    item.Value is not null &&
                    !string.IsNullOrWhiteSpace(item.Value.Column))
                .ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, TableSortPreference>(
                StringComparer.Ordinal);
        }
    }
}
