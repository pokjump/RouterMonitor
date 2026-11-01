using Microsoft.Extensions.Logging;

namespace RouterMonitor.Core.Http;

/// <summary>
/// Thin HTTP wrapper around a cookie-based session with the router's web UI:
/// timeouts, retry with backoff, and GET/POST helpers. No router-specific knowledge lives here.
/// </summary>
public sealed class RouterHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly int _maxRetries;
    private readonly TimeSpan _defaultTimeout;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public Uri BaseAddress { get; }

    public RouterHttpClient(Uri baseAddress, ILogger logger, TimeSpan timeout, int maxRetries = 3)
    {
        BaseAddress = baseAddress;
        _logger = logger;
        _maxRetries = Math.Max(1, maxRetries);
        _defaultTimeout = timeout;

        var handler = new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            // Timeouts are enforced per request in SendWithRetryAsync instead, so slow pages
            // (e.g. the home-network device list) can get a longer budget than quick ones.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // The router's embedded web server accepts a request on a fresh TCP connection but
        // hangs (until client timeout) on a second request reused over the same keep-alive
        // connection. Force a new connection per request rather than pooling one.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <param name="timeout">Per-attempt timeout; defaults to the client-wide timeout passed to the constructor.</param>
    public async Task<string> GetHtmlAsync(string path, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        using var response = await SendWithRetryAsync(path, () => new HttpRequestMessage(HttpMethod.Get, path), timeout, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        using var response = await SendWithRetryAsync(
            path,
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) },
            timeout,
            cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string path, Func<HttpRequestMessage> requestFactory, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var attemptTimeout = timeout ?? _defaultTimeout;

        // The router's embedded web server can't handle a second request while one is still
        // in flight - a concurrent GET/POST from another caller (e.g. the polling loop and the
        // view model's own startup calls) hangs until timeout instead of queuing. Serialize
        // every request through this client so only one is ever in flight at a time.
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = requestFactory();
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(attemptTimeout);
                    try
                    {
                        // Default completion option buffers the whole body, so the timeout covers
                        // the router rendering the page, not just the response headers.
                        var response = await _http.SendAsync(request, timeoutCts.Token);
                        response.EnsureSuccessStatusCode();
                        return response;
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Router nie odpowiedział na {path} w ciągu {attemptTimeout.TotalSeconds:0} s.", ex);
                    }
                }
                catch (Exception ex) when (IsRetryable(ex, cancellationToken))
                {
                    if (attempt >= _maxRetries)
                        throw new RouterCommunicationException($"Żądanie HTTP {path} nie powiodło się po {_maxRetries} próbach.", ex);

                    var delay = TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));
                    _logger.LogWarning(ex, "Żądanie HTTP {Path} nieudane (próba {Attempt}/{Max}), ponawiam za {DelayMs} ms", path, attempt, _maxRetries, delay.TotalMilliseconds);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken)
        => ex switch
        {
            HttpRequestException => true,
            TimeoutException => true,
            TaskCanceledException => !cancellationToken.IsCancellationRequested, // true timeout, not caller cancellation
            _ => false,
        };

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
    }
}

public sealed class RouterCommunicationException(string message, Exception? inner) : Exception(message, inner);
