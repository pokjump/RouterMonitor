using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Models;
using RouterMonitor.Wpf.Data;

namespace RouterMonitor.Wpf.Services;

/// <summary>
/// Polls the router on a fixed interval, in the background, without blocking the UI thread.
/// Persists each poll to <see cref="HistoryDatabase"/> and raises events for the ViewModel to
/// pick up (marshaling to the UI thread is the subscriber's responsibility).
/// </summary>
public sealed class PollingService : IAsyncDisposable
{
    private readonly IRouterProvider _provider;
    private readonly HistoryDatabase _db;
    private readonly ILogger<PollingService> _logger;
    private readonly TimeSpan _interval;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _isFirstPoll = true;
    private readonly Dictionary<string, NetworkDevice> _onlineDevices = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    public event Action<RouterOverview, IReadOnlyList<NetworkDevice>>? DataUpdated;
    public event Action<Exception>? PollFailed;
    public event Action<IReadOnlyList<DeviceStatusChange>>? DevicesConnected;
    public event Action<IReadOnlyList<NetworkDevice>>? DevicesDisconnected;

    /// <summary>
    /// Fired as each real step of a poll completes (login/overview fetch/device fetch/persist),
    /// so the UI can drive an actual progress bar instead of a fake animated one.
    /// </summary>
    public event Action<int, string>? ConnectionProgressChanged;

    public PollingService(IRouterProvider provider, HistoryDatabase db, ILogger<PollingService> logger, TimeSpan interval)
    {
        _provider = provider;
        _db = db;
        _logger = logger;
        _interval = interval;
    }

    public void Start()
    {
        if (_loopTask is not null)
            return;

        _cts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Polls immediately instead of waiting for the next scheduled tick - used by the UI's
    /// manual "retry" action. Guarded against overlapping with the scheduled loop's own poll.
    /// </summary>
    public async Task PollNowAsync(CancellationToken cancellationToken = default)
    {
        await _pollGate.WaitAsync(cancellationToken);
        try
        {
            await PollOnceAsync(cancellationToken);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _isFirstPoll = true;

        using var timer = new PeriodicTimer(_interval);
        while (true)
        {
            await _pollGate.WaitAsync(cancellationToken);
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            finally
            {
                _pollGate.Release();
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Diffs this poll's device list against the online set from the previous poll and raises
    /// connect/disconnect events accordingly. Seeds the baseline on the first poll of the run
    /// instead of comparing against an empty set, so devices already online at startup aren't
    /// reported as newly connected.
    /// </summary>
    private void RaiseConnectivityChanges(IReadOnlyList<NetworkDevice> devices, IReadOnlyList<string> newMacs)
    {
        var currentByMac = devices.Where(d => d.MacAddress is not null).ToDictionary(d => d.MacAddress!, d => d);

        if (_isFirstPoll)
        {
            _onlineDevices.Clear();
            foreach (var (mac, device) in currentByMac)
                _onlineDevices[mac] = device;
            return;
        }

        var connected = new List<DeviceStatusChange>();
        foreach (var (mac, device) in currentByMac)
        {
            if (!_onlineDevices.ContainsKey(mac))
                connected.Add(new DeviceStatusChange(device, IsNew: newMacs.Contains(mac)));
            _onlineDevices[mac] = device;
        }

        var disconnectedMacs = _onlineDevices.Keys.Except(currentByMac.Keys).ToList();
        var disconnected = disconnectedMacs.Select(mac => _onlineDevices[mac]).ToList();
        foreach (var mac in disconnectedMacs)
            _onlineDevices.Remove(mac);

        if (connected.Count > 0)
            DevicesConnected?.Invoke(connected);
        if (disconnected.Count > 0)
            DevicesDisconnected?.Invoke(disconnected);
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConnectionProgressChanged?.Invoke(10, "Łączenie z routerem...");
            var overview = await _provider.GetOverviewAsync(cancellationToken);

            ConnectionProgressChanged?.Invoke(55, "Pobieranie listy urządzeń...");
            var devices = await _provider.GetDevicesAsync(cancellationToken);
            var now = DateTimeOffset.Now;

            var downstream = NumericParsing.ExtractLeadingNumber(overview.Find("transfer pobiera", "downstream", "download"));
            var upstream = NumericParsing.ExtractLeadingNumber(overview.Find("transfer wysył", "upstream", "upload"));

            ConnectionProgressChanged?.Invoke(80, "Zapisywanie danych...");
            await _db.RecordTransferSampleAsync(now, overview.Uptime, downstream, upstream, cancellationToken);
            var newMacs = await _db.RecordDeviceSightingsAsync(now, devices, cancellationToken);

            ConnectionProgressChanged?.Invoke(100, "Połączono");

            DataUpdated?.Invoke(overview, devices);
            RaiseConnectivityChanges(devices, newMacs);

            _isFirstPoll = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Odpytywanie routera nie powiodło się.");
            PollFailed?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts?.Dispose();
        _pollGate.Dispose();
    }
}

/// <summary>A device seen online this poll that wasn't online the previous poll; <see cref="IsNew"/> marks a MAC never recorded before.</summary>
public sealed record DeviceStatusChange(NetworkDevice Device, bool IsNew);
