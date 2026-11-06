using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Wpf.Services;

/// <summary>
/// Serves a mobile-first web version of the app on the LAN, so the dashboard and the management
/// actions are reachable from a phone at http://&lt;this PC's IP&gt;:port. Everything except the page
/// itself and the login call requires the PIN-derived session cookie, since the actions (reboot,
/// deleting devices) would otherwise be open to anyone on the Wi-Fi.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private const string CookieName = "rm_session";
    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    private readonly IRouterProvider _provider;
    private readonly PollingService _polling;
    private readonly RateLimitService _rateLimits;
    private readonly ILogger<WebServer> _logger;
    private readonly int _port;
    private readonly string _pin;
    private readonly string _sessionToken;

    private readonly object _stateLock = new();
    private RouterOverview? _overview;
    private IReadOnlyList<NetworkDevice> _devices = [];
    private bool _isOnline;
    private string _statusMessage = "Łączenie z routerem...";
    private DateTimeOffset? _lastUpdated;

    private readonly object _loginLock = new();
    private int _failedLogins;
    private DateTimeOffset _lockedUntil;

    private WebApplication? _app;

    public WebServer(IRouterProvider provider, PollingService polling, RateLimitService rateLimits, int port, string pin, ILogger<WebServer> logger)
    {
        _provider = provider;
        _polling = polling;
        _rateLimits = rateLimits;
        _port = port;
        _pin = pin;
        _logger = logger;

        // Derived from the PIN, so a phone stays logged in across app restarts but is logged out
        // as soon as the PIN in settings.json changes.
        _sessionToken = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(pin), Encoding.UTF8.GetBytes("RouterMonitor.WebSession.v1")));

        _polling.DataUpdated += OnDataUpdated;
        _polling.PollFailed += OnPollFailed;
    }

    public int Port => _port;

    /// <summary>Addresses the phone can use, e.g. "http://192.168.1.23:1234".</summary>
    public IReadOnlyList<string> Urls => GetLanIPv4Addresses().Select(ip => $"http://{ip}:{_port}").ToList();

    /// <summary>Starts listening on all interfaces. Throws if the port is taken or can't be bound.</summary>
    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Any, _port));

        var app = builder.Build();
        MapEndpoints(app);

        await app.StartAsync();
        _app = app;
        _logger.LogInformation("Serwer WWW nasłuchuje na porcie {Port}: {Urls}", _port, string.Join(", ", Urls));
    }

    private void MapEndpoints(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isPublic = path == "/" || path == "/api/login";
            if (!isPublic && !IsAuthenticated(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Cheap CSRF guard on top of the SameSite=Strict cookie: a cross-site form post can't set this header.
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-RouterMonitor"] != "1")
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });

        app.MapGet("/", () => Results.Content(LoadPage(), "text/html; charset=utf-8"));

        app.MapPost("/api/login", (LoginRequest request, HttpContext context) =>
        {
            lock (_loginLock)
            {
                if (DateTimeOffset.Now < _lockedUntil)
                    return Results.Json(new { error = "Za dużo błędnych prób. Spróbuj ponownie za kilka minut." }, statusCode: 429);

                var ok = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(request.Pin?.Trim() ?? ""), Encoding.UTF8.GetBytes(_pin));
                if (!ok)
                {
                    _failedLogins++;
                    if (_failedLogins >= MaxFailedLogins)
                    {
                        _failedLogins = 0;
                        _lockedUntil = DateTimeOffset.Now + LockoutDuration;
                        _logger.LogWarning("Serwer WWW: zablokowano logowanie po {Count} błędnych PIN-ach (ostatnio z {Ip}).",
                            MaxFailedLogins, context.Connection.RemoteIpAddress);
                    }
                    return Results.Json(new { error = "Nieprawidłowy PIN." }, statusCode: 401);
                }

                _failedLogins = 0;
            }

            context.Response.Cookies.Append(CookieName, _sessionToken, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                MaxAge = TimeSpan.FromDays(180),
                Path = "/",
            });
            _logger.LogInformation("Serwer WWW: zalogowano z {Ip}.", context.Connection.RemoteIpAddress);
            return Results.Ok();
        });

        app.MapPost("/api/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(CookieName);
            return Results.Ok();
        });

        app.MapGet("/api/state", () => Results.Json(BuildState()));

        app.MapPost("/api/retry", async (CancellationToken ct) =>
        {
            await _polling.PollNowAsync(ct);
            return Results.Json(BuildState());
        });

        app.MapPost("/api/reboot", async (HttpContext context, CancellationToken ct) =>
        {
            try
            {
                await _provider.RebootAsync(ct);
                _logger.LogWarning("Router został zrestartowany z telefonu ({Ip}).", context.Connection.RemoteIpAddress);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zrestartować routera (żądanie z telefonu).");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        app.MapGet("/api/inactive", async (CancellationToken ct) =>
        {
            try
            {
                var all = await _provider.GetAllKnownDevicesAsync(ct);
                var inactive = all
                    .Where(d => d.IsConnected == false && d.HostId is not null)
                    .OrderBy(d => d.Name)
                    .Select(ToDto);
                return Results.Json(inactive);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się wczytać nieaktywnych urządzeń (żądanie z telefonu).");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        app.MapGet("/api/limits", async (bool? refresh, CancellationToken ct) =>
        {
            try
            {
                if (refresh == true || !_rateLimits.IsLoaded)
                    await _rateLimits.RefreshAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się wczytać limitów prędkości (żądanie z telefonu).");
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }

            return Results.Json(_rateLimits.GetEntries().Select(e => new
            {
                mac = e.Mac,
                name = e.Name,
                ip = e.IpAddress,
                online = e.IsOnline,
                downKbps = e.DownloadKbps,
                upKbps = e.UploadKbps,
            }));
        });

        app.MapPost("/api/limits", async (RateLimitRequest request, HttpContext context, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Mac)
                || request.DownKbps is <= 0 or > 1_000_000
                || request.UpKbps is <= 0 or > 1_000_000)
            {
                return Results.Json(new { error = "Nieprawidłowe dane limitu." }, statusCode: 400);
            }

            try
            {
                await _rateLimits.SetAsync(request.Mac, request.DownKbps, request.UpKbps, ct);
                _logger.LogWarning("Limit prędkości z telefonu ({Ip}) dla {Mac}: pobieranie {Down} kbps, wysyłanie {Up} kbps.",
                    context.Connection.RemoteIpAddress, request.Mac, request.DownKbps?.ToString() ?? "brak", request.UpKbps?.ToString() ?? "brak");
                return Results.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się zmienić limitu prędkości dla {Mac} (żądanie z telefonu).", request.Mac);
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        app.MapPost("/api/devices/{hostId:int}/delete", async (int hostId, CancellationToken ct) =>
        {
            try
            {
                await _provider.DeleteDeviceAsync(hostId, ct);
                return Results.Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nie udało się usunąć urządzenia hostid={HostId} (żądanie z telefonu).", hostId);
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });
    }

    private bool IsAuthenticated(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var value)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(_sessionToken));

    private object BuildState()
    {
        lock (_stateLock)
        {
            var o = _overview;
            return new
            {
                online = _isOnline,
                status = _statusMessage,
                lastUpdated = _lastUpdated?.ToString("HH:mm:ss"),
                model = o?.Model,
                firmware = o?.Firmware,
                uptime = o?.Uptime,
                wanIp = o?.WanIp,
                gateway = o?.Gateway,
                dns = o?.Dns,
                downstream = o?.Find("transfer pobiera", "downstream", "download"),
                upstream = o?.Find("transfer wysył", "upstream", "upload"),
                wifi = o is null
                    ? []
                    : o.Sections
                        .Where(s => s.Title.Contains("wifi", StringComparison.OrdinalIgnoreCase))
                        .Select(WifiBand.FromSection)
                        .Select(b => new { title = b.Title, ssid = b.Ssid, encryption = b.Encryption })
                        .ToArray(),
                devices = _devices.OrderBy(d => d.Name).Select(ToDto).ToArray(),
            };
        }
    }

    private static object ToDto(NetworkDevice d) => new
    {
        hostId = d.HostId,
        name = d.Name,
        ip = d.IpAddress,
        mac = d.MacAddress,
        connection = d.ConnectionType,
    };

    private void OnDataUpdated(RouterOverview overview, IReadOnlyList<NetworkDevice> devices)
    {
        lock (_stateLock)
        {
            _overview = overview;
            _devices = devices;
            _isOnline = true;
            _statusMessage = "Połączono";
            _lastUpdated = DateTimeOffset.Now;
        }
    }

    private void OnPollFailed(Exception ex)
    {
        lock (_stateLock)
        {
            _isOnline = false;
            _statusMessage = $"Błąd połączenia: {ex.Message}";
        }
    }

    private static string? _page;

    private static string LoadPage()
    {
        if (_page is not null)
            return _page;

        using var stream = typeof(WebServer).Assembly.GetManifestResourceStream("RouterMonitor.Web.index.html")
            ?? throw new InvalidOperationException("Brak osadzonej strony index.html.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return _page = reader.ReadToEnd();
    }

    /// <summary>IPv4 addresses of interfaces that are up and have a default gateway (i.e. the real LAN, not virtual adapters).</summary>
    private static IEnumerable<IPAddress> GetLanIPv4Addresses()
    {
        var withGateway = new List<IPAddress>();
        var others = new List<IPAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                            && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address))
                    continue;
                (hasGateway ? withGateway : others).Add(unicast.Address);
            }
        }

        return withGateway.Count > 0 ? withGateway : others;
    }

    public async ValueTask DisposeAsync()
    {
        _polling.DataUpdated -= OnDataUpdated;
        _polling.PollFailed -= OnPollFailed;

        if (_app is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zatrzymanie serwera WWW nie powiodło się.");
            }
            await _app.DisposeAsync();
        }
    }

    internal sealed record LoginRequest(string? Pin);

    internal sealed record RateLimitRequest(string? Mac, int? DownKbps, int? UpKbps);
}
