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
    private WebServer? _webServer;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
#if !DEBUG
        // Debug builds run from bin/Debug during development and get rebuilt/cleaned constantly -
        // registering that ephemeral path for Windows sign-in would silently hijack the autostart
        // entry away from the real installed build and break it the next time bin/Debug changes.
        StartupRegistration.EnsureRegistered();
#endif

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

        var viewModel = new MainViewModel(_provider, _polling, _tray, _loggerFactory.CreateLogger<MainViewModel>());
        _mainWindow.DataContext = viewModel;

        _polling.PollFailed += ex => _loggerFactory.CreateLogger<App>().LogError(ex, "Odpytywanie nieudane.");

        // Start hidden in the tray - the window opens only when the user asks for it (tray menu/double-click).
        _polling.Start();

        await StartWebServerAsync(settings, viewModel);
    }

    private async Task StartWebServerAsync(AppSettings settings, MainViewModel viewModel)
    {
        if (!settings.WebServerEnabled)
        {
            viewModel.SetWebAccess(null, null, "Wyłączony w settings.json (WebServerEnabled).");
            return;
        }

        var pin = settings.WebPin!;
        _webServer = new WebServer(_provider!, _polling!, settings.WebServerPort, pin, _loggerFactory!.CreateLogger<WebServer>());
        try
        {
            await _webServer.StartAsync();
            var urls = _webServer.Urls;
            viewModel.SetWebAccess(
                urls.Count > 0 ? string.Join("   ", urls) : $"http://<adres tego komputera>:{settings.WebServerPort}",
                pin,
                "Działa - otwórz adres w przeglądarce telefonu podłączonego do tej samej sieci.");
        }
        catch (Exception ex)
        {
            _loggerFactory!.CreateLogger<App>().LogError(ex, "Nie udało się uruchomić serwera WWW na porcie {Port}.", settings.WebServerPort);
            await _webServer.DisposeAsync();
            _webServer = null;
            viewModel.SetWebAccess(null, null,
                $"Nie udało się uruchomić na porcie {settings.WebServerPort} (port zajęty?). Zmień WebServerPort w settings.json. Błąd: {ex.Message}");
        }
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
        if (_webServer is not null)
            await _webServer.DisposeAsync();

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
