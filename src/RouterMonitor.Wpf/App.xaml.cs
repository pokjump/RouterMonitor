using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Providers.AdbVV5822;
using RouterMonitor.Wpf.Data;
using RouterMonitor.Wpf.Services;
using RouterMonitor.Wpf.ViewModels;
using RouterMonitor.Wpf.Views;

namespace RouterMonitor.Wpf;

public partial class App : System.Windows.Application
{
    private ILoggerFactory? _loggerFactory;
    private IRouterProvider? _provider;
    private PollingService? _polling;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        StartupRegistration.EnsureRegistered();

        var settings = AppSettings.LoadOrCreateDefault();
        var logDirectory = Path.Combine(Path.GetDirectoryName(AppSettings.DatabasePath)!, "logs");

        _loggerFactory = LoggerFactory.Create(builder => builder
            .AddDebug()
            .AddProvider(new FileLoggerProvider(logDirectory))
            .SetMinimumLevel(LogLevel.Information));

        var options = new AdbVV5822Options
        {
            BaseAddress = new Uri(settings.BaseAddress),
            Username = settings.Username,
            Password = settings.Password,
            RawDumpDirectory = settings.RawDumpDirectory,
        };
        _provider = new AdbVV5822Provider(options, _loggerFactory.CreateLogger<AdbVV5822Provider>());

        var db = new HistoryDatabase(AppSettings.DatabasePath, _loggerFactory.CreateLogger<HistoryDatabase>());
        await db.InitializeAsync();

        _polling = new PollingService(
            _provider, db, _loggerFactory.CreateLogger<PollingService>(),
            TimeSpan.FromSeconds(Math.Max(5, settings.PollIntervalSeconds)));

        _mainWindow = new MainWindow();
        _tray = new TrayIconService(_mainWindow);
        _tray.ExitRequested += OnTrayExitRequested;

        var viewModel = new MainViewModel(_provider, _polling, db, _tray, _loggerFactory.CreateLogger<MainViewModel>());
        _mainWindow.DataContext = viewModel;

        _polling.PollFailed += ex => _loggerFactory.CreateLogger<App>().LogError(ex, "Odpytywanie nieudane.");

        // Start hidden in the tray - the window opens only when the user asks for it (tray menu/double-click).
        _polling.Start();
    }

    private void OnTrayExitRequested()
    {
        if (_mainWindow is not null)
            _mainWindow.AllowClose = true;
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _loggerFactory?.CreateLogger<App>().LogError(e.Exception, "Nieobsłużony wyjątek w wątku UI.");
        _tray?.ShowError($"Wystąpił nieoczekiwany błąd: {e.Exception.Message}");
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_polling is not null)
            await _polling.DisposeAsync();

        if (_provider is not null)
        {
            try
            {
                await _provider.LogoutAsync();
            }
            catch
            {
                // Best-effort logout on shutdown; nothing useful to do if it fails.
            }

            (_provider as IDisposable)?.Dispose();
        }

        _tray?.Dispose();
        _loggerFactory?.Dispose();

        base.OnExit(e);
    }
}
