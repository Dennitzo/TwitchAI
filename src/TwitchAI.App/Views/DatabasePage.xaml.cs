using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure.Persistence;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class DatabasePage : Page
{
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    private bool _isLoaded;
    private bool _suppressTableSelection;
    private bool _suppressPageSizeSelection;
    private bool _isTableLayoutSubscribed;
    private readonly TableColumnWidthPersistenceController
        _columnWidthPersistence;
    private Task<bool>? _pendingNavigationConfirmation;
    private DatabaseTableItem? _activeTable;
    private string? _renderedTableName;

    public DatabasePage()
    {
        ViewModel = App.Current.GetService<DatabaseViewModel>();
        InitializeComponent();
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                DataTable,
                GetColumnLayoutScope,
                App.Current.GetService<ITableColumnLayoutStore>());
    }

    public DatabaseViewModel ViewModel { get; }

    public async Task<bool> ConfirmNavigationAwayAsync()
    {
        if (_pendingNavigationConfirmation is not null)
        {
            return await _pendingNavigationConfirmation;
        }

        _pendingNavigationConfirmation = ResolvePendingChangesAsync();
        try
        {
            return await _pendingNavigationConfirmation;
        }
        finally
        {
            _pendingNavigationConfirmation = null;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModelEvents();
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _suppressTableSelection = true;
        _suppressPageSizeSelection = true;
        TableList.SelectedItem = ViewModel.SelectedTable;
        PageSizeBox.SelectedItem = ViewModel.PageSize;
        _suppressPageSizeSelection = false;
        _suppressTableSelection = false;

        await ViewModel.InitializeAsync();
        _activeTable = ViewModel.SelectedTable;
        RebuildColumns();
        ApplyEditState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _columnWidthPersistence.Flush();
        UnsubscribeFromViewModelEvents();
    }

    private async void OnSearchSubmitted(
        AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!await ConfirmNavigationAwayAsync())
        {
            return;
        }

        await ViewModel.ApplyQueryAsync();
        RebuildColumns();
    }

    private async void OnDataTableSorting(
        object sender,
        TableViewSortingEventArgs e)
    {
        // The TableView's built-in sorter only knows the rows on the current
        // page. Handling the event here keeps sorting in SQLite, before
        // LIMIT/OFFSET are applied.
        e.Handled = true;
        if (!_isLoaded ||
            ViewModel.IsBusy ||
            e.Column.Tag is not string columnName ||
            !ViewModel.SortColumns.Contains(columnName))
        {
            return;
        }

        if (!await ConfirmNavigationAwayAsync())
        {
            RebuildColumns();
            return;
        }

        await ViewModel.SortByColumnAsync(columnName);
        RebuildColumns();
    }

    private async void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmNavigationAwayAsync())
        {
            return;
        }

        await ViewModel.ReloadCurrentPageAsync();
        RebuildColumns();
        ApplyEditState();
    }

    private void OnAddRowClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.AddRow();
        DataTable.SelectedItems.Clear();
        if (ViewModel.SelectedRow is not null)
        {
            DataTable.SelectedItems.Add(ViewModel.SelectedRow);
        }
        ApplyEditState();
    }

    private async void OnDeleteRowClicked(object sender, RoutedEventArgs e)
    {
        var table = ViewModel.SelectedTable;
        var rows = ViewModel.SelectedRows.ToArray();
        if (table is null || rows.Length == 0)
        {
            return;
        }

        try
        {
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"{rows.Length:N0} Zeile(n) löschen?",
                Content =
                    $"Die Auswahl aus „{table.DisplayName}“ wird sofort gelöscht. "
                    + "Direkt abhängige Datensätze werden ebenfalls entfernt.",
                PrimaryButtonText = "Zeile löschen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.DeleteSelectedRowsAsync();
            ApplyEditState();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Zeile konnte nicht gelöscht werden",
                exception.Message);
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(ViewModel.SelectedRowJson);
        Clipboard.SetContent(package);
        ViewModel.StatusMessage = "Der ausgewählte Datensatz wurde als JSON kopiert.";
    }

    private async void OnExportJsonClicked(object sender, RoutedEventArgs e)
    {
        await ExportCurrentPageAsync("json");
    }

    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        await ExportCurrentPageAsync("csv");
    }

    private async void OnTableSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressTableSelection ||
            TableList.SelectedItem is not DatabaseTableItem selected ||
            ReferenceEquals(selected, _activeTable))
        {
            return;
        }

        if (!await ConfirmNavigationAwayAsync())
        {
            RestoreActiveTableSelection();
            return;
        }

        await ViewModel.SelectTableAsync(selected);
        _activeTable = selected;
        RebuildColumns();
        ApplyEditState();
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedRows(
            DataTable.SelectedItems.OfType<DatabaseRowItem>());
        ApplyEditState();
    }

    private async void OnCellEditEnded(
        object sender,
        TableViewCellEditEndedEventArgs e)
    {
        if (e.DataItem is DatabaseRowItem row)
        {
            await ViewModel.AutoSaveRowAsync(row);
            RebuildColumns();
            ApplyEditState();
        }
    }

    private async void OnPreviousPageClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmNavigationAwayAsync())
        {
            return;
        }

        await ViewModel.GoPreviousAsync();
        RebuildColumns();
    }

    private async void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmNavigationAwayAsync())
        {
            return;
        }

        await ViewModel.GoNextAsync();
        RebuildColumns();
    }

    private async void OnPageSizeChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressPageSizeSelection ||
            PageSizeBox.SelectedItem is not int pageSize ||
            pageSize == ViewModel.PageSize)
        {
            return;
        }

        if (!await ConfirmNavigationAwayAsync())
        {
            _suppressPageSizeSelection = true;
            PageSizeBox.SelectedItem = ViewModel.PageSize;
            _suppressPageSizeSelection = false;
            return;
        }

        await ViewModel.ChangePageSizeAsync(pageSize);
        RebuildColumns();
    }

    private async Task<bool> ResolvePendingChangesAsync()
    {
        if (!ViewModel.HasPendingChanges)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Ungespeicherte Änderungen",
            Content =
                "Vor dem Wechsel müssen die Datenbankentwürfe gespeichert oder verworfen werden.",
            PrimaryButtonText = "Speichern",
            SecondaryButtonText = "Verwerfen",
            CloseButtonText = "Hier bleiben",
            DefaultButton = ContentDialogButton.Close,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                return await SaveWithConfirmationAsync();
            case ContentDialogResult.Secondary:
                ViewModel.DiscardDrafts();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> SaveWithConfirmationAsync()
    {
        var summary = ViewModel.BuildChangeSummary();
        var preview = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            IsReadOnly = true,
            MaxHeight = 360,
            MinWidth = 520,
            Text = summary,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ViewModel.HasPendingPhysicalDeletion
                ? "Physische Löschung endgültig ausführen?"
                : "Änderungen atomar speichern?",
            Content = preview,
            PrimaryButtonText = ViewModel.HasPendingPhysicalDeletion
                ? "Endgültig löschen"
                : "Speichern",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        var result = await ViewModel.SaveAsync();
        ApplyEditState();
        if (result.Outcome == DatabaseSaveOutcome.Saved)
        {
            return true;
        }

        if (result.Outcome == DatabaseSaveOutcome.ConcurrencyConflict)
        {
            await ShowConcurrencyDialogAsync(result);
            return false;
        }

        await ShowMessageAsync("Speichern nicht möglich", result.Message);
        return false;
    }

    private async Task ShowConcurrencyDialogAsync(DatabaseSaveResult result)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Row-Version-Konflikt",
            Content =
                "Der Datensatz wurde parallel geändert. Blindes Überschreiben ist gesperrt.",
            PrimaryButtonText = "Neu laden",
            SecondaryButtonText = "Änderungen kopieren",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.DiscardDrafts();
            await ViewModel.ReloadCurrentPageAsync();
            RebuildColumns();
            return;
        }

        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(result.DraftJson ?? ViewModel.BuildDraftJson());
        Clipboard.SetContent(package);
        ViewModel.StatusMessage =
            "Der Entwurf wurde kopiert; die Datenbank blieb unverändert.";
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Schließen",
        };
        await dialog.ShowAsync();
    }

    private async Task ExportCurrentPageAsync(string format)
    {
        if (!await ConfirmNavigationAwayAsync())
        {
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Datenbankseite exportieren?",
            Content =
                $"Die aktuelle Seite von „{ViewModel.SelectedTableDisplayName}“ enthält möglicherweise Chat- und Identitätsdaten. Exportiert werden {ViewModel.Rows.Count:N0} sichtbare Zeilen.",
            PrimaryButtonText = "Datei auswählen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var extension = $".{format}";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName =
                    $"TwitchAI-{ViewModel.SelectedTableName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add(format.ToUpperInvariant(), [extension]);
            var window = App.Current.MainWindow
                ?? throw new InvalidOperationException(
                    "Das Anwendungsfenster ist nicht verfügbar.");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            var rows = ViewModel.Rows
                .Select(row => row.Cells.ToDictionary(
                    cell => cell.ColumnName,
                    cell => cell.DisplayValue,
                    StringComparer.Ordinal))
                .ToArray();
            var content = format == "json"
                ? JsonSerializer.Serialize(
                    new
                    {
                        envelopeVersion = "twitchai.database.export.v1",
                        table = ViewModel.SelectedTableName,
                        page = ViewModel.PageNumber,
                        exportedAtUtc = DateTimeOffset.UtcNow,
                        rows,
                    },
                    ExportJsonOptions)
                : BuildCsv(ViewModel.ColumnNames, rows);
            await FileIO.WriteTextAsync(file, content);
            await RecordDatabaseExportAsync(format, rows.Length);
            ViewModel.StatusMessage =
                $"{rows.Length:N0} Zeilen der aktuellen Seite wurden als {format.ToUpperInvariant()} exportiert.";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Export fehlgeschlagen", exception.Message);
        }
    }

    private async Task RecordDatabaseExportAsync(string format, int rowCount)
    {
        var factory = App.Current.GetService<ISqliteConnectionFactory>();
        var writeCoordinator = App.Current.GetService<ISqliteWriteCoordinator>();
        await using var writeLease = await writeCoordinator.AcquireAsync();
        await using var connection = await factory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO configuration_audit(
                entity_type, entity_id, operation,
                before_json, after_json, scope_id, change_reason, committed_utc)
            VALUES(
                'database_export', @entity_id, 'insert',
                NULL, @after_json, NULL, @reason, @committed_utc);
            """;
        command.Parameters.AddWithValue("@entity_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue(
            "@after_json",
            JsonSerializer.Serialize(
                new
                {
                    table = ViewModel.SelectedTableName,
                    format,
                    rows = rowCount,
                    page = ViewModel.PageNumber,
                }));
        command.Parameters.AddWithValue(
            "@reason",
            "Manuell bestätigter Datenbankexport");
        command.Parameters.AddWithValue(
            "@committed_utc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static string BuildCsv(
        IReadOnlyList<string> columns,
        IReadOnlyList<Dictionary<string, string>> rows)
    {
        static string Escape(string value) =>
            $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", columns.Select(Escape)));
        foreach (var row in rows)
        {
            csv.AppendLine(
                string.Join(
                    ",",
                    columns.Select(
                        column => Escape(
                            row.GetValueOrDefault(column) ?? string.Empty))));
        }

        return csv.ToString();
    }

    private void OnTableLayoutChanged(object? sender, EventArgs e)
    {
        RebuildColumns();
    }

    private void SubscribeToViewModelEvents()
    {
        if (_isTableLayoutSubscribed)
        {
            return;
        }

        ViewModel.TableLayoutChanged += OnTableLayoutChanged;
        _isTableLayoutSubscribed = true;
    }

    private void UnsubscribeFromViewModelEvents()
    {
        if (!_isTableLayoutSubscribed)
        {
            return;
        }

        ViewModel.TableLayoutChanged -= OnTableLayoutChanged;
        _isTableLayoutSubscribed = false;
    }

    private void RebuildColumns()
    {
        // Persist the outgoing table before replacing its dynamic columns so
        // a quick table switch cannot lose a resize still in the debounce.
        _columnWidthPersistence.Flush();
        DataTable.Columns.Clear();
        var tableName = ViewModel.SelectedTableName;
        _renderedTableName = tableName;
        for (var index = 0; index < ViewModel.ColumnNames.Count; index++)
        {
            var columnName = ViewModel.ColumnNames[index];
            var isPrimaryKey = StringComparer.Ordinal.Equals(
                columnName,
                ViewModel.PrimaryKeyColumnName);
            var binding = new Binding
            {
                Mode = ViewModel.IsColumnEditable(columnName)
                    ? BindingMode.TwoWay
                    : BindingMode.OneWay,
                Path = new PropertyPath($"Cells[{index}].DisplayValue"),
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            };
            DataTable.Columns.Add(
                new TableViewTextColumn
                {
                    Binding = binding,
                    CanResize = true,
                    CanSort = ViewModel.SortColumns.Contains(columnName),
                    Header = CreateColumnHeader(columnName, isPrimaryKey),
                    IsReadOnly = !ViewModel.IsColumnEditable(columnName),
                    MaxWidth = 900,
                    MinWidth = 80,
                    Tag = columnName,
                    Width = new GridLength(
                        ViewModel.GetPreferredColumnWidth(columnName)),
                });
        }

        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
        AutomationProperties.SetHelpText(
            DataTable,
            $"Serverseitig nach {ViewModel.SelectedSortColumn} " +
            $"{ViewModel.SelectedSortDirection.ToLowerInvariant()} sortiert.");

        // Dynamic column headers are materialized after they are attached to
        // the TableView. Reapplying the direction on the dispatcher makes the
        // arrow visible even after a table change or reload.
        DispatcherQueue.TryEnqueue(UpdateSortIndicators);
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in DataTable.Columns)
        {
            column.SortDirection = null;
        }

        foreach (var column in DataTable.Columns)
        {
            if (column.Tag is string columnName &&
                StringComparer.Ordinal.Equals(
                    columnName,
                    ViewModel.SelectedSortColumn))
            {
                column.SortDirection =
                    ViewModel.SelectedSortDirection == "Absteigend"
                        ? SortDirection.Descending
                        : SortDirection.Ascending;
                break;
            }
        }
    }

    private static object CreateColumnHeader(string columnName, bool isPrimaryKey)
    {
        if (!isPrimaryKey)
        {
            return columnName;
        }

        var header = new TextBlock
        {
            Text = "ID",
        };
        AutomationProperties.SetName(
            header,
            $"ID, Primärschlüsselspalte {columnName}");
        AutomationProperties.SetHelpText(
            header,
            $"Echter Datenbankspaltenname: {columnName}");
        ToolTipService.SetToolTip(
            header,
            $"Primärschlüssel · {columnName}");
        return header;
    }

    private string GetColumnLayoutScope() =>
        string.IsNullOrWhiteSpace(_renderedTableName)
            ? string.Empty
            : $"database.{_renderedTableName}";

    private void ApplyEditState()
    {
        DataTable.IsReadOnly = false;
    }

    private void RestoreActiveTableSelection()
    {
        _suppressTableSelection = true;
        TableList.SelectedItem = _activeTable;
        _suppressTableSelection = false;
    }

}
