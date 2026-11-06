using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Wpf.Services;

/// <summary>One row of the "Limity" view: an online device and/or a device that has a limit.</summary>
public sealed record RateLimitEntry(string Mac, string Name, string? IpAddress, bool IsOnline, int? DownloadKbps, int? UploadKbps);

/// <summary>
/// Shared by the desktop window and the phone web view. Caches the router's speed limits (reading
/// them takes several slow page loads) and keeps them pointed at the right device: the router's
/// rules match by IP, so when a limited device shows up on a different IP, the rules are moved.
/// </summary>
public sealed class RateLimitService
{
    private readonly IRouterProvider _provider;
    private readonly ILogger<RateLimitService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();

    private IReadOnlyList<DeviceRateLimit>? _limits;
    private IReadOnlyList<NetworkDevice> _onlineDevices = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private int _ipSyncRunning;

    /// <summary>Raised (on a background thread) whenever the cached limits change.</summary>
    public event Action? Changed;

    public RateLimitService(IRouterProvider provider, PollingService polling, ILogger<RateLimitService> logger)
    {
        _provider = provider;
        _logger = logger;
        polling.DataUpdated += (_, devices) => OnDevicesUpdated(devices);
    }

    public bool IsLoaded => _limits is not null;

    public IReadOnlyList<RateLimitEntry> GetEntries()
    {
        lock (_stateLock)
        {
            var limits = (_limits ?? []).ToDictionary(l => l.Mac, StringComparer.OrdinalIgnoreCase);
            var rows = new List<RateLimitEntry>();

            foreach (var device in _onlineDevices.Where(d => !string.IsNullOrWhiteSpace(d.MacAddress)))
            {
                limits.Remove(device.MacAddress!, out var limit);
                rows.Add(new RateLimitEntry(device.MacAddress!, device.Name, CleanIp(device.IpAddress), true, limit?.DownloadKbps, limit?.UploadKbps));
            }

            foreach (var limit in limits.Values)
                rows.Add(new RateLimitEntry(limit.Mac, _names.GetValueOrDefault(limit.Mac) ?? limit.Mac, limit.IpAddress, false, limit.DownloadKbps, limit.UploadKbps));

            return rows
                .OrderByDescending(r => r.DownloadKbps is not null || r.UploadKbps is not null)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ReloadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sets (or with both null, removes) the limit for an online device or one that already has a limit.</summary>
    public async Task SetAsync(string mac, int? downloadKbps, int? uploadKbps, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (downloadKbps is null && uploadKbps is null)
            {
                await _provider.RemoveRateLimitAsync(mac, cancellationToken);
            }
            else
            {
                var ip = CurrentIp(mac)
                    ?? throw new InvalidOperationException("Urządzenie musi być teraz w sieci, żeby ustawić mu limit (potrzebny jest jego adres IP).");
                await _provider.SetRateLimitAsync(mac, ip, downloadKbps, uploadKbps, cancellationToken);
            }
        }
        finally
        {
            // Re-read even after a failure: a half-applied change must show up as it really is.
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udało się odświeżyć listy limitów po zmianie.");
            }
            _gate.Release();
        }
    }

    public Task RemoveAsync(string mac, CancellationToken cancellationToken = default) => SetAsync(mac, null, null, cancellationToken);

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var limits = await _provider.GetRateLimitsAsync(cancellationToken);
        lock (_stateLock)
            _limits = limits;
        Changed?.Invoke();
    }

    private string? CurrentIp(string mac)
    {
        lock (_stateLock)
        {
            var device = _onlineDevices.FirstOrDefault(d => string.Equals(d.MacAddress, mac, StringComparison.OrdinalIgnoreCase));
            if (device is not null)
                return CleanIp(device.IpAddress);
            return _limits?.FirstOrDefault(l => string.Equals(l.Mac, mac, StringComparison.OrdinalIgnoreCase))?.IpAddress;
        }
    }

    private void OnDevicesUpdated(IReadOnlyList<NetworkDevice> devices)
    {
        lock (_stateLock)
        {
            _onlineDevices = devices;
            foreach (var d in devices.Where(d => d.MacAddress is not null))
                _names[d.MacAddress!] = d.Name;
        }

        Changed?.Invoke();
        _ = SyncMovedDevicesAsync();
    }

    /// <summary>Moves limits whose device came back on a different IP; also loads the limits on first poll.</summary>
    private async Task SyncMovedDevicesAsync()
    {
        if (Interlocked.Exchange(ref _ipSyncRunning, 1) == 1)
            return;

        try
        {
            if (_limits is null)
                await RefreshAsync();

            List<(DeviceRateLimit Limit, string NewIp)> moved;
            lock (_stateLock)
            {
                moved = (_limits ?? [])
                    .Select(l => (Limit: l, NewIp: CurrentIpUnlocked(l.Mac)))
                    .Where(x => x.NewIp is not null && x.NewIp != x.Limit.IpAddress)
                    .Select(x => (x.Limit, x.NewIp!))
                    .ToList();
            }

            foreach (var (limit, newIp) in moved)
            {
                _logger.LogWarning("Urządzenie {Mac} zmieniło adres IP z {Old} na {New} - przenoszenie limitu.", limit.Mac, limit.IpAddress, newIp);
                await SetAsync(limit.Mac, limit.DownloadKbps, limit.UploadKbps);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się zsynchronizować limitów prędkości z routerem.");
        }
        finally
        {
            Interlocked.Exchange(ref _ipSyncRunning, 0);
        }
    }

    private string? CurrentIpUnlocked(string mac)
    {
        var device = _onlineDevices.FirstOrDefault(d => string.Equals(d.MacAddress, mac, StringComparison.OrdinalIgnoreCase));
        return device is null ? null : CleanIp(device.IpAddress);
    }

    /// <summary>"192.168.1.38 (DHCP)" → "192.168.1.38".</summary>
    private static string? CleanIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : ip.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
