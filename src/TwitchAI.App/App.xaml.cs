using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.App;

public partial class App : Application
{
    private const int AbortHResult = unchecked((int)0x80004004);
    private const int FailHResult = unchecked((int)0x80004005);
    private readonly object _diagnosticGate = new();
    private readonly IHost _host;
    private readonly JsonLocalAppStorage _localStorage;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        _localStorage = new JsonLocalAppStorage();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DebugSettings.XamlResourceReferenceFailed += OnXamlResourceReferenceFailed;

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILocalAppStorage>(_localStorage);
                services.AddSingleton(
                    new TwitchRuntimeModeSettings(
                        _localStorage.GetBooleanOrDefault(
                            LocalAppSettingKeys.ReadOnlyMode,
                            defaultValue: false)));
                services.AddSingleton<ITwitchRuntimeModeSettings>(
                    provider => provider.GetRequiredService<
                        TwitchRuntimeModeSettings>());
                services.AddSingleton<
                    ITableSortPreferenceStore,
                    TableSortPreferenceStore>();
                services.AddSingleton<
                    ITableColumnLayoutStore,
                    TableColumnLayoutStore>();
                services.AddSingleton<
                    IConfigurationPageSectionPreferenceStore,
                    ConfigurationPageSectionPreferenceStore>();
                services.AddTwitchAIInfrastructure(options =>
                {
                    options.DatabasePath = Path.Combine(
                        _localStorage.LocalFolderPath,
                        "TwitchAI.db");
                    options.TwitchOAuth.ClientId =
                        _localStorage.GetString("TwitchClientId") ?? string.Empty;
                    var storedBroadcasterIds =
                        TwitchChannelSelection.ParseBroadcasterIds(
                            _localStorage.GetString("BroadcasterIds"));
                    if (storedBroadcasterIds.Count == 0)
                    {
                        storedBroadcasterIds =
                            TwitchChannelSelection.ParseBroadcasterIds(
                                _localStorage.GetString("BroadcasterId"));
                    }

                    options.TwitchApi.SetBroadcasterIds(storedBroadcasterIds);
                    options.TwitchApi.ReaderUserId =
                        _localStorage.GetString("ReaderUserId") ?? string.Empty;
                    options.TwitchApi.ModeratorUserId =
                        _localStorage.GetString("ModeratorUserId") ?? string.Empty;
                    if (Uri.TryCreate(
                            _localStorage.GetString("LmStudioBaseUrl"),
                            UriKind.Absolute,
                            out var baseAddress)
                        && baseAddress.Scheme is "http" or "https")
                    {
                        options.LmStudio.BaseAddress = baseAddress;
                    }

                    options.LmStudio.Model =
                        _localStorage.GetString("LmStudioModel")
                        ?? "openai/gpt-oss-20b";
                    options.LmStudio.UseCompactModerationContractV6 =
                        _localStorage.GetBooleanOrDefault(
                            LocalAppSettingKeys.UseCompactModerationContractV6,
                            defaultValue: true);
                });
                services.AddSingleton<
                    TwitchAI.Core.Contracts.IChatTextNormalizer,
                    TwitchAI.Core.Services.ChatTextNormalizer>();
                services.AddSingleton<IEffectivePolicyResolver, EffectivePolicyResolver>();
                services.AddSingleton<IBlacklistMatcher, BlacklistMatcher>();
                services.AddSingleton<IMessageAvailabilityRegistry, MessageAvailabilityRegistry>();
                services.AddSingleton<IWhitelistPhraseSource, SqliteWhitelistPhraseSource>();
                services.AddSingleton<IWhitelistFilterAgent, WhitelistFilterAgent>();
                services.AddSingleton<IPhraseRuleSource, SqlitePhraseRuleSource>();
                services.AddSingleton(
                    new ModerationQueueRuntimeSettings(
                        _localStorage.GetBooleanOrDefault(
                            LocalAppSettingKeys.RemoveIdenticalAiQueueMessages,
                            defaultValue: true)));
                services.AddSingleton<IModerationQueueRuntimeSettings>(
                    provider => provider.GetRequiredService<
                        ModerationQueueRuntimeSettings>());
                services.AddSingleton<IModerationBatcher, ModerationBatcher>();
                services.AddSingleton<IModerationEnvelopeValidator, ModerationEnvelopeValidator>();
                services.AddSingleton<HelixChannelContextService>();
                services.AddSingleton<IChannelContextService>(
                    provider => provider.GetRequiredService<HelixChannelContextService>());
                services.AddSingleton<ITwitchEventSubClient, CoreTwitchEventSubClient>();
                services.AddSingleton<ILocalModerationClient, CoreLocalModerationClient>();
                services.AddSingleton<IModerationRepairClient, ModerationRepairClient>();
                services.AddSingleton<IRetrievalExampleStore, SqliteRetrievalExampleStore>();
                services.AddSingleton<IModerationCaseRepository, CoreModerationCaseRepository>();
                services.AddSingleton<IModerationCoordinator, ModerationCoordinator>();
                services.AddSingleton<ILearningTestService, LearningTestService>();
                services.AddSingleton<ModerationUiBridge>();
                services.AddSingleton(
                    provider => new AppState
                    {
                        IsReadOnlyMode = provider
                            .GetRequiredService<TwitchRuntimeModeSettings>()
                            .IsReadOnly,
                    });
                services.AddSingleton<ApplicationStateLoader>();
                services.AddSingleton<
                    IConfigurationDataService,
                    ConfigurationDataService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<ModerationQueueViewModel>();
                services.AddTransient<PoliciesViewModel>();
                services.AddSingleton<WhitelistViewModel>();
                services.AddSingleton<ChatViewModel>();
                services.AddSingleton<LearningViewModel>();
                services.AddTransient<LearningDatasetViewModel>();
                services.AddSingleton<DatabaseViewModel>();
                services.AddSingleton<AuditViewModel>();
                services.AddSingleton<SettingsViewModel>();
            })
            .Build();
    }

    public static new App Current => (App)Application.Current;

    public Window? MainWindow => _window;

    public T GetService<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public void ApplyTheme(string selectedTheme)
    {
        if (_window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = selectedTheme switch
        {
            "Hell" => ElementTheme.Light,
            "Dunkel" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            try
            {
                await _host.StartAsync();
                await GetService<ApplicationStateLoader>().LoadAsync();
                GetService<AppState>().DatabaseStatus = "SQLite bereit";
            }
            catch (Exception exception)
            {
                GetService<AppState>().DatabaseStatus = $"SQLite-Fehler: {exception.Message}";
            }

            _window = new MainWindow(
                GetService<ShellViewModel>(),
                GetService<ILocalAppStorage>());
            ApplyTheme(_localStorage.GetString("Theme") ?? "System");
            GetService<ModerationUiBridge>().Initialize(_window.DispatcherQueue);
            _window.Closed += OnWindowClosed;
            _window.Activate();

            try
            {
                await GetService<SettingsViewModel>()
                    .RestoreTwitchSessionAsync();
            }
            catch (Exception exception)
            {
                WriteStartupDiagnostic(
                    $"Twitch session restoration failed:{Environment.NewLine}{exception}");
            }
        }
        catch (Exception exception)
        {
            var errorPath = Path.Combine(
                _localStorage.LocalFolderPath,
                "startup-error.log");
            File.WriteAllText(errorPath, exception.ToString());
            throw;
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Detach UI projections before stopping the host. Coordinator
        // cancellation can still publish a final failed/cancelled activity,
        // but the WinUI dispatcher is already being torn down at this point.
        GetService<ModerationUiBridge>().Dispose();
        try
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception)
        {
            WriteRuntimeDiagnostic(
                $"Application shutdown failed:{Environment.NewLine}{exception}");
        }
        finally
        {
            try
            {
                _host.Dispose();
            }
            catch (Exception exception)
            {
                WriteRuntimeDiagnostic(
                    $"Application disposal failed:{Environment.NewLine}{exception}");
            }
        }
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteRuntimeDiagnostic(
            $"Unhandled XAML exception:{Environment.NewLine}{args.Message}" +
            $"{Environment.NewLine}{args.Exception}");

        // WinUI.TableView 1.4.1 starts an asynchronous drag visual while a
        // column header is pressed. Windows can abort that COM operation when
        // the pointer/capture changes, which otherwise terminates the entire
        // WinUI dispatcher. Column reordering is disabled in every table, but
        // keep this narrow guard for an already queued drag callback.
        if (args.Exception is COMException
            && args.Exception.HResult is AbortHResult or FailHResult
            && args.Exception.StackTrace?.Contains(
                "WinUI.TableView.TableViewColumnHeader.CreateDragVisualsAsync",
                StringComparison.Ordinal) == true)
        {
            args.Handled = true;
        }
    }

    private void OnAppDomainUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args)
    {
        WriteRuntimeDiagnostic(
            $"Unhandled AppDomain exception · terminating={args.IsTerminating}:" +
            $"{Environment.NewLine}{args.ExceptionObject}");
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args)
    {
        WriteRuntimeDiagnostic(
            $"Unobserved task exception:{Environment.NewLine}{args.Exception}");
        args.SetObserved();
    }

    private void OnXamlResourceReferenceFailed(
        DebugSettings sender,
        XamlResourceReferenceFailedEventArgs args)
    {
        WriteStartupDiagnostic(
            $"XAML resource lookup failed:{Environment.NewLine}{args.Message}");
    }

    private void WriteStartupDiagnostic(string message)
    {
        WriteDiagnosticFile("startup-error.log", message);
    }

    private void WriteRuntimeDiagnostic(string message)
    {
        WriteDiagnosticFile("runtime-error.log", message);
    }

    private void WriteDiagnosticFile(string fileName, string message)
    {
        try
        {
            lock (_diagnosticGate)
            {
                var errorPath = Path.Combine(
                    _localStorage.LocalFolderPath,
                    fileName);
                File.AppendAllText(
                    errorPath,
                    $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{message}" +
                    $"{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never trigger a second process-terminating error.
        }
    }
}
