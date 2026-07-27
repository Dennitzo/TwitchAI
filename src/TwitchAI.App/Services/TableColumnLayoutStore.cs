using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinUI.TableView;

namespace TwitchAI.App.Services;

public readonly record struct TableColumnLayoutKey(
    string Scope,
    string Column);

public interface ITableColumnLayoutStore
{
    bool TryGetWidth(
        string scope,
        string column,
        out GridLength width);

    void SaveWidths(
        IReadOnlyDictionary<TableColumnLayoutKey, GridLength> widths);
}

/// <summary>
/// Stores each table column width under a separate package-independent key.
/// Separate keys allow Debug, Release, and Portable instances to merge changes
/// without replacing the layouts of unrelated tables.
/// </summary>
public sealed class TableColumnLayoutStore : ITableColumnLayoutStore
{
    private const string SettingsKeyPrefix = "TableColumnWidth.v1.";
    private readonly ILocalAppStorage _storage;

    public TableColumnLayoutStore(ILocalAppStorage storage)
    {
        _storage = storage;
    }

    public bool TryGetWidth(
        string scope,
        string column,
        out GridLength width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return TryDeserialize(
            _storage.GetString(GetSettingsKey(scope, column)),
            out width);
    }

    public void SaveWidths(
        IReadOnlyDictionary<TableColumnLayoutKey, GridLength> widths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0)
        {
            return;
        }

        var settings = new Dictionary<string, string>(
            widths.Count,
            StringComparer.Ordinal);
        foreach (var (key, width) in widths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Scope);
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Column);
            if (TrySerialize(width, out var serialized))
            {
                settings[GetSettingsKey(key.Scope, key.Column)] = serialized;
            }
        }

        if (settings.Count == 0)
        {
            return;
        }

        try
        {
            _storage.SetStrings(settings);
        }
        catch (IOException)
        {
            // Resizing remains available when local settings cannot be written.
        }
        catch (UnauthorizedAccessException)
        {
            // Resizing remains available when local settings cannot be written.
        }
    }

    private static string GetSettingsKey(
        string scope,
        string column) =>
        $"{SettingsKeyPrefix}{scope}.{column}";

    private static bool TrySerialize(
        GridLength width,
        out string serialized)
    {
        if (width.IsAuto)
        {
            serialized = "auto";
            return true;
        }

        if (!double.IsFinite(width.Value) || width.Value <= 0)
        {
            serialized = string.Empty;
            return false;
        }

        var prefix = width.IsStar ? "star:" : "pixel:";
        serialized = prefix + width.Value.ToString(
            "0.###",
            CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryDeserialize(
        string? serialized,
        out GridLength width)
    {
        if (string.Equals(serialized, "auto", StringComparison.Ordinal))
        {
            width = GridLength.Auto;
            return true;
        }

        var separator = serialized?.IndexOf(':') ?? -1;
        if (separator <= 0
            || !double.TryParse(
                serialized![(separator + 1)..],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            || !double.IsFinite(value)
            || value <= 0)
        {
            width = default;
            return false;
        }

        var unit = serialized[..separator] switch
        {
            "pixel" when value <= 2_000 => GridUnitType.Pixel,
            "star" when value <= 100 => GridUnitType.Star,
            _ => (GridUnitType?)null,
        };
        if (unit is null)
        {
            width = default;
            return false;
        }

        width = new GridLength(value, unit.Value);
        return true;
    }
}

/// <summary>
/// Connects a TableView's live column widths to the persistent layout store.
/// Changes are debounced to avoid writing settings for every pointer pixel.
/// </summary>
public sealed class TableColumnWidthPersistenceController
{
    private readonly TableView _table;
    private readonly Func<string> _scopeProvider;
    private readonly ITableColumnLayoutStore _store;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly Dictionary<TableColumnLayoutKey, GridLength> _pending = [];
    private bool _isApplyingStoredWidths;

    public TableColumnWidthPersistenceController(
        TableView table,
        Func<string> scopeProvider,
        ITableColumnLayoutStore store)
    {
        _table = table;
        _scopeProvider = scopeProvider;
        _store = store;
        _saveTimer = table.DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += OnSaveTimerTick;
        table.Columns.ColumnPropertyChanged += OnColumnPropertyChanged;
        table.Unloaded += OnTableUnloaded;
    }

    public void ApplyStoredWidths()
    {
        var scope = _scopeProvider();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        _isApplyingStoredWidths = true;
        try
        {
            foreach (var column in _table.Columns)
            {
                if (column.Tag is string columnName
                    && _store.TryGetWidth(
                        scope,
                        columnName,
                        out var storedWidth))
                {
                    column.Width = storedWidth;
                }
            }
        }
        finally
        {
            _isApplyingStoredWidths = false;
        }
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (_pending.Count == 0)
        {
            return;
        }

        var snapshot = new Dictionary<TableColumnLayoutKey, GridLength>(
            _pending);
        _pending.Clear();
        _store.SaveWidths(snapshot);
    }

    private void OnColumnPropertyChanged(
        object? sender,
        TableViewColumnPropertyChangedEventArgs args)
    {
        if (_isApplyingStoredWidths
            || !string.Equals(
                args.PropertyName,
                nameof(TableViewColumn.Width),
                StringComparison.Ordinal)
            || args.Column.Tag is not string columnName)
        {
            return;
        }

        var scope = _scopeProvider();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        _pending[new TableColumnLayoutKey(scope, columnName)] =
            args.Column.Width;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        Flush();
    }

    private void OnTableUnloaded(
        object sender,
        RoutedEventArgs args)
    {
        Flush();
    }
}
