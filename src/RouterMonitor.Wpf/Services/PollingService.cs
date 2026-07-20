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

    public event Action<RouterOverview, IReadOnlyList<NetworkDevice>>? DataUpdated;
    public event Action<Exception>? PollFailed;
    public event Action<IReadOnlyList<NetworkDevice>>? NewDevicesDetected;

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

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _isFirstPoll = !await _db.HasAnyKnownDeviceAsync(cancellationToken);

        using var timer = new PeriodicTimer(_interval);
        while (true)
        {
            await PollOnceAsync(cancellationToken);

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

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var overview = await _provider.GetOverviewAsync(cancellationToken);
            var devices = await _provider.GetDevicesAsync(cancellationToken);
            var now = DateTimeOffset.Now;

            var downstream = NumericParsing.ExtractLeadingNumber(overview.Find("transfer pobiera", "downstream", "download"));
            var upstream = NumericParsing.ExtractLeadingNumber(overview.Find("transfer wysył", "upstream", "upload"));
            await _db.RecordTransferSampleAsync(now, overview.Uptime, downstream, upstream, cancellationToken);

            var newMacs = await _db.RecordDeviceSightingsAsync(now, devices, cancellationToken);

            DataUpdated?.Invoke(overview, devices);

            // Suppress alerts on the app's very first-ever poll — otherwise every device
            // already on the household network would be reported as "new".
            if (newMacs.Count > 0 && !_isFirstPoll)
            {
                var newDevices = devices.Where(d => d.MacAddress is not null && newMacs.Contains(d.MacAddress)).ToList();
                if (newDevices.Count > 0)
                    NewDevicesDetected?.Invoke(newDevices);
            }

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
    }
}
