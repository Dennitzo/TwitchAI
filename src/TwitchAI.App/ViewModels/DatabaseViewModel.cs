using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using TwitchAI.App.Services;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.App.ViewModels;

public enum DatabaseSaveOutcome
{
    NothingToSave,
    Saved,
    ValidationFailed,
    ConcurrencyConflict,
}

public sealed record DatabaseSaveResult(
    DatabaseSaveOutcome Outcome,
    string Message,
    string? DraftJson = null);

public sealed partial class DatabaseViewModel : ObservableObject
{
    private const string SortScopePrefix = "database.";
    private const string DatabaseEditorAuditReason =
        "Manuelle Änderung im Datenbankeditor";

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["action_attempts"] = "Twitch-Versuche",
            ["ai_run_metrics"] = "AI-Läufe",
            ["blacklist_rules"] = "Blacklist-Regeln",
            ["channel_context"] = "Kanalkontext",
            ["chat_message_snapshots"] = "Nachrichten-Snapshots",
            ["chat_reply_proposals"] = "Chat-Antwortvorschläge",
            ["chat_reply_templates"] = "Chat-Antwortvorlagen",
            ["configuration_audit"] = "Konfigurationsaudit",
            ["data_deletion_audit"] = "Lösch-Audit",
            ["message_availability_events"] = "Verfügbarkeitsereignisse",
            ["message_clear_watermarks"] = "Clear-Watermarks",
            ["moderation_cases"] = "Moderationsfälle",
            ["moderation_feedback"] = "Moderationsfeedback",
            ["phrase_feedback"] = "Phrasenfeedback",
            ["phrase_rules"] = "Blacklist-Phrasen",
            ["policy_category_exclusions"] = "Kategorieausnahmen",
            ["policy_sets"] = "Regelsets",
            ["processed_eventsub_events"] = "EventSub-Deduplizierung",
            ["record_annotations"] = "Datensatzanmerkungen",
            ["schema_migrations"] = "Migrationen",
            ["twitch_categories"] = "Twitch-Kategorien",
            ["whitelist_phrases"] = "Whitelist",
        };

    private readonly IDatabaseCatalog _catalog;
    private readonly IDatabaseTableReader _reader;
    private readonly IDatabaseTableEditor _editor;
    private readonly IDatabaseRowDeletionService _rowDeletion;
    private readonly IUniversalDatabaseEditor _universalEditor;
    private readonly ITableSortPreferenceStore _sortPreferences;
    private readonly string _addRowLabel = "Zeile hinzufügen";
    private readonly string _deleteActionLabel = "Zeile löschen";
    private readonly List<DatabaseRowItem> _loadedRows = [];
    private DatabaseTableDefinition? _selectedDefinition;
    private Dictionary<string, DatabaseColumnMetadata> _columnMetadata =
        new Dictionary<string, DatabaseColumnMetadata>(StringComparer.Ordinal);
    private string _primaryKeyColumnName = string.Empty;
    private CancellationTokenSource? _loadCancellation;
    private DatabaseRowDeletionPreview? _pendingPhysicalDeletion;
    private int _currentOffset;
    private string? _appliedSearchText;

    public DatabaseViewModel(
        IDatabaseCatalog catalog,
        IDatabaseTableReader reader,
        IDatabaseTableEditor editor,
        IDatabaseRowDeletionService rowDeletion,
        IUniversalDatabaseEditor universalEditor,
        ITableSortPreferenceStore sortPreferences)
    {
        _catalog = catalog;
        _reader = reader;
        _editor = editor;
        _rowDeletion = rowDeletion;
        _universalEditor = universalEditor;
        _sortPreferences = sortPreferences;

        Tables = new ObservableCollection<DatabaseTableItem>(
            catalog.Tables.Select(CreateTableItem));
        SelectedTable = Tables.FirstOrDefault();
        _selectedDefinition = SelectedTable is null
            ? null
            : catalog.GetRequired(SelectedTable.Name);
        _primaryKeyColumnName = _selectedDefinition?.PrimaryKey ?? string.Empty;
    }

    public ObservableCollection<DatabaseTableItem> Tables { get; }

    public ObservableCollection<DatabaseRowItem> Rows { get; } = [];

    public ObservableCollection<DatabaseRowItem> SelectedRows { get; } = [];

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100];

    public IReadOnlyList<string> SortDirections { get; } =
        ["Aufsteigend", "Absteigend"];

    public ObservableCollection<string> SortColumns { get; } = [];

    public IReadOnlyList<string> ColumnNames { get; private set; } = [];

    public string PrimaryKeyColumnName => _primaryKeyColumnName;

    public double GetPreferredColumnWidth(string columnName)
    {
        if (!_columnMetadata.TryGetValue(columnName, out var metadata))
        {
            return 180;
        }

        if (columnName.EndsWith("_id", StringComparison.Ordinal) ||
            columnName.EndsWith("_version", StringComparison.Ordinal))
        {
            return 150;
        }

        if (columnName.EndsWith("_utc", StringComparison.Ordinal) ||
            columnName.Contains("timestamp", StringComparison.Ordinal))
        {
            return 220;
        }

        if (columnName.Contains("message", StringComparison.Ordinal) ||
            columnName.Contains("reason", StringComparison.Ordinal) ||
            columnName.Contains("phrase", StringComparison.Ordinal) ||
            columnName.Contains("json", StringComparison.Ordinal) ||
            columnName.Contains("fingerprint", StringComparison.Ordinal) ||
            columnName.Contains("hash", StringComparison.Ordinal))
        {
            return 320;
        }

        return HasNumericAffinity(metadata.DeclaredType) ? 130 : 190;
    }

    public string SelectedTableDisplayName =>
        SelectedTable?.DisplayName ?? "Tabelle auswählen";

    public string SelectedTableName => SelectedTable?.Name ?? "—";

    public string SelectedTableAccess => SelectedTable?.Access ?? "—";

    public bool IsAppendOnlyTable =>
        _selectedDefinition?.AccessClass == DatabaseTableAccessClass.AppendOnly;

    public string AddRowLabel => _addRowLabel;

    public bool CanToggleEdit => IsEditUnlocked && false;

    public bool CanReload => !IsBusy;

    public bool CanAddRow => !IsBusy && SelectedTable is not null;

    public bool CanDeleteSelected =>
        !IsBusy
        && SelectedTable is not null
        && SelectedRows.Count > 0;

    public bool CanSave => HasPendingChanges && !IsBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasRows => Rows.Count > 0;

    public bool CanExport => HasRows && !IsBusy;

    public bool HasPendingPhysicalDeletion =>
        _pendingPhysicalDeletion is not null;

    public string PageSummary =>
        TotalCount == 0
            ? $"Seite 1 · 0 von 0 Zeilen · {PageSize} pro Seite"
            : $"Seite {PageNumber} · {_currentOffset + 1}–{_currentOffset + Rows.Count} von {TotalCount:N0} · {PageSize} pro Seite";

    public string DeleteActionLabel => _deleteActionLabel;

    [ObservableProperty]
    public partial DatabaseTableItem? SelectedTable { get; set; }

    [ObservableProperty]
    public partial DatabaseRowItem? SelectedRow { get; set; }

    [ObservableProperty]
    public partial bool IsEditUnlocked { get; set; } = true;

    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedSortColumn { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedSortDirection { get; set; } = "Aufsteigend";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        "Eine Tabelle auswählen, um Datensätze zu laden.";

    [ObservableProperty]
    public partial string SelectedRowJson { get; set; } =
        "Kein Datensatz ausgewählt.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int PageNumber { get; set; } = 1;

    [ObservableProperty]
    public partial int PageSize { get; set; } = 50;

    [ObservableProperty]
    public partial long TotalCount { get; set; }

    [ObservableProperty]
    public partial bool CanGoPrevious { get; set; }

    [ObservableProperty]
    public partial bool CanGoNext { get; set; }

    public event EventHandler? TableLayoutChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedTable is not null)
        {
            await SelectTableAsync(SelectedTable, cancellationToken);
        }
    }

    public async Task SelectTableAsync(
        DatabaseTableItem table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        SelectedTable = table;
        _selectedDefinition = _catalog.GetRequired(table.Name);
        _primaryKeyColumnName = _selectedDefinition.PrimaryKey;
        OnPropertyChanged(nameof(PrimaryKeyColumnName));
        OnPropertyChanged(nameof(IsAppendOnlyTable));
        OnPropertyChanged(nameof(AddRowLabel));
        IsEditUnlocked = true;
        DiscardDrafts();
        SearchText = string.Empty;
        _appliedSearchText = null;
        var sortScope = GetSortScope(table.Name);
        var hasStoredSortPreference = _sortPreferences.TryGet(
            sortScope,
            out var sortPreference);
        sortPreference ??= new TableSortPreference(
            _selectedDefinition.PrimaryKey,
            Descending: false);
        SelectedSortColumn = sortPreference.Column;
        SelectedSortDirection = sortPreference.Descending
            ? "Absteigend"
            : "Aufsteigend";
        await LoadFirstPageAsync(
            allowStoredSortFallback: hasStoredSortPreference,
            cancellationToken: cancellationToken);
        NotifySelectionState();
    }

    public async Task LoadFirstPageAsync(CancellationToken cancellationToken = default)
    {
        await LoadFirstPageAsync(
            allowStoredSortFallback: false,
            cancellationToken: cancellationToken);
    }

    private async Task LoadFirstPageAsync(
        bool allowStoredSortFallback,
        CancellationToken cancellationToken)
    {
        _currentOffset = 0;
        PageNumber = 1;
        await LoadPageAsync(
            _currentOffset,
            cancellationToken,
            allowStoredSortFallback);
    }

    public Task ApplyQueryAsync(CancellationToken cancellationToken = default)
    {
        _appliedSearchText = string.IsNullOrEmpty(SearchText)
            ? null
            : SearchText;
        SaveCurrentSortPreferenceIfValid();
        return LoadFirstPageAsync(cancellationToken);
    }

    public Task SortByColumnAsync(
        string columnName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        if (!SortColumns.Contains(columnName, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Spalte '{columnName}' kann nicht sortiert werden.",
                nameof(columnName));
        }

        if (StringComparer.Ordinal.Equals(columnName, SelectedSortColumn))
        {
            SelectedSortDirection = SelectedSortDirection == "Aufsteigend"
                ? "Absteigend"
                : "Aufsteigend";
        }
        else
        {
            SelectedSortColumn = columnName;
            SelectedSortDirection = "Aufsteigend";
        }

        return ApplyQueryAsync(cancellationToken);
    }

    private void SaveCurrentSortPreferenceIfValid()
    {
        if (SelectedTable is null
            || !SortColumns.Contains(
                SelectedSortColumn,
                StringComparer.Ordinal))
        {
            return;
        }

        _sortPreferences.Save(
            GetSortScope(SelectedTable.Name),
            SelectedSortColumn,
            SelectedSortDirection == "Absteigend");
    }

    public Task ReloadCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        return LoadPageAsync(_currentOffset, cancellationToken);
    }

    public async Task GoNextAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoNext)
        {
            return;
        }

        _currentOffset += PageSize;
        PageNumber++;
        await LoadPageAsync(_currentOffset, cancellationToken);
    }

    public async Task GoPreviousAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoPrevious)
        {
            return;
        }

        _currentOffset = Math.Max(0, _currentOffset - PageSize);
        PageNumber = _currentOffset / PageSize + 1;
        await LoadPageAsync(_currentOffset, cancellationToken);
    }

    public async Task ChangePageSizeAsync(
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!PageSizes.Contains(pageSize))
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (PageSize == pageSize)
        {
            return;
        }

        PageSize = pageSize;
        await LoadFirstPageAsync(cancellationToken);
    }

    public void ToggleEdit()
    {
        IsEditUnlocked = true;
        StatusMessage =
            "Direkte Bearbeitung ist dauerhaft aktiv; Änderungen werden automatisch gespeichert.";
        NotifySelectionState();
    }

    public void AddRow()
    {
        if (!CanAddRow || _selectedDefinition is null)
        {
            return;
        }

        var cells = ColumnNames.Select(
                columnName =>
                {
                    var metadata = _columnMetadata[columnName];
                    var isEditable = true;
                    var initialValue =
                        isEditable && HasNumericAffinity(metadata.DeclaredType)
                            ? "0"
                            : string.Empty;
                    return new DatabaseCellItem(
                        columnName,
                        null,
                        initialValue,
                        isEditable);
                })
            .ToArray();
        var row = new DatabaseRowItem(
            cells,
            primaryKeyValue: null,
            entityId: null,
            rowVersion: null,
            isArchived: false,
            isNew: true);
        AttachRow(row);
        _loadedRows.Insert(0, row);
        PublishLoadedRows();
        SelectedRow = row;
        SetSelectedRows([row]);
        RefreshDraftState();
    }

    public void SetSelectedRows(IEnumerable<DatabaseRowItem> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        SelectedRows.Clear();
        foreach (var row in rows.Distinct())
        {
            SelectedRows.Add(row);
        }

        SelectedRow = SelectedRows.LastOrDefault();
        NotifySelectionState();
    }

    public async Task AutoSaveRowAsync(
        DatabaseRowItem row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (IsBusy || SelectedTable is null)
        {
            return;
        }

        var changedCells = row.Cells
            .Where(cell =>
                cell.IsEditable
                && (row.IsNew
                    ? cell.DisplayValue.Length > 0
                    : cell.IsChanged))
            .ToArray();
        if (changedCells.Length == 0)
        {
            return;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            foreach (var cell in changedCells)
            {
                values[cell.ColumnName] = ParseUniversalValue(
                    _columnMetadata[cell.ColumnName],
                    cell.DisplayValue);
            }
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Der Wert konnte noch nicht automatisch gespeichert werden.";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            await _universalEditor.SaveRowAsync(
                SelectedTable.Name,
                PrimaryKeyColumnName,
                row.PrimaryKeyValue,
                values,
                cancellationToken);
            StatusMessage = row.IsNew
                ? "Zeile automatisch hinzugefügt."
                : "Änderung automatisch gespeichert.";
            await ReloadCurrentPageAsync(cancellationToken);
        }
        catch (SqliteException exception)
        {
            ErrorMessage = exception.Message;
            StatusMessage = row.IsNew
                ? "Weitere Pflichtfelder ausfüllen; die neue Zeile ist noch nicht gespeichert."
                : "Die Änderung wurde von SQLite abgelehnt.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Die Änderung konnte nicht automatisch gespeichert werden.";
        }
        finally
        {
            IsBusy = false;
            NotifySelectionState();
        }
    }

    public async Task<int> DeleteSelectedRowsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanDeleteSelected || SelectedTable is null)
        {
            return 0;
        }

        var selected = SelectedRows.ToArray();
        foreach (var draft in selected.Where(item => item.IsNew))
        {
            DetachRow(draft);
            _loadedRows.Remove(draft);
        }

        var keys = selected
            .Where(item => !item.IsNew && item.PrimaryKeyValue is not null)
            .Select(item => item.PrimaryKeyValue!)
            .ToArray();
        var deleted = selected.Length - keys.Length;
        if (keys.Length > 0)
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            try
            {
                deleted += await _universalEditor.DeleteRowsAsync(
                    SelectedTable.Name,
                    PrimaryKeyColumnName,
                    keys,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ErrorMessage = exception.Message;
                StatusMessage = "Die ausgewählten Zeilen konnten nicht gelöscht werden.";
                return 0;
            }
            finally
            {
                IsBusy = false;
            }
        }

        SelectedRows.Clear();
        SelectedRow = null;
        await ReloadCurrentPageAsync(cancellationToken);
        StatusMessage = $"{deleted:N0} Zeile(n) gelöscht.";
        return deleted;
    }

    private static object? ParseUniversalValue(
        DatabaseColumnMetadata metadata,
        string displayValue)
    {
        if (string.IsNullOrEmpty(displayValue))
        {
            return null;
        }

        var type = metadata.DeclaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            if (!long.TryParse(
                    displayValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var integer))
            {
                throw new FormatException($"{metadata.Name} erwartet eine ganze Zahl.");
            }

            return integer;
        }

        if (type.Contains("REAL", StringComparison.Ordinal)
            || type.Contains("FLOA", StringComparison.Ordinal)
            || type.Contains("DOUB", StringComparison.Ordinal)
            || type.Contains("NUM", StringComparison.Ordinal)
            || type.Contains("DEC", StringComparison.Ordinal))
        {
            if (!double.TryParse(
                    displayValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                throw new FormatException($"{metadata.Name} erwartet eine Zahl.");
            }

            return number;
        }

        return displayValue;
    }

    public void ToggleSoftDeleteSelected()
    {
        if (!CanDeleteSelected
            || SelectedTable?.SupportsSoftArchive != true
            || SelectedRow is null)
        {
            return;
        }

        if (SelectedRow.IsNew)
        {
            DetachRow(SelectedRow);
            _loadedRows.Remove(SelectedRow);
            SelectedRow = null;
            PublishLoadedRows();
            RefreshDraftState();
            return;
        }

        SelectedRow.ToggleArchived();
        RefreshDraftState();
    }

    public async Task<DatabaseRowDeletionPreview> StageSelectedDeletionAsync(
        CancellationToken cancellationToken = default)
    {
        var request = CreatePhysicalDeletionRequest();
        IsBusy = true;
        try
        {
            var preview = await _rowDeletion
                .PreviewAsync(request, cancellationToken)
                .ConfigureAwait(true);
            if (preview.CanDelete)
            {
                _pendingPhysicalDeletion = preview;
                StatusMessage =
                    $"Die physische Löschung von {request.TableName} · ID {request.RecordId} ist als Entwurf vorgemerkt.";
                RefreshDraftState();
            }

            return preview;
        }
        finally
        {
            IsBusy = false;
            NotifySelectionState();
        }
    }

    private DatabaseRowDeletionRequest CreatePhysicalDeletionRequest()
    {
        if (!CanDeleteSelected
            || SelectedTable is null
            || SelectedTable.SupportsSoftArchive
            || SelectedRow?.PrimaryKeyValue is null)
        {
            throw new InvalidOperationException(
                "Bitte eine physisch löschbare Datenbankzeile auswählen und offene Entwürfe zuerst speichern oder verwerfen.");
        }

        return new DatabaseRowDeletionRequest(
            SelectedTable.Name,
            Convert.ToString(
                SelectedRow.PrimaryKeyValue,
                CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException(
                "Die ausgewählte Zeile besitzt keine gültige ID."));
    }

    public void DiscardDrafts()
    {
        _pendingPhysicalDeletion = null;
        foreach (var row in _loadedRows.Where(item => !item.IsNew))
        {
            row.ResetDraft();
        }

        foreach (var row in _loadedRows.Where(item => item.IsNew).ToArray())
        {
            DetachRow(row);
            _loadedRows.Remove(row);
        }

        PublishLoadedRows();
        RefreshDraftState();
    }

    public async Task<DatabaseSaveResult> SaveAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HasPendingChanges)
        {
            return new DatabaseSaveResult(
                DatabaseSaveOutcome.NothingToSave,
                "Es liegen keine Änderungen vor.");
        }

        if (_pendingPhysicalDeletion is { } deletionPreview)
        {
            if (_loadedRows.Any(
                    row => row.DraftState != DatabaseDraftState.None))
            {
                return ValidationFailure(
                    "Eine physische Löschung kann nicht gemeinsam mit anderen Datenbankänderungen gespeichert werden.");
            }

            if (!deletionPreview.CanDelete
                || string.IsNullOrWhiteSpace(deletionPreview.ConfirmationToken))
            {
                return ValidationFailure(
                    deletionPreview.BlockReason
                    ?? "Die Löschvorschau erlaubt keine physische Löschung.");
            }

            IsBusy = true;
            ErrorMessage = string.Empty;
            NotifySelectionState();
            try
            {
                var result = await _rowDeletion
                    .DeleteAsync(
                        deletionPreview.Request,
                        deletionPreview.ConfirmationToken,
                        cancellationToken)
                    .ConfigureAwait(true);
                _pendingPhysicalDeletion = null;
                StatusMessage =
                    $"{result.DeletedRows.Sum(item => item.RowCount):N0} Zeile(n) wurden physisch gelöscht.";
                if (result.PositiveFeedbackReverted != 0
                    || result.NegativeFeedbackReverted != 0)
                {
                    StatusMessage +=
                        $" Lernfeedback zurückgerechnet: {result.PositiveFeedbackReverted:N0} positiv, "
                        + $"{result.NegativeFeedbackReverted:N0} negativ.";
                }

                await ReloadCurrentPageAsync(cancellationToken);
                return new DatabaseSaveResult(
                    DatabaseSaveOutcome.Saved,
                    StatusMessage);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                ErrorMessage = exception.Message;
                StatusMessage =
                    "Die physische Löschung wurde nicht ausgeführt.";
                return new DatabaseSaveResult(
                    DatabaseSaveOutcome.ValidationFailed,
                    exception.Message);
            }
            finally
            {
                IsBusy = false;
                NotifySelectionState();
            }
        }

        if (_selectedDefinition is null ||
            _selectedDefinition.AccessClass is not (
                DatabaseTableAccessClass.UserEditable or
                DatabaseTableAccessClass.AppendOnly))
        {
            return ValidationFailure("Die ausgewählte Tabelle ist nicht direkt bearbeitbar.");
        }

        if (_selectedDefinition.AccessClass == DatabaseTableAccessClass.AppendOnly &&
            _loadedRows.Any(
                row => row.DraftState is not (
                    DatabaseDraftState.None or
                    DatabaseDraftState.Insert)))
        {
            return ValidationFailure(
                "Append-only-Tabellen erlauben ausschließlich neue Datensätze.");
        }

        IReadOnlyList<StagedDatabaseChange> changes;
        try
        {
            changes = BuildChangeSet(_selectedDefinition);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or OverflowException)
        {
            return ValidationFailure(exception.Message);
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        NotifySelectionState();
        try
        {
            var result = await _editor.ApplyAsync(
                changes,
                DatabaseEditorAuditReason,
                cancellationToken);
            var changeCount = result.Changes.Count;
            IsEditUnlocked = false;
            StatusMessage =
                $"{changeCount} Änderung(en) atomar gespeichert und auditiert.";
            await ReloadCurrentPageAsync(cancellationToken);
            return new DatabaseSaveResult(DatabaseSaveOutcome.Saved, StatusMessage);
        }
        catch (DatabaseConcurrencyException exception)
        {
            ErrorMessage =
                "Der Datensatz wurde zwischenzeitlich geändert. Es wurde nichts überschrieben.";
            StatusMessage = ErrorMessage;
            return new DatabaseSaveResult(
                DatabaseSaveOutcome.ConcurrencyConflict,
                ErrorMessage,
                BuildDraftJson(exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = exception.Message;
            StatusMessage = "Die Transaktion wurde vollständig zurückgerollt.";
            return new DatabaseSaveResult(
                DatabaseSaveOutcome.ValidationFailed,
                exception.Message);
        }
        finally
        {
            IsBusy = false;
            NotifySelectionState();
        }
    }

    public string BuildChangeSummary()
    {
        var lines = new List<string>();
        if (_pendingPhysicalDeletion is { } deletion)
        {
            lines.Add(
                $"{deletion.Request.TableName} · ID {deletion.Request.RecordId}: endgültig physisch löschen");
            foreach (var impact in deletion.Impacts)
            {
                lines.Add(
                    $"  {impact.TableName}: {impact.RowCount:N0} betroffene Zeile(n)");
            }

            lines.Add(
                "  Diese Löschung ist irreversibel und macht bereits ausgeführte Twitch-Aktionen nicht rückgängig.");
        }

        foreach (var row in _loadedRows.Where(item => item.DraftState != DatabaseDraftState.None))
        {
            var identity = row.IsNew
                ? "neuer Datensatz"
                : $"{SelectedTable?.PrimaryKey}={row.PrimaryKeyValue}";
            lines.Add($"{identity}: {row.DraftLabel}");
            if (row.DraftState is DatabaseDraftState.Insert or DatabaseDraftState.Update)
            {
                foreach (var cell in row.Cells.Where(
                             item => item.IsEditable &&
                                     (row.IsNew || item.IsChanged)))
                {
                    var before = row.IsNew
                        ? "∅"
                        : FormatDisplayValue(cell.OriginalValue, null);
                    lines.Add($"  {cell.ColumnName}: {before} → {cell.DisplayValue}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string BuildDraftJson(string? note = null)
    {
        var payload = new
        {
            table = SelectedTable?.Name,
            reason = DatabaseEditorAuditReason,
            note,
            physicalDeletion = _pendingPhysicalDeletion is null
                ? null
                : new
                {
                    table = _pendingPhysicalDeletion.Request.TableName,
                    recordId = _pendingPhysicalDeletion.Request.RecordId,
                    impacts = _pendingPhysicalDeletion.Impacts,
                },
            changes = _loadedRows
                .Where(item => item.DraftState != DatabaseDraftState.None)
                .Select(row => new
                {
                    operation = row.DraftState.ToString(),
                    entityId = row.EntityId,
                    expectedRowVersion = row.RowVersion,
                    values = row.Cells
                        .Where(cell => cell.IsEditable && (row.IsNew || cell.IsChanged))
                        .ToDictionary(cell => cell.ColumnName, cell => cell.DisplayValue),
                }),
        };
        return JsonSerializer.Serialize(payload, IndentedJsonOptions);
    }

    public bool IsColumnEditable(string columnName)
    {
        return _columnMetadata.ContainsKey(columnName);
    }

    public void RefreshDraftState()
    {
        HasPendingChanges =
            _pendingPhysicalDeletion is not null
            || _loadedRows.Any(
                item => item.DraftState != DatabaseDraftState.None);
        OnPropertyChanged(nameof(HasPendingPhysicalDeletion));
        if (SelectedRow is not null)
        {
            SelectedRowJson = SerializeRow(SelectedRow);
        }

        NotifySelectionState();
    }

    partial void OnSelectedTableChanged(DatabaseTableItem? value)
    {
        OnPropertyChanged(nameof(SelectedTableDisplayName));
        OnPropertyChanged(nameof(SelectedTableName));
        OnPropertyChanged(nameof(SelectedTableAccess));
        OnPropertyChanged(nameof(IsAppendOnlyTable));
        OnPropertyChanged(nameof(AddRowLabel));
        NotifySelectionState();
    }

    partial void OnSelectedRowChanged(DatabaseRowItem? value)
    {
        SelectedRowJson = value is null
            ? "Kein Datensatz ausgewählt."
            : SerializeRow(value);
        OnPropertyChanged(nameof(DeleteActionLabel));
        NotifySelectionState();
    }

    partial void OnIsEditUnlockedChanged(bool value)
    {
        NotifySelectionState();
    }

    partial void OnHasPendingChangesChanged(bool value)
    {
        NotifySelectionState();
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifySelectionState();
    }

    private async Task LoadPageAsync(
        int offset,
        CancellationToken cancellationToken,
        bool allowStoredSortFallback = false)
    {
        if (SelectedTable is null)
        {
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _loadCancellation.Token;
        var requestedTable = SelectedTable;
        var requestedDefinition = _catalog.GetRequired(requestedTable.Name);

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = $"{requestedTable.DisplayName} wird geladen …";
        NotifySelectionState();
        try
        {
            var requestedSortDirection = SelectedSortDirection == "Absteigend"
                ? DatabaseSortDirection.Descending
                : DatabaseSortDirection.Ascending;
            DatabaseTableQuery CreateQuery(
                string sortColumn,
                DatabaseSortDirection sortDirection) =>
                new(
                    PageSize: PageSize,
                    Offset: offset,
                    SearchText: _appliedSearchText,
                    SortColumn: sortColumn,
                    SortDirection: sortDirection,
                    PaginationMode: DatabasePaginationMode.Offset);

            DatabasePage page;
            try
            {
                page = await _reader.ReadPageAsync(
                    requestedTable.Name,
                    CreateQuery(SelectedSortColumn, requestedSortDirection),
                    token);
            }
            catch (ArgumentException exception) when (
                allowStoredSortFallback &&
                string.Equals(
                    exception.ParamName,
                    "requestedColumn",
                    StringComparison.Ordinal))
            {
                // A column persisted by an older build may have been renamed or
                // removed. Retry exactly once with the catalogued primary key;
                // the reader validates it against the live PRAGMA schema before
                // it is ever used as a SQL identifier.
                SelectedSortColumn = requestedDefinition.PrimaryKey;
                SelectedSortDirection = "Aufsteigend";
                page = await _reader.ReadPageAsync(
                    requestedTable.Name,
                    CreateQuery(
                        requestedDefinition.PrimaryKey,
                        DatabaseSortDirection.Ascending),
                    token);
            }

            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(requestedTable, SelectedTable))
            {
                return;
            }

            ClearLoadedRows();
            _columnMetadata = page.Columns.ToDictionary(
                item => item.Name,
                StringComparer.Ordinal);
            var primaryKeyColumns = page.Columns
                .Where(item => item.IsPrimaryKey)
                .ToArray();
            if (primaryKeyColumns.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Tabelle '{requestedTable.Name}' muss genau eine " +
                    "Primärschlüsselspalte besitzen.");
            }

            var primaryKey = primaryKeyColumns[0].Name;
            _primaryKeyColumnName = primaryKey;
            OnPropertyChanged(nameof(PrimaryKeyColumnName));

            ColumnNames =
            [
                primaryKey,
                .. page.Columns
                    .Select(item => item.Name)
                    .Where(
                        columnName => !StringComparer.Ordinal.Equals(
                            columnName,
                            primaryKey)),
            ];
            SortColumns.Clear();
            foreach (var columnName in ColumnNames)
            {
                if (_columnMetadata[columnName].IsSortable)
                {
                    SortColumns.Add(columnName);
                }
            }

            SelectedSortColumn = page.SortColumn;
            SelectedSortDirection =
                page.SortDirection == DatabaseSortDirection.Descending
                    ? "Absteigend"
                    : "Aufsteigend";
            _sortPreferences.Save(
                GetSortScope(requestedTable.Name),
                SelectedSortColumn,
                page.SortDirection == DatabaseSortDirection.Descending);
            foreach (var databaseRow in page.Rows)
            {
                var row = CreateRow(
                    databaseRow,
                    _selectedDefinition!,
                    ColumnNames,
                    primaryKey);
                AttachRow(row);
                _loadedRows.Add(row);
            }

            _currentOffset = page.Offset;
            PageNumber = _currentOffset / PageSize + 1;
            TotalCount = page.TotalCount;
            CanGoPrevious = _currentOffset > 0;
            CanGoNext = page.HasMore;
            requestedTable.RowCountLabel = _appliedSearchText is null
                ? $"{page.TotalCount:N0} Zeilen"
                : $"{page.TotalCount:N0} Treffer";
            PublishLoadedRows();
            StatusMessage = page.Rows.Count == 0
                ? "Die serverseitige Abfrage enthält keine Datensätze."
                : $"{page.Rows.Count} von {page.TotalCount:N0} Datensätzen serverseitig geladen.";
            TableLayoutChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer table or page request owns the visible state.
        }
        catch (Exception exception)
        {
            ClearLoadedRows();
            Rows.Clear();
            ErrorMessage = exception.Message;
            StatusMessage = "Die Tabelle konnte nicht geladen werden.";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
                NotifySelectionState();
            }
        }
    }

    private static string GetSortScope(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return $"{SortScopePrefix}{tableName}";
    }

    private List<StagedDatabaseChange> BuildChangeSet(
        DatabaseTableDefinition definition)
    {
        var changes = new List<StagedDatabaseChange>();
        foreach (var row in _loadedRows.Where(item => item.DraftState != DatabaseDraftState.None))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (row.DraftState is DatabaseDraftState.Insert or DatabaseDraftState.Update)
            {
                foreach (var cell in row.Cells.Where(
                             item => item.IsEditable &&
                                     (row.IsNew || item.IsChanged)))
                {
                    var column = definition.EditableColumns[cell.ColumnName];
                    values[cell.ColumnName] = ParseEditorValue(column, cell.DisplayValue);
                }
            }

            changes.Add(
                new StagedDatabaseChange(
                    definition.Name,
                    row.DraftState switch
                    {
                        DatabaseDraftState.Insert => StagedDatabaseOperation.Insert,
                        DatabaseDraftState.Update => StagedDatabaseOperation.Update,
                        DatabaseDraftState.Archive => StagedDatabaseOperation.Archive,
                        DatabaseDraftState.Restore => StagedDatabaseOperation.Restore,
                        _ => throw new InvalidOperationException("Ungültiger Datenbankentwurf."),
                    },
                    row.EntityId,
                    row.RowVersion,
                    values));
        }

        return changes;
    }

    private static object? ParseEditorValue(
        DatabaseColumnDefinition column,
        string displayValue)
    {
        if (displayValue.Length == 0 && !column.Required)
        {
            return null;
        }

        return column.Kind switch
        {
            DatabaseColumnKind.Text => displayValue,
            DatabaseColumnKind.WholeNumber => long.Parse(
                displayValue,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture),
            DatabaseColumnKind.Real => double.Parse(
                displayValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture),
            DatabaseColumnKind.Boolean => ParseBoolean(column.Name, displayValue),
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
    }

    private static bool ParseBoolean(string columnName, string displayValue)
    {
        return displayValue.ToLowerInvariant() switch
        {
            "1" or "true" or "ja" => true,
            "0" or "false" or "nein" => false,
            _ => throw new FormatException(
                $"Spalte '{columnName}' erwartet true/false oder 1/0."),
        };
    }

    private static DatabaseTableItem CreateTableItem(DatabaseTableDefinition definition)
    {
        var access = definition.AccessClass switch
        {
            DatabaseTableAccessClass.UserEditable => "Bearbeitbar",
            DatabaseTableAccessClass.AppendOnly => "Nur anfügen",
            DatabaseTableAccessClass.ApplicationManaged => "Workflow",
            DatabaseTableAccessClass.AuditReadOnly => "Schreibgeschützt",
            _ => definition.AccessClass.ToString(),
        };
        return new DatabaseTableItem(
            definition.Name,
            DisplayNames.GetValueOrDefault(definition.Name, definition.Name),
            access,
            definition.PrimaryKey,
            definition.AccessClass is
                DatabaseTableAccessClass.UserEditable or
                DatabaseTableAccessClass.AppendOnly,
            definition.SupportsSoftArchive);
    }

    private static DatabaseRowItem CreateRow(
        DatabaseRow databaseRow,
        DatabaseTableDefinition definition,
        IReadOnlyList<string> columnNames,
        string primaryKeyColumnName)
    {
        var values = databaseRow.Values;
        var cells = columnNames.Select(
                columnName =>
                {
                    values.TryGetValue(columnName, out var rawValue);
                    definition.EditableColumns.TryGetValue(columnName, out var editableColumn);
                    return new DatabaseCellItem(
                        columnName,
                        rawValue,
                        FormatDisplayValue(rawValue, editableColumn),
                        editableColumn is not null &&
                        definition.AccessClass ==
                        DatabaseTableAccessClass.UserEditable);
                })
            .ToArray();

        values.TryGetValue(primaryKeyColumnName, out var keyValue);
        long? entityId = definition.AccessClass == DatabaseTableAccessClass.UserEditable &&
                         keyValue is not null
            ? Convert.ToInt64(keyValue, CultureInfo.InvariantCulture)
            : null;
        long? rowVersion = values.TryGetValue("row_version", out var versionValue) &&
                           versionValue is not null
            ? Convert.ToInt64(versionValue, CultureInfo.InvariantCulture)
            : null;
        var isArchived = values.TryGetValue("archived_utc", out var archivedValue) &&
                         archivedValue is not null;
        return new DatabaseRowItem(
            cells,
            keyValue,
            entityId,
            rowVersion,
            isArchived,
            isNew: false);
    }

    private static string FormatDisplayValue(
        object? rawValue,
        DatabaseColumnDefinition? definition)
    {
        if (rawValue is null)
        {
            return string.Empty;
        }

        if (definition?.Kind == DatabaseColumnKind.Boolean)
        {
            return Convert.ToInt64(rawValue, CultureInfo.InvariantCulture) == 0
                ? "false"
                : "true";
        }

        return Convert.ToString(rawValue, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool HasNumericAffinity(string declaredType)
    {
        return declaredType.Contains("INT", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("REAL", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("FLOA", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("DOUB", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("NUM", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("DEC", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("BOOL", StringComparison.OrdinalIgnoreCase);
    }

    private void PublishLoadedRows()
    {
        Rows.Clear();
        foreach (var row in _loadedRows)
        {
            Rows.Add(row);
        }

        if (SelectedRow is not null && !Rows.Contains(SelectedRow))
        {
            SelectedRow = null;
        }

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(PageSummary));
    }

    private void AttachRow(DatabaseRowItem row)
    {
        row.DraftChanged += OnRowDraftChanged;
    }

    private void DetachRow(DatabaseRowItem row)
    {
        row.DraftChanged -= OnRowDraftChanged;
    }

    private void ClearLoadedRows()
    {
        foreach (var row in _loadedRows)
        {
            DetachRow(row);
        }

        _loadedRows.Clear();
        SelectedRow = null;
        HasPendingChanges = false;
    }

    private void OnRowDraftChanged(object? sender, EventArgs e)
    {
        RefreshDraftState();
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(CanToggleEdit));
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(CanAddRow));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(DeleteActionLabel));
        OnPropertyChanged(nameof(PageSummary));
    }

    private DatabaseSaveResult ValidationFailure(string message)
    {
        ErrorMessage = message;
        StatusMessage = "Der Entwurf wurde nicht gespeichert.";
        return new DatabaseSaveResult(DatabaseSaveOutcome.ValidationFailed, message);
    }

    private static string SerializeRow(DatabaseRowItem row)
    {
        var values = row.Cells.ToDictionary(
            item => item.ColumnName,
            item => string.IsNullOrEmpty(item.DisplayValue)
                ? null
                : item.DisplayValue,
            StringComparer.Ordinal);
        var payload = new
        {
            state = row.DraftLabel,
            rowVersion = row.RowVersion,
            values,
        };
        return JsonSerializer.Serialize(payload, IndentedJsonOptions);
    }
}
