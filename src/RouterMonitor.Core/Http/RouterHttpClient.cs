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

    public Uri BaseAddress { get; }

    public RouterHttpClient(Uri baseAddress, ILogger logger, TimeSpan timeout, int maxRetries = 3)
    {
        BaseAddress = baseAddress;
        _logger = logger;
        _maxRetries = Math.Max(1, maxRetries);

        var handler = new HttpClientHandler
        {
            CookieContainer = new System.Net.CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout,
        };
    }

    public async Task<string> GetHtmlAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<string> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) },
            cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                if (attempt >= _maxRetries)
                    throw new RouterCommunicationException($"Żądanie HTTP nie powiodło się po {_maxRetries} próbach.", ex);

                var delay = TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex, "Żądanie HTTP nieudane (próba {Attempt}/{Max}), ponawiam za {DelayMs} ms", attempt, _maxRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken)
        => ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => !cancellationToken.IsCancellationRequested, // true timeout, not caller cancellation
            _ => false,
        };

    public void Dispose() => _http.Dispose();
}

public sealed class RouterCommunicationException(string message, Exception? inner) : Exception(message, inner);
