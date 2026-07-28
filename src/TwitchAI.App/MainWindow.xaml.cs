using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.App.Views;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace TwitchAI.App;

public sealed partial class MainWindow : Window
{
    private const string WindowWidthSetting = "WindowWidth";
    private const string WindowHeightSetting = "WindowHeight";
    private const string WindowWidthDipsSetting = "WindowWidthDipsV2";
    private const string WindowHeightDipsSetting = "WindowHeightDipsV2";
    private const string WindowLayoutVersionSetting = "WindowLayoutVersion";
    private const string NavigationPaneIsOpenSetting = "NavigationPaneIsOpen";
    private const string NavigationPaneWidthSetting = "NavigationPaneWidth";
    private const double DefaultWindowWidthDips = 1440;
    private const double DefaultWindowHeightDips = 900;
    private const double DefaultPaneWidth = 320;
    private const double MinimumPaneWidth = 280;
    private const double MaximumPaneWidth = 520;
    private const double MinimumContentWidth = 320;
    private const double KeyboardPaneResizeStep = 16;
    private const double PaneResizeHitTarget = 10;

    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal)
    {
        ["queue"] = typeof(ModerationQueuePage),
        ["blacklist"] = typeof(PoliciesPage),
        ["backseatgaming"] = typeof(BackseatgamingPage),
        ["whitelist"] = typeof(WhitelistPage),
        ["chat"] = typeof(ChatPage),
        ["database"] = typeof(DatabasePage),
        ["audit"] = typeof(AuditPage),
        ["settings"] = typeof(SettingsPage),
    };
    private readonly ILocalAppStorage _localStorage;
    private readonly DispatcherQueueTimer _layoutSaveTimer;
    private WindowId _windowId;
    private AppWindow? _appWindow;
    private SizeInt32 _lastRestoredWindowSize;
    private bool _preferredPaneIsOpen;
    private double _preferredPaneWidth;
    private bool _hasActivated;
    private bool _navigationLoaded;
    private bool _initialWindowSizeApplied;
    private bool _windowStateReady;
    private bool _navigationStateReady;
    private object? _committedNavigationItem;
    private bool _suppressNavigationSelection;
    private bool _navigationConfirmationPending;
    private bool _closeConfirmationPending;
    private bool _allowClose;
    private bool _isPaneResizePointerActive;
    private uint _paneResizePointerId;

    public MainWindow(
        ShellViewModel viewModel,
        ILocalAppStorage localStorage)
    {
        ViewModel = viewModel;
        _localStorage = localStorage;
        _preferredPaneIsOpen = ReadStoredPaneIsOpen();
        _preferredPaneWidth = ReadStoredPaneWidth();
        InitializeComponent();
        _layoutSaveTimer = DispatcherQueue.CreateTimer();
        _layoutSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _layoutSaveTimer.IsRepeating = false;
        _layoutSaveTimer.Tick += OnLayoutSaveTimerTick;
        ConfigureWindow();
        Activated += OnWindowActivated;
        Closed += OnClosed;
    }

    public ShellViewModel ViewModel { get; }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var windowHandle = WindowNative.GetWindowHandle(this);
        _windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        var iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "TwitchAI.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        _navigationLoaded = true;
        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        ApplyEffectivePaneWidth();
        RootNavigation.IsPaneOpen = _preferredPaneIsOpen;
        _navigationStateReady = true;
        UpdatePaneResizeThumb();
        TryApplyInitialWindowSize();
    }

    private void OnWindowActivated(
        object sender,
        WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            return;
        }

        _hasActivated = true;
        TryApplyInitialWindowSize();
    }

    private void OnNavigationPaneOpened(
        NavigationView sender,
        object args)
    {
        PaneResizeThumb.Visibility = Visibility.Visible;
        UpdatePaneResizeThumb();
        if (_navigationStateReady)
        {
            _preferredPaneIsOpen = true;
            SaveWindowAndPaneState();
        }
    }

    private void OnNavigationPaneClosed(
        NavigationView sender,
        object args)
    {
        PaneResizeThumb.Visibility = Visibility.Collapsed;
        if (_navigationStateReady)
        {
            _preferredPaneIsOpen = false;
            SaveWindowAndPaneState();
        }
    }

    private void OnNavigationSizeChanged(
        object sender,
        SizeChangedEventArgs args)
    {
        var maximumWidth = GetMaximumPaneWidth();
        if (RootNavigation.OpenPaneLength > maximumWidth)
        {
            RootNavigation.OpenPaneLength = maximumWidth;
        }
        else if (RootNavigation.OpenPaneLength < _preferredPaneWidth)
        {
            ApplyEffectivePaneWidth();
        }

        UpdatePaneResizeThumb();
    }

    private void OnPaneResizeDragDelta(
        object sender,
        DragDeltaEventArgs args)
    {
        ResizeNavigationPane(
            RootNavigation.OpenPaneLength + args.HorizontalChange);
    }

    private void OnPaneResizeDragCompleted(
        object sender,
        DragCompletedEventArgs args)
    {
        SaveWindowAndPaneState();
    }

    private void OnPaneResizeKeyDown(
        object sender,
        KeyRoutedEventArgs args)
    {
        var requestedWidth = args.Key switch
        {
            VirtualKey.Left =>
                RootNavigation.OpenPaneLength - KeyboardPaneResizeStep,
            VirtualKey.Right =>
                RootNavigation.OpenPaneLength + KeyboardPaneResizeStep,
            VirtualKey.Home => MinimumPaneWidth,
            VirtualKey.End => GetMaximumPaneWidth(),
            _ => double.NaN,
        };

        if (double.IsNaN(requestedWidth))
        {
            return;
        }

        ResizeNavigationPane(requestedWidth);
        SaveWindowAndPaneState();
        args.Handled = true;
    }

    private void OnNavigationPointerPressed(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (!RootNavigation.IsPaneOpen)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(RootNavigation);
        if (!pointerPoint.Properties.IsLeftButtonPressed
            || Math.Abs(
                pointerPoint.Position.X - RootNavigation.OpenPaneLength)
                > PaneResizeHitTarget
            || !RootNavigation.CapturePointer(args.Pointer))
        {
            return;
        }

        _isPaneResizePointerActive = true;
        _paneResizePointerId = args.Pointer.PointerId;
        args.Handled = true;
    }

    private void OnNavigationPointerMoved(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (!_isPaneResizePointerActive
            || args.Pointer.PointerId != _paneResizePointerId)
        {
            return;
        }

        var pointerPoint = args.GetCurrentPoint(RootNavigation);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            CompletePanePointerResize(args);
            return;
        }

        ResizeNavigationPane(pointerPoint.Position.X);
        args.Handled = true;
    }

    private void OnNavigationPointerReleased(
        object sender,
        PointerRoutedEventArgs args)
    {
        CompletePanePointerResize(args);
    }

    private void OnNavigationPointerCanceled(
        object sender,
        PointerRoutedEventArgs args)
    {
        CompletePanePointerResize(args);
    }

    private void OnNavigationPointerCaptureLost(
        object sender,
        PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId == _paneResizePointerId)
        {
            _isPaneResizePointerActive = false;
        }
    }

    private async void OnSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigationSelection)
        {
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag || !_routes.TryGetValue(tag, out var pageType))
        {
            return;
        }

        var requestedNavigationItem = args.SelectedItemContainer;
        if (ContentFrame.CurrentSourcePageType == pageType)
        {
            _committedNavigationItem = requestedNavigationItem;
            return;
        }

        if (_navigationConfirmationPending)
        {
            RestoreCommittedNavigationItem(sender);
            return;
        }

        _navigationConfirmationPending = true;
        try
        {
            if (ContentFrame.Content is DatabasePage databasePage
                && !await databasePage.ConfirmNavigationAwayAsync())
            {
                RestoreCommittedNavigationItem(sender);
                return;
            }

            SetSelectedNavigationItem(sender, requestedNavigationItem);
            if (ContentFrame.Navigate(
                    pageType,
                    null,
                    args.RecommendedNavigationTransitionInfo))
            {
                _committedNavigationItem = requestedNavigationItem;
            }
            else
            {
                RestoreCommittedNavigationItem(sender);
            }
        }
        finally
        {
            _navigationConfirmationPending = false;
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        ViewModel.ActivePageTitle = e.SourcePageType.Name switch
        {
            nameof(ModerationQueuePage) => "Moderationswarteschlange",
            nameof(PoliciesPage) => "Blacklist",
            nameof(BackseatgamingPage) => "Backseatgaming",
            nameof(WhitelistPage) => "Whitelist",
            nameof(ChatPage) => "Chat",
            nameof(DatabasePage) => "Datenbank",
            nameof(AuditPage) => "Ausführungslog",
            nameof(SettingsPage) => "Einstellungen",
            _ => "TwitchAI",
        };
    }

    private async void OnAppWindowClosing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_allowClose
            || ContentFrame.Content is not DatabasePage databasePage
            || !databasePage.ViewModel.HasPendingChanges)
        {
            return;
        }

        args.Cancel = true;
        if (_closeConfirmationPending)
        {
            return;
        }

        _closeConfirmationPending = true;
        try
        {
            if (await databasePage.ConfirmNavigationAwayAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closeConfirmationPending = false;
        }
    }

    private void OnAppWindowChanged(
        AppWindow sender,
        AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange || !_windowStateReady)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State != OverlappedPresenterState.Restored)
        {
            return;
        }

        _lastRestoredWindowSize = sender.Size;
        ScheduleLayoutSave();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _layoutSaveTimer.Stop();
        SaveWindowAndPaneState();
        _layoutSaveTimer.Tick -= OnLayoutSaveTimerTick;
        Activated -= OnWindowActivated;
        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow.Closing -= OnAppWindowClosing;
        }
    }

    private void TryApplyInitialWindowSize()
    {
        if (_initialWindowSizeApplied
            || !_hasActivated
            || !_navigationLoaded
            || _appWindow is null)
        {
            return;
        }

        _initialWindowSizeApplied = true;
        _lastRestoredWindowSize = GetInitialWindowSize(
            _windowId,
            GetRasterizationScale());
        _appWindow.Resize(_lastRestoredWindowSize);

        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (_appWindow is null)
                {
                    return;
                }

                if (_appWindow.Presenter is not OverlappedPresenter presenter
                    || presenter.State == OverlappedPresenterState.Restored)
                {
                    _lastRestoredWindowSize = _appWindow.Size;
                }

                _windowStateReady = true;
                ApplyEffectivePaneWidth();
                ScheduleLayoutSave();
            });
    }

    private SizeInt32 GetInitialWindowSize(
        WindowId windowId,
        double rasterizationScale)
    {
        const double minimumWidthDips = 720;
        const double minimumHeightDips = 540;

        var requestedWidth = ReadStoredWindowDimension(
            WindowWidthDipsSetting,
            WindowWidthSetting,
            DefaultWindowWidthDips,
            rasterizationScale);
        var requestedHeight = ReadStoredWindowDimension(
            WindowHeightDipsSetting,
            WindowHeightSetting,
            DefaultWindowHeightDips,
            rasterizationScale);
        var displayArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return new SizeInt32(
                Math.Max(
                    DipsToPixels(minimumWidthDips, rasterizationScale),
                    requestedWidth),
                Math.Max(
                    DipsToPixels(minimumHeightDips, rasterizationScale),
                    requestedHeight));
        }

        var maximumWidth = Math.Max(1, displayArea.WorkArea.Width);
        var maximumHeight = Math.Max(1, displayArea.WorkArea.Height);
        var effectiveMinimumWidth = Math.Min(
            DipsToPixels(minimumWidthDips, rasterizationScale),
            maximumWidth);
        var effectiveMinimumHeight = Math.Min(
            DipsToPixels(minimumHeightDips, rasterizationScale),
            maximumHeight);
        return new SizeInt32(
            Math.Clamp(requestedWidth, effectiveMinimumWidth, maximumWidth),
            Math.Clamp(requestedHeight, effectiveMinimumHeight, maximumHeight));
    }

    private int ReadStoredWindowDimension(
        string dipsKey,
        string legacyPixelsKey,
        double fallbackDips,
        double rasterizationScale)
    {
        if (double.TryParse(
                _localStorage.GetString(dipsKey),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var storedDips)
            && double.IsFinite(storedDips)
            && storedDips > 0)
        {
            return DipsToPixels(storedDips, rasterizationScale);
        }

        if (int.TryParse(
                _localStorage.GetString(legacyPixelsKey),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var legacyPixels)
            && legacyPixels > 0)
        {
            return legacyPixels;
        }

        return DipsToPixels(fallbackDips, rasterizationScale);
    }

    private bool ReadStoredPaneIsOpen()
    {
        return bool.TryParse(
                _localStorage.GetString(NavigationPaneIsOpenSetting),
                out var isOpen)
            && isOpen;
    }

    private double ReadStoredPaneWidth()
    {
        return double.TryParse(
                _localStorage.GetString(NavigationPaneWidthSetting),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var width)
            && double.IsFinite(width)
                ? Math.Clamp(width, MinimumPaneWidth, MaximumPaneWidth)
                : DefaultPaneWidth;
    }

    private void SaveWindowAndPaneState()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WindowLayoutVersionSetting] = "2",
            [NavigationPaneIsOpenSetting] =
                _preferredPaneIsOpen.ToString(),
            [NavigationPaneWidthSetting] =
                _preferredPaneWidth.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture),
        };

        if (_lastRestoredWindowSize.Width > 0
            && _lastRestoredWindowSize.Height > 0)
        {
            var rasterizationScale = GetRasterizationScale();
            settings[WindowWidthSetting] =
                _lastRestoredWindowSize.Width.ToString(
                    CultureInfo.InvariantCulture);
            settings[WindowHeightSetting] =
                _lastRestoredWindowSize.Height.ToString(
                    CultureInfo.InvariantCulture);
            settings[WindowWidthDipsSetting] = PixelsToDips(
                    _lastRestoredWindowSize.Width,
                    rasterizationScale)
                .ToString("0.###", CultureInfo.InvariantCulture);
            settings[WindowHeightDipsSetting] = PixelsToDips(
                    _lastRestoredWindowSize.Height,
                    rasterizationScale)
                .ToString("0.###", CultureInfo.InvariantCulture);
        }

        _localStorage.SetStrings(settings);
    }

    private void ResizeNavigationPane(double requestedWidth)
    {
        if (!double.IsFinite(requestedWidth))
        {
            return;
        }

        _preferredPaneWidth = Math.Clamp(
            requestedWidth,
            MinimumPaneWidth,
            MaximumPaneWidth);
        ApplyEffectivePaneWidth();
        ScheduleLayoutSave();
    }

    private void ApplyEffectivePaneWidth()
    {
        RootNavigation.OpenPaneLength = Math.Clamp(
            _preferredPaneWidth,
            MinimumPaneWidth,
            GetMaximumPaneWidth());
        UpdatePaneResizeThumb();
    }

    private void CompletePanePointerResize(PointerRoutedEventArgs args)
    {
        if (!_isPaneResizePointerActive
            || args.Pointer.PointerId != _paneResizePointerId)
        {
            return;
        }

        _isPaneResizePointerActive = false;
        RootNavigation.ReleasePointerCapture(args.Pointer);
        SaveWindowAndPaneState();
        args.Handled = true;
    }

    private double GetMaximumPaneWidth()
    {
        if (RootNavigation.ActualWidth <= 0)
        {
            return MaximumPaneWidth;
        }

        return Math.Max(
            MinimumPaneWidth,
            Math.Min(
                MaximumPaneWidth,
                RootNavigation.ActualWidth - MinimumContentWidth));
    }

    private void UpdatePaneResizeThumb()
    {
        PaneHeaderLayout.Width = Math.Max(
            0,
            RootNavigation.OpenPaneLength
            - RootNavigation.CompactPaneLength
            - PaneHeaderLayout.Margin.Left);

        if (!RootNavigation.IsPaneOpen)
        {
            PaneResizeThumb.Visibility = Visibility.Collapsed;
            return;
        }

        PaneResizeThumb.Visibility = Visibility.Visible;
    }

    private void ScheduleLayoutSave()
    {
        if (!_navigationStateReady)
        {
            return;
        }

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private void OnLayoutSaveTimerTick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        SaveWindowAndPaneState();
    }

    private double GetRasterizationScale()
    {
        var rasterizationScale = RootNavigation.XamlRoot?.RasterizationScale ?? 1;
        return double.IsFinite(rasterizationScale) && rasterizationScale > 0
            ? rasterizationScale
            : 1;
    }

    private static int DipsToPixels(
        double dips,
        double rasterizationScale)
    {
        var pixels = Math.Round(
            dips * rasterizationScale,
            MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(pixels, 1, int.MaxValue);
    }

    private static double PixelsToDips(
        int pixels,
        double rasterizationScale)
    {
        return pixels / rasterizationScale;
    }

    private void RestoreCommittedNavigationItem(NavigationView navigationView)
    {
        if (_committedNavigationItem is not null)
        {
            SetSelectedNavigationItem(navigationView, _committedNavigationItem);
        }
    }

    private void SetSelectedNavigationItem(
        NavigationView navigationView,
        object navigationItem)
    {
        _suppressNavigationSelection = true;
        try
        {
            navigationView.SelectedItem = navigationItem;
        }
        finally
        {
            _suppressNavigationSelection = false;
        }
    }
}
