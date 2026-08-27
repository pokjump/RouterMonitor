using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Models;
using RouterMonitor.Wpf.Data;
using RouterMonitor.Wpf.Services;

namespace RouterMonitor.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxChartPoints = 60;

    private readonly IRouterProvider _provider;
    private readonly PollingService _polling;
    private readonly HistoryDatabase _db;
    private readonly TrayIconService _tray;
    private readonly ILogger<MainViewModel> _logger;

    [ObservableProperty] private string model = "–";
    [ObservableProperty] private string firmware = "–";
    [ObservableProperty] private string uptime = "–";
    [ObservableProperty] private string wanIp = "–";
    [ObservableProperty] private string gateway = "–";
    [ObservableProperty] private string dns = "–";
    [ObservableProperty] private string downstreamText = "–";
    [ObservableProperty] private string upstreamText = "–";
    [ObservableProperty] private string statusMessage = "Łączenie z routerem...";
    [ObservableProperty] private string lastUpdatedText = "–";
    [ObservableProperty] private bool isOnline;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryNowCommand))]
    private bool isRetrying;
    [ObservableProperty] private bool isConnecting = true;
    [ObservableProperty] private int connectionProgress;
    [ObservableProperty] private string connectionStageText = "Łączenie z routerem...";
    [ObservableProperty] private DateTime historyDate = DateTime.Today.AddDays(-1);

    public ObservableCollection<NetworkDevice> Devices { get; } = [];
    public ObservableCollection<WifiBand> WifiBands { get; } = [];
    public ObservableCollection<double> DownstreamHistory { get; } = [];
    public ObservableCollection<double> UpstreamHistory { get; } = [];
    public ObservableCollection<DeviceSightingSummary> HistoryDevices { get; } = [];
    public ObservableCollection<ManagedDeviceRow> InactiveDevices { get; } = [];

    [ObservableProperty] private string manageStatusMessage = "";

    public MainViewModel(IRouterProvider provider, PollingService polling, HistoryDatabase db, TrayIconService tray, ILogger<MainViewModel> logger)
    {
        _provider = provider;
        _polling = polling;
        _db = db;
        _tray = tray;
        _logger = logger;

        _polling.DataUpdated += OnDataUpdated;
        _polling.PollFailed += OnPollFailed;
        _polling.DevicesConnected += OnDevicesConnected;
        _polling.DevicesDisconnected += OnDevicesDisconnected;
        _polling.ConnectionProgressChanged += OnConnectionProgressChanged;

        _ = LoadHistoryAsync();
        _ = LoadInactiveDevicesAsync();
    }

    private void OnDataUpdated(RouterOverview overview, IReadOnlyList<NetworkDevice> devices)
    {
        System.Windows.Application.Current!.Dispatcher.Invoke(() =>
        {
            Model = overview.Model ?? "–";
            Firmware = overview.Firmware ?? "–";
            Uptime = overview.Uptime ?? "–";
            WanIp = overview.WanIp ?? "–";
            Gateway = overview.Gateway ?? "–";
            Dns = overview.Dns ?? "–";
            DownstreamText = overview.Find("transfer pobiera", "downstream", "download") ?? "–";
            UpstreamText = overview.Find("transfer wysył", "upstream", "upload") ?? "–";
            StatusMessage = "Połączono";
            IsOnline = true;
            LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");

            Devices.Clear();
            foreach (var device in devices.OrderBy(d => d.Name))
                Devices.Add(device);

            WifiBands.Clear();
            foreach (var section in overview.Sections.Where(s => s.Title.Contains("wifi", StringComparison.OrdinalIgnoreCase)))
                WifiBands.Add(WifiBand.FromSection(section));

            PushChartPoint(
                NumericParsing.ExtractLeadingNumber(DownstreamText),
                NumericParsing.ExtractLeadingNumber(UpstreamText));
        });
    }

    private void PushChartPoint(double? downstream, double? upstream)
    {
        DownstreamHistory.Add(downstream ?? 0);
        UpstreamHistory.Add(upstream ?? 0);

        while (DownstreamHistory.Count > MaxChartPoints)
            DownstreamHistory.RemoveAt(0);
        while (UpstreamHistory.Count > MaxChartPoints)
            UpstreamHistory.RemoveAt(0);
    }

    private void OnPollFailed(Exception ex)
    {
        System.Windows.Application.Current!.Dispatcher.Invoke(() =>
        {
            IsOnline = false;
            StatusMessage = $"Błąd połączenia: {ex.Message}";
            IsConnecting = false;
            ConnectionProgress = 0;
        });
    }

    /// <summary>
    /// Reflects the real steps of a poll (login/overview/devices/persist), not a fake animation.
    /// Only surfaced while there's an actual connection attempt in flight - the initial connect,
    /// or an explicit user-triggered retry - so it doesn't flash on every steady-state background poll.
    /// </summary>
    private void OnConnectionProgressChanged(int percent, string message)
    {
        System.Windows.Application.Current!.Dispatcher.Invoke(() =>
        {
            if (IsOnline && !IsRetrying)
                return;

            ConnectionProgress = percent;
            ConnectionStageText = message;
            IsConnecting = percent < 100;
        });
    }

    private void OnDevicesConnected(IReadOnlyList<DeviceStatusChange> changes)
    {
        System.Windows.Application.Current!.Dispatcher.Invoke(() =>
        {
            foreach (var (device, isNew) in changes)
            {
                var message = $"{device.Name} ({device.MacAddress}) dołączył do sieci - IP {device.IpAddress ?? "?"}.";
                _tray.ShowDeviceAlert(isNew ? "Nowe urządzenie w sieci" : "Urządzenie połączone", message);
                _logger.LogWarning(
                    "Urządzenie połączone{New}: {Name} MAC={Mac} IP={Ip} Połączenie={Connection}",
                    isNew ? " (nowe)" : "", device.Name, device.MacAddress, device.IpAddress, device.ConnectionType);
            }
        });
    }

    private void OnDevicesDisconnected(IReadOnlyList<NetworkDevice> devices)
    {
        System.Windows.Application.Current!.Dispatcher.Invoke(() =>
        {
            foreach (var device in devices)
            {
                var message = $"{device.Name} ({device.MacAddress}) rozłączył się z siecią.";
                _tray.ShowDeviceAlert("Urządzenie rozłączone", message);
                _logger.LogWarning(
                    "Urządzenie rozłączone: {Name} MAC={Mac} Połączenie={Connection}",
                    device.Name, device.MacAddress, device.ConnectionType);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanRetryNow))]
    private async Task RetryNowAsync()
    {
        IsRetrying = true;
        IsConnecting = true;
        ConnectionProgress = 0;
        StatusMessage = "Ponawianie połączenia...";
        try
        {
            await _polling.PollNowAsync();
        }
        finally
        {
            IsRetrying = false;
        }
    }

    private bool CanRetryNow() => !IsRetrying;

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        var from = new DateTimeOffset(HistoryDate.Date, DateTimeOffset.Now.Offset);
        var to = from.AddDays(1);
        var results = await _db.GetDevicesSeenBetweenAsync(from, to);

        HistoryDevices.Clear();
        foreach (var result in results)
            HistoryDevices.Add(result);
    }

    [RelayCommand]
    private async Task RebootRouterAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "Na pewno zrestartować router? Internet i WiFi w domu będą chwilowo niedostępne.",
            "Uruchom ponownie router",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

        if (!confirmed)
            return;

        try
        {
            StatusMessage = "Wysyłanie żądania ponownego uruchomienia...";
            await _provider.RebootAsync();
            StatusMessage = "Router uruchamia się ponownie - połączenie wróci za chwilę.";
            _logger.LogWarning("Router został zrestartowany na żądanie użytkownika.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się zrestartować routera.");
            System.Windows.MessageBox.Show($"Nie udało się zrestartować routera: {ex.Message}", "Router Monitor", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task LoadInactiveDevicesAsync()
    {
        try
        {
            var all = await _provider.GetAllKnownDevicesAsync();
            var inactive = all.Where(d => d.IsConnected == false).ToList();

            InactiveDevices.Clear();
            foreach (var device in inactive.OrderBy(d => d.Name))
                InactiveDevices.Add(new ManagedDeviceRow(device, isSelected: true));

            ManageStatusMessage = inactive.Count == 0
                ? "Brak nieaktywnych urządzeń."
                : $"Znaleziono {inactive.Count} nieaktywnych urządzeń.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się wczytać listy nieaktywnych urządzeń.");
            ManageStatusMessage = $"Błąd wczytywania: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectAllInactive()
    {
        foreach (var row in InactiveDevices)
            row.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllInactive()
    {
        foreach (var row in InactiveDevices)
            row.IsSelected = false;
    }

    [RelayCommand]
    private async Task DeleteSelectedInactiveAsync()
    {
        var selected = InactiveDevices.Where(r => r.IsSelected && r.Device.HostId is not null).ToList();
        if (selected.Count == 0)
        {
            ManageStatusMessage = "Nie zaznaczono żadnego urządzenia do usunięcia.";
            return;
        }

        var confirmed = System.Windows.MessageBox.Show(
            $"Usunąć {selected.Count} zaznaczonych nieaktywnych urządzeń z listy znanych urządzeń routera?",
            "Usuń urządzenia",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

        if (!confirmed)
            return;

        var failures = 0;
        foreach (var row in selected)
        {
            try
            {
                await _provider.DeleteDeviceAsync(row.Device.HostId!.Value);
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogError(ex, "Nie udało się usunąć urządzenia {Name} (hostid={HostId}).", row.Name, row.Device.HostId);
            }
        }

        ManageStatusMessage = failures == 0
            ? $"Usunięto {selected.Count} urządzeń."
            : $"Usunięto {selected.Count - failures} z {selected.Count} - {failures} nie powiodło się (patrz log).";

        await LoadInactiveDevicesAsync();
    }

    public void Dispose()
    {
        _polling.DataUpdated -= OnDataUpdated;
        _polling.PollFailed -= OnPollFailed;
        _polling.DevicesConnected -= OnDevicesConnected;
        _polling.DevicesDisconnected -= OnDevicesDisconnected;
        _polling.ConnectionProgressChanged -= OnConnectionProgressChanged;
    }
}
