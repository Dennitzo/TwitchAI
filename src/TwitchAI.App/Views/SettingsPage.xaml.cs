using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.Storage;

namespace TwitchAI.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _isPageLoaded;
    private int _twitchOperationInProgress;

    public SettingsPage()
    {
        ViewModel = App.Current.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = true;
        try { await ViewModel.CheckLocalAiAsync(); }
        catch (Exception exception) { ViewModel.LocalAiConnectionStatus = exception.Message; }
    }

    private void OnReasoningSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPageLoaded && sender is ComboBox { SelectedItem: string value }
            && !string.IsNullOrWhiteSpace(value))
        {
            ViewModel.ReasoningEffort = value;
        }
    }

    private void OnReadOnlyModeToggled(object sender, RoutedEventArgs e)
    {
        if (!_isPageLoaded || sender is not ToggleSwitch toggle)
        {
            return;
        }

        ViewModel.ReadOnlyMode = toggle.IsOn;
    }

    private async void OnExportDatabaseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"TwitchAI-Datenstand-{DateTime.Now:yyyyMMdd-HHmmss}.db"
            };
            picker.FileTypeChoices.Add("SQLite-Datenbank", new List<string> { ".db" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await ViewModel.ExportDatabaseAsync(file.Path);
            await ShowMessageAsync("Export abgeschlossen", "Der vollständige Datenstand wurde exportiert.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Export nicht möglich", exception.Message);
        }
    }

    private async void OnImportDatabaseClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".db");
            picker.FileTypeFilter.Add(".sqlite");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var confirmation = new ContentDialog
            {
                Title = "Datenbank importieren?",
                Content = "Der aktuelle Datenstand wird vorher gesichert und durch die ausgewählte Datei ersetzt. Konfigurationseinträge werden aktiviert.",
                PrimaryButtonText = "Importieren",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ImportDatabaseAsync(file.Path);
            await ShowMessageAsync("Import abgeschlossen", "Der Datenstand wurde importiert. Bitte TwitchAI neu starten.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Import nicht möglich", exception.Message);
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double availableWidth)
    {
        var useCompactLayout = availableWidth < 800;
        PageHeader.Margin = useCompactLayout
            ? new Thickness(72, 20, 16, 18)
            : new Thickness(28, 24, 28, 18);
        PageContent.Margin = useCompactLayout
            ? new Thickness(72, 0, 16, 40)
            : new Thickness(28, 0, 28, 48);

        UpdateStatusHeader(
            TwitchStatus,
            TwitchHeaderStatusColumn,
            useCompactLayout);
        UpdateStatusHeader(
            LocalAiStatus,
            LocalAiHeaderStatusColumn,
            useCompactLayout);

        TwitchChannelColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(ChannelLoginInput, useCompactLayout ? 0 : 1);
        Grid.SetRow(ChannelLoginInput, useCompactLayout ? 1 : 0);

        TwitchActions.ColumnDefinitions[0].Width = useCompactLayout
            ? new GridLength(1, GridUnitType.Star)
            : GridLength.Auto;
        TwitchCheckActionColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(CheckTwitchButton, useCompactLayout ? 0 : 1);
        Grid.SetRow(CheckTwitchButton, useCompactLayout ? 1 : 0);
        ConnectTwitchButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        CheckTwitchButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        SaveActionColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(SaveSettingsButton, useCompactLayout ? 0 : 1);
        Grid.SetRow(SaveSettingsButton, useCompactLayout ? 1 : 0);
        SaveSettingsButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
        Grid.SetRow(SaveStatusText, useCompactLayout ? 2 : 1);
    }

    private static void UpdateStatusHeader(
        FrameworkElement status,
        ColumnDefinition statusColumn,
        bool useCompactLayout)
    {
        statusColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(status, useCompactLayout ? 0 : 1);
        Grid.SetRow(status, useCompactLayout ? 1 : 0);
        status.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
    }

    private bool _saveInProgress;

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (_saveInProgress || !TryBeginTwitchOperation())
        {
            return;
        }

        _saveInProgress = true;
        SettingsScrollViewer.IsEnabled = false;
        try
        {
            await ViewModel.SaveAsync();
            App.Current.ApplyTheme(ViewModel.SelectedTheme);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Speichern nicht möglich", exception.Message);
        }
        finally
        {
            _saveInProgress = false;
            EndTwitchOperation();
            SettingsScrollViewer.IsEnabled = true;
        }
    }

    private void OnTwitchClientIdLostFocus(object sender, RoutedEventArgs e)
    {
        ViewModel.TwitchClientIdPersistenceStatus =
            ViewModel.TwitchClientId.Length == 0
                ? "Keine Client-ID eingetragen."
                : "Client-ID wird erst mit 'Speichern' übernommen.";
    }

    private async void OnConnectTwitchClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBeginTwitchOperation())
        {
            return;
        }

        try
        {
            var restoredSession = await ViewModel.RestoreTwitchSessionAsync();
            if (restoredSession.IsConnected)
            {
                await ShowMessageAsync(
                    "Twitch bereits verbunden",
                    restoredSession.Message);
                return;
            }

            if (!restoredSession.MayStartDeviceAuthorization)
            {
                await ShowMessageAsync(
                    "Twitch-Verbindung",
                    restoredSession.Message);
                return;
            }

            var authorization = await ViewModel.StartDeviceAuthorizationAsync();
            if (await RunDeviceAuthorizationDialogAsync(authorization))
            {
                await ShowMessageAsync(
                    "Twitch verbunden",
                    "Konto, Moderator-ID und Zielkanäle wurden über Twitch verifiziert.");
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Twitch-Verbindung fehlgeschlagen", exception.Message);
        }
        finally
        {
            EndTwitchOperation();
        }
    }

    private async void OnCheckTwitchClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBeginTwitchOperation())
        {
            return;
        }

        try
        {
            await ViewModel.CheckTwitchAsync();
            await ShowMessageAsync("Twitch-Verbindung", ViewModel.TwitchConnectionStatus);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Twitch-Prüfung fehlgeschlagen", exception.Message);
        }
        finally
        {
            EndTwitchOperation();
        }
    }

    private async Task<bool> RunDeviceAuthorizationDialogAsync(
        TwitchAI.Infrastructure.Authentication.TwitchDeviceAuthorization authorization)
    {
        using var cancellation = new CancellationTokenSource();
        var browserStatus = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "MutedTextBrush"],
            Text = "Die Twitch-Aktivierungsseite wird automatisch geöffnet.",
            TextWrapping = TextWrapping.Wrap,
        };
        var copyStatus = new TextBlock
        {
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "MutedTextBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var codeBox = new TextBox
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 26,
            HorizontalTextAlignment = TextAlignment.Center,
            IsReadOnly = true,
            MinWidth = 260,
            Text = authorization.UserCode,
        };
        AutomationProperties.SetName(
            codeBox,
            "Einmaliger Twitch-Gerätecode");

        var copyButton = new Button
        {
            Content = "Code kopieren",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(copyButton, "Twitch-Gerätecode kopieren");
        copyButton.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(authorization.UserCode);
            Clipboard.SetContent(package);
            copyStatus.Text = "Code wurde in die Zwischenablage kopiert.";
        };

        var progress = new ProgressRing
        {
            IsActive = true,
            Width = 18,
            Height = 18,
        };
        var pollingStatus = new TextBlock
        {
            Text = "Warte automatisch auf die Freigabe bei Twitch …",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pollingRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        pollingRow.Children.Add(progress);
        pollingRow.Children.Add(pollingStatus);

        var content = new StackPanel
        {
            Spacing = 12,
        };
        content.Children.Add(
            new TextBlock
            {
                Text = "Gib auf der geöffneten Twitch-Seite exakt diesen einmaligen Code ein:",
                TextWrapping = TextWrapping.Wrap,
            });
        content.Children.Add(codeBox);
        content.Children.Add(copyButton);
        content.Children.Add(copyStatus);
        content.Children.Add(
            new TextBlock
            {
                Text = $"Gültig bis {authorization.ExpiresAt.ToLocalTime():dd.MM.yyyy HH:mm:ss} Uhr (lokale Zeit)",
                TextWrapping = TextWrapping.Wrap,
            });
        content.Children.Add(
            new HyperlinkButton
            {
                Content = authorization.VerificationUri.AbsoluteUri,
                NavigateUri = authorization.VerificationUri,
                Padding = new Thickness(0),
            });
        content.Children.Add(browserStatus);
        content.Children.Add(pollingRow);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Twitch-Gerät aktivieren",
            Content = content,
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };

        // Start polling before showing the browser. This ensures that a Twitch
        // approval is observed immediately and does not rely on another click.
        var pollingTask = ViewModel.CompleteDeviceAuthorizationAsync(
            authorization,
            cancellation.Token);
        var dialogTask = ShowDialogAsync(dialog);

        try
        {
            var browserOpened = await Launcher.LaunchUriAsync(
                authorization.VerificationUri);
            if (!browserOpened)
            {
                browserStatus.Text =
                    "Der Browser konnte nicht automatisch geöffnet werden. Öffne den angezeigten Link manuell.";
            }

            var firstCompleted = await Task.WhenAny(pollingTask, dialogTask);
            if (firstCompleted == dialogTask && !pollingTask.IsCompleted)
            {
                cancellation.Cancel();
                await ObserveExpectedCancellationAsync(pollingTask, cancellation.Token);
                return false;
            }

            await pollingTask;
            return true;
        }
        finally
        {
            if (!pollingTask.IsCompleted)
            {
                cancellation.Cancel();
            }

            if (!dialogTask.IsCompleted)
            {
                dialog.Hide();
            }

            await ObserveDialogCompletionAsync(dialogTask);
            await ObserveExpectedCancellationAsync(pollingTask, cancellation.Token);
        }
    }

    private bool TryBeginTwitchOperation()
    {
        if (_saveInProgress
            || Interlocked.CompareExchange(ref _twitchOperationInProgress, 1, 0) != 0)
        {
            return false;
        }

        ConnectTwitchButton.IsEnabled = false;
        CheckTwitchButton.IsEnabled = false;
        SaveSettingsButton.IsEnabled = false;
        return true;
    }

    private void EndTwitchOperation()
    {
        Interlocked.Exchange(ref _twitchOperationInProgress, 0);
        ConnectTwitchButton.IsEnabled = true;
        CheckTwitchButton.IsEnabled = true;
        SaveSettingsButton.IsEnabled = true;
    }

    private static async Task<ContentDialogResult> ShowDialogAsync(
        ContentDialog dialog)
    {
        return await dialog.ShowAsync();
    }

    private static async Task ObserveDialogCompletionAsync(
        Task<ContentDialogResult> dialogTask)
    {
        try
        {
            await dialogTask;
        }
        catch (OperationCanceledException)
        {
            // The dialog can be canceled while its page or window is closing.
        }
    }

    private static async Task ObserveExpectedCancellationAsync(
        Task pollingTask,
        CancellationToken cancellationToken)
    {
        try
        {
            await pollingTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // User cancellation is expected and has been fully observed here.
        }
    }

    private async void OnTestLocalAiClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var models = await ViewModel.CheckLocalAiAsync();
            await ShowMessageAsync(
                "Lokale-AI-Verbindung",
                models.Count == 0
                    ? "Lokale AI antwortet, meldet aber kein verfügbares Modell."
                    : $"Verfügbare Modelle:\n{string.Join("\n", models)}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Lokale AI nicht erreichbar", exception.Message);
        }
    }

    private async void OnRefreshLocalAiModelsClicked(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            var models = await ViewModel.CheckLocalAiAsync();
            await ShowMessageAsync(
                "Lokale AI-Modelle",
                models.Count == 0
                    ? "Im Hugging-Face-Cache wurde kein vollständiges GGUF-Modell gefunden."
                    : $"Verfügbare Modelle:\n{string.Join("\n", models)}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(
                "Modelle konnten nicht geladen werden",
                exception.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var messageText = new TextBlock
        {
            IsTextSelectionEnabled = true,
            MaxWidth = 520,
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(messageText, title);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = messageText,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 240,
                MaxWidth = 540,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "Schließen",
        };
        await dialog.ShowAsync();
    }
}
