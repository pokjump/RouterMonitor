using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core.Http;
using RouterMonitor.Core.Models;
using RouterMonitor.Core.Parsing;
using RouterMonitor.Core.Providers.AdbVV5822.Crypto;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// IRouterProvider implementation for the ADB VV5822 ("epicentro" panel), firmware
/// VV5822_NETIA_7.6.0.0010. The panel has no JSON/AJAX API - every page is server-rendered
/// HTML parsed with <see cref="InfoFieldParser"/>. Login is a plain-text form POST (no
/// hashing/nonce in this firmware), tracked via a cookie session.
/// </summary>
public sealed class AdbVV5822Provider : IRouterProvider, IDisposable
{
    private static readonly Regex PasswordInputRegex = new("""type\s*=\s*["']?password""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AdbVV5822Options _options;
    private readonly RouterHttpClient _http;
    private readonly ILogger<AdbVV5822Provider> _logger;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private bool _loggedIn;

    public AdbVV5822Provider(AdbVV5822Options options, ILogger<AdbVV5822Provider> logger)
    {
        _options = options;
        _logger = logger;
        _http = new RouterHttpClient(options.BaseAddress, logger, options.Timeout, options.MaxRetries);
    }

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Logowanie do panelu routera {BaseAddress}...", _options.BaseAddress);

        var loginPageHtml = await _http.GetHtmlAsync(_options.LoginPath, cancellationToken);
        DumpIfEnabled(loginPageHtml, "login-page");

        var page = AdbLoginPageParser.Parse(loginPageHtml, _options.BaseAddress);

        // The login form hashes the password client-side (see login_onsubmit() in /ui/login):
        // userPwd = HMAC-SHA256(password, nonce); code998/code999 = HMAC-SHA256(md5crypt(password, ""), nonce).
        // The plaintext password field (origUserPwd) is disabled before submit, so it's omitted here too.
        var (userPwd, code998, code999) = LoginHash.ComputeLoginFields(_options.Password, page.Nonce);

        var postFields = new Dictionary<string, string>
        {
            ["userName"] = _options.Username,
            ["userPwd"] = userPwd,
            ["nonce"] = page.Nonce,
            ["code1"] = page.Code1,
            ["code2"] = page.Code2,
            ["code3"] = page.Code3,
            ["code998"] = code998,
            ["code999"] = code999,
            ["language"] = page.Language,
            ["login"] = "Zaloguj",
        };

        var resultHtml = await _http.PostFormAsync(page.Action.PathAndQuery, postFields, cancellationToken);
        DumpIfEnabled(resultHtml, "login-result");

        if (LooksLikeLoginPage(resultHtml))
        {
            throw new InvalidOperationException(
                "Logowanie nie powiodło się - po POST nadal widoczna jest strona logowania. " +
                "Sprawdź hasło/login oraz zrzut 'login-result' w trybie surowego dumpu.");
        }

        _loggedIn = true;
        _logger.LogInformation("Zalogowano pomyślnie.");
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (!_loggedIn)
            return;

        await _http.GetHtmlAsync(_options.LogoutPath, cancellationToken);
        _loggedIn = false;
        _logger.LogInformation("Wylogowano.");
    }

    public async Task<RouterOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var sections = await GetSectionsAsync(_options.OverviewPath, "overview", cancellationToken);
        return new RouterOverview(sections);
    }

    public async Task<IReadOnlyList<NetworkDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await GetHomenetDevicesAsync(cancellationToken);

        // /ui/dboard/homenet lists every device the router has ever seen, including ones that
        // are currently offline (its own "Połączony: Nie" field) - only surface devices that
        // are actually on the network right now. Keep IsConnected == null (couldn't parse the
        // field) rather than drop it, so a parsing miss fails open.
        return devices.Where(d => d.IsConnected != false).ToList();
    }

    public async Task<IReadOnlyList<NetworkDevice>> GetAllKnownDevicesAsync(CancellationToken cancellationToken = default)
        => await GetHomenetDevicesAsync(cancellationToken);

    public async Task DeleteDeviceAsync(int hostId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Usuwanie urządzenia (hostid={HostId}) z listy znanych urządzeń.", hostId);
        var html = await GetHtmlEnsureSessionAsync($"{_options.HomenetPath}?action=delhost&hostid={hostId}", cancellationToken);
        DumpIfEnabled(html, $"delhost-{hostId}");
    }

    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Wysyłanie żądania ponownego uruchomienia routera...");

        // The reboot page only renders in "advanced mode" - a session-wide flag toggled by
        // this query param (see the "Ustawienia zaawansowane" tab) - so it must be set first.
        await GetHtmlEnsureSessionAsync("/ui/dboard?level=2", cancellationToken);

        var rebootPageHtml = await GetHtmlEnsureSessionAsync(_options.RebootPath, cancellationToken);
        DumpIfEnabled(rebootPageHtml, "reboot-page");

        var actionKey = RebootPageParser.ParseActionKey(rebootPageHtml);
        var postFields = new Dictionary<string, string>
        {
            ["action__key"] = actionKey,
            ["reboot"] = "Uruchom ponownie",
        };

        try
        {
            await _http.PostFormAsync(_options.RebootActionPath, postFields, cancellationToken);
            _logger.LogInformation("Żądanie ponownego uruchomienia zostało wysłane do routera.");
        }
        catch (RouterCommunicationException ex)
        {
            // The router legitimately drops the connection as it restarts - that's the expected
            // outcome of a successful reboot request, not a failure to report to the caller.
            _logger.LogInformation(ex, "Połączenie przerwane w trakcie restartu routera - oczekiwane zachowanie.");
        }
        finally
        {
            _loggedIn = false; // the session is gone once the router actually restarts
        }
    }

    private async Task<IReadOnlyList<NetworkDevice>> GetHomenetDevicesAsync(CancellationToken cancellationToken)
    {
        var html = await GetHtmlEnsureSessionAsync(_options.HomenetPath, cancellationToken);
        DumpIfEnabled(html, "homenet");

        var blocks = HomenetPageParser.ParseDeviceBlocks(html);
        if (blocks.Count == 0)
            _logger.LogWarning("Strona {Path} nie zwróciła żadnych urządzeń - sprawdź zrzut 'homenet'.", _options.HomenetPath);

        var devices = new List<NetworkDevice>();
        foreach (var block in blocks)
        {
            // The page's own top-level panel header (e.g. "Sieć domowa") parses as an empty
            // section - expected, not a warning-worthy anomaly; only real device panels have fields.
            if (block.Section.Fields.Count == 0)
                continue;

            devices.Add(NetworkDevice.FromSection(block.Section, block.HostId));
        }

        return devices;
    }

    public async Task<WanStatus> GetWanStatusAsync(CancellationToken cancellationToken = default)
    {
        // No standalone WAN page (see AdbVV5822Options) - WAN facts live in the overview's
        // "Połączenie internetowe" sub-section, found generically via WanStatus.Find(...).
        var sections = await GetSectionsAsync(_options.OverviewPath, "overview", cancellationToken);
        return new WanStatus(sections);
    }

    public async Task<WifiStatus> GetWifiAsync(CancellationToken cancellationToken = default)
    {
        // No standalone WiFi page (see AdbVV5822Options) - each band is its own sub-section
        // of the overview page, titled e.g. "WiFi-1.1 (2.4GHz)" / "WiFi-2.1 (5GHz)".
        var sections = await GetSectionsAsync(_options.OverviewPath, "overview", cancellationToken);
        var bands = sections
            .Where(s => s.Title.Contains("wifi", StringComparison.OrdinalIgnoreCase) && s.Fields.Count > 0)
            .Select(WifiBand.FromSection)
            .ToList();

        return new WifiStatus(bands);
    }

    private async Task<IReadOnlyList<InfoSection>> GetSectionsAsync(string path, string dumpName, CancellationToken cancellationToken)
    {
        var html = await GetHtmlEnsureSessionAsync(path, cancellationToken);
        DumpIfEnabled(html, dumpName);

        var sections = InfoFieldParser.ParseSections(html);
        if (sections.Count == 0)
            _logger.LogWarning("Strona {Path} nie zwróciła żadnych sekcji .infoField/.panel - sprawdź zrzut '{DumpName}'.", path, dumpName);

        return sections;
    }

    private async Task<string> GetHtmlEnsureSessionAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken);

        var html = await _http.GetHtmlAsync(path, cancellationToken);
        if (!LooksLikeLoginPage(html))
            return html;

        _logger.LogWarning("Sesja wygasła podczas pobierania {Path} - logowanie ponowne.", path);
        await ForceLoginAsync(cancellationToken);
        return await _http.GetHtmlAsync(path, cancellationToken);
    }

    /// <summary>
    /// Serializes login attempts through <see cref="_sessionLock"/> - the polling loop and the
    /// view model's own startup calls (e.g. loading inactive devices) both hit this provider
    /// concurrently, and two simultaneous LoginAsync calls race on the router's single session,
    /// corrupting it. Double-checks <see cref="_loggedIn"/> after acquiring the lock so a caller
    /// that lost the race doesn't log in again unnecessarily.
    /// </summary>
    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
            return;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_loggedIn)
                await LoginAsync(cancellationToken);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task ForceLoginAsync(CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            await LoginAsync(cancellationToken);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static bool LooksLikeLoginPage(string html)
        => PasswordInputRegex.IsMatch(html) && !html.Contains("infoField", StringComparison.OrdinalIgnoreCase);

    private void DumpIfEnabled(string html, string name)
    {
        if (string.IsNullOrWhiteSpace(_options.RawDumpDirectory))
            return;

        Directory.CreateDirectory(_options.RawDumpDirectory);
        var fileName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}.html";
        var path = Path.Combine(_options.RawDumpDirectory, fileName);
        File.WriteAllText(path, html);
        _logger.LogDebug("Zapisano surowy zrzut HTML: {Path}", path);
    }

    public void Dispose()
    {
        _http.Dispose();
        _sessionLock.Dispose();
    }
}
