using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure.Persistence;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class AuditPage : Page
{
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
    private readonly TableColumnWidthPersistenceController
        _columnWidthPersistence;

    public AuditPage()
    {
        ViewModel = App.Current.GetService<AuditViewModel>();
        InitializeComponent();
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                AuditTable,
                static () => "audit",
                App.Current.GetService<ITableColumnLayoutStore>());
        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
    }

    public AuditViewModel ViewModel { get; }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useCompactLayout = e.NewSize.Width < 900;
        AuditRoot.Margin = useCompactLayout
            ? new Thickness(72, 16, 12, 24)
            : new Thickness(28, 24, 28, 40);
        ToolbarSecondRow.Height = useCompactLayout
            ? GridLength.Auto
            : new GridLength(0);

        ToolbarSearchColumn.Width = new GridLength(1, GridUnitType.Star);
        ToolbarResultColumn.Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(170);
        ToolbarPolicyColumn.Width = useCompactLayout
            ? GridLength.Auto
            : new GridLength(170);
        ToolbarExportColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;

        Grid.SetRow(AuditSearchBox, 0);
        Grid.SetColumn(AuditSearchBox, 0);
        Grid.SetColumnSpan(AuditSearchBox, useCompactLayout ? 3 : 1);

        Grid.SetRow(ResultFilterBox, useCompactLayout ? 1 : 0);
        Grid.SetColumn(ResultFilterBox, useCompactLayout ? 0 : 1);

        Grid.SetRow(PolicyFilterBox, useCompactLayout ? 1 : 0);
        Grid.SetColumn(PolicyFilterBox, useCompactLayout ? 1 : 2);

        Grid.SetRow(AuditExportButton, useCompactLayout ? 1 : 0);
        Grid.SetColumn(AuditExportButton, useCompactLayout ? 2 : 3);
    }

    private void OnAuditTableSorting(
        object sender,
        TableViewSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is not string columnName)
        {
            return;
        }

        ViewModel.SortByColumn(columnName);
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in AuditTable.Columns)
        {
            column.SortDirection =
                column.Tag is string columnName
                && string.Equals(
                    columnName,
                    ViewModel.SortColumn,
                    StringComparison.Ordinal)
                    ? ViewModel.SortDescending
                        ? SortDirection.Descending
                        : SortDirection.Ascending
                    : null;
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Kontrollierter Export",
            Content =
                "Der Export enthält Username, Originalnachricht, Moderationsgrund und Twitch-Ergebnis. Speichere die Datei nur an einem geschützten Ort.",
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
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName =
                    $"TwitchAI-Audit-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);
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

            var exported = ViewModel.FilteredItems.ToArray();
            var json = JsonSerializer.Serialize(
                new
                {
                    envelopeVersion = "twitchai.audit.export.v1",
                    exportedAtUtc = DateTimeOffset.UtcNow,
                    filters = new
                    {
                        ViewModel.SearchText,
                        ViewModel.ResultFilter,
                        ViewModel.PolicyFilter,
                    },
                    records = exported,
                },
                ExportJsonOptions);
            await FileIO.WriteTextAsync(file, json);
            await RecordExportAuditAsync(exported.Length);

            var completed = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Export abgeschlossen",
                Content = $"{exported.Length:N0} Datensätze wurden als JSON gespeichert.",
                CloseButtonText = "Schließen",
            };
            await completed.ShowAsync();
        }
        catch (Exception exception)
        {
            var failed = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Export fehlgeschlagen",
                Content = exception.Message,
                CloseButtonText = "Schließen",
            };
            await failed.ShowAsync();
        }
    }

    private static async Task RecordExportAuditAsync(int recordCount)
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
                'audit_export', @entity_id, 'insert',
                NULL, @after_json, NULL, @reason, @committed_utc);
            """;
        command.Parameters.AddWithValue("@entity_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue(
            "@after_json",
            JsonSerializer.Serialize(
                new
                {
                    format = "json",
                    records = recordCount,
                }));
        command.Parameters.AddWithValue(
            "@reason",
            "Manuell bestätigter Export des Ausführungslogs");
        command.Parameters.AddWithValue(
            "@committed_utc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
