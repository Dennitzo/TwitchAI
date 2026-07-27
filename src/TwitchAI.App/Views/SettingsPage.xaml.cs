using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.ViewModels;

namespace TwitchAI.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = App.Current.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RefreshLmStudioCredentialStatusAsync();
        }
        catch
        {
            // The status text already explains that the Credential Locker
            // could not be read; page loading must remain non-blocking.
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double availableWidth)
    {
        var useCompactLayout = availableWidth < 800;
        PageContent.Margin = useCompactLayout
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 48);

        UpdateStatusHeader(
            TwitchStatus,
            TwitchHeaderStatusColumn,
            useCompactLayout);
        UpdateStatusHeader(
            LmStudioStatus,
            LmStudioHeaderStatusColumn,
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

        LmStudioModelColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(LmStudioModelInput, useCompactLayout ? 0 : 1);
        Grid.SetRow(LmStudioModelInput, useCompactLayout ? 1 : 0);

        LmStudioTokenActionsColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(LmStudioTokenActions, useCompactLayout ? 0 : 1);
        Grid.SetRow(LmStudioTokenActions, useCompactLayout ? 1 : 0);
        LmStudioTokenActions.Orientation = useCompactLayout
            ? Orientation.Vertical
            : Orientation.Horizontal;
        LmStudioTokenActions.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        BatchWaitColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        BatchQueueColumn.Width = useCompactLayout
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(BatchWaitInput, useCompactLayout ? 0 : 1);
        Grid.SetRow(BatchWaitInput, useCompactLayout ? 1 : 0);
        Grid.SetColumn(BatchQueueInput, useCompactLayout ? 0 : 2);
        Grid.SetRow(BatchQueueInput, useCompactLayout ? 2 : 0);

        SaveActionColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(SaveSettingsButton, useCompactLayout ? 0 : 1);
        Grid.SetRow(SaveSettingsButton, useCompactLayout ? 1 : 0);
        SaveSettingsButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
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

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Save();
            App.Current.ApplyTheme(ViewModel.SelectedTheme);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Einstellungen ungültig", exception.Message);
        }
    }

    private void OnTwitchClientIdLostFocus(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.SaveTwitchClientId();
        }
        catch (Exception exception)
        {
            ViewModel.TwitchClientIdPersistenceStatus =
                $"Client-ID konnte nicht gespeichert werden: {exception.Message}";
        }
    }

    private async void OnConnectTwitchClicked(object sender, RoutedEventArgs e)
    {
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
            var content = new StackPanel
            {
                Spacing = 12,
            };
            content.Children.Add(
                new TextBlock
                {
                    Text = "Öffne die Twitch-Seite und gib diesen einmaligen Code ein:",
                    TextWrapping = TextWrapping.Wrap,
                });
            content.Children.Add(
                new TextBlock
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 26,
                    Text = authorization.UserCode,
                });
            content.Children.Add(
                new HyperlinkButton
                {
                    Content = authorization.VerificationUri.ToString(),
                    NavigateUri = authorization.VerificationUri,
                });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Twitch Device Code",
                Content = content,
                PrimaryButtonText = "Freigabe abgeschlossen",
                CloseButtonText = "Abbrechen",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.CompleteDeviceAuthorizationAsync(authorization);
                await ShowMessageAsync(
                    "Twitch verbunden",
                    "Konto, Moderator-ID und Zielkanal wurden über Twitch verifiziert.");
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Twitch-Verbindung fehlgeschlagen", exception.Message);
        }
    }

    private async void OnCheckTwitchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.CheckTwitchAsync();
            await ShowMessageAsync("Twitch-Verbindung", ViewModel.TwitchConnectionStatus);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Twitch-Prüfung fehlgeschlagen", exception.Message);
        }
    }

    private async void OnTestLmStudioClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var models = await ViewModel.CheckLmStudioAsync();
            await ShowMessageAsync(
                "LM-Studio-Verbindung",
                models.Count == 0
                    ? "LM Studio antwortet, meldet aber kein geladenes Modell."
                    : $"Geladene Modelle:\n{string.Join("\n", models)}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("LM Studio nicht erreichbar", exception.Message);
        }
    }

    private async void OnSaveLmStudioTokenClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SaveLmStudioTokenAsync(LmStudioTokenBox.Password);
            LmStudioTokenBox.Password = string.Empty;
            await ShowMessageAsync(
                "LM-Studio-Token gespeichert",
                "Der optionale API-Token liegt ausschließlich im Windows Credential Locker.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Token konnte nicht gespeichert werden", exception.Message);
        }
    }

    private async void OnDeleteLmStudioTokenClicked(object sender, RoutedEventArgs e)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "LM-Studio-Token entfernen?",
            Content =
                "Der gespeicherte Token wird aus dem Windows Credential Locker entfernt.",
            PrimaryButtonText = "Token entfernen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await ViewModel.DeleteLmStudioTokenAsync();
            LmStudioTokenBox.Password = string.Empty;
            await ShowMessageAsync(
                "LM-Studio-Token entfernt",
                "LM Studio wird jetzt ohne Authorization-Header angesprochen.");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("Token konnte nicht entfernt werden", exception.Message);
        }
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
}
