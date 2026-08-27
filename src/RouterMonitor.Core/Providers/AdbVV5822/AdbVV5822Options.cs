namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>Connection settings for the ADB VV5822 "epicentro" panel (firmware VV5822_NETIA_7.6.0.0010).</summary>
public sealed record AdbVV5822Options
{
    public Uri BaseAddress { get; init; } = new("http://192.168.1.1");

    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>Path that serves the login form when not authenticated.</summary>
    public string LoginPath { get; init; } = "/ui/login";

    public string LogoutPath { get; init; } = "/ui/logout";

    public string OverviewPath { get; init; } = "/ui/dboard";

    public string HomenetPath { get; init; } = "/ui/dboard/homenet";

    /// <summary>Reboot confirmation page - only renders once the session is in "advanced mode" (see AdbVV5822Provider.RebootAsync).</summary>
    public string RebootPath { get; init; } = "/ui/dboard/system/reboot?backto=home";

    public string RebootActionPath { get; init; } = "/ui/dboard/system/reboot/action";

    // No separate WifiPath/WanPath/LanPath: on the live firmware, /ui/dboard/wifi and
    // /ui/dboard/wan without an "?edit=...&if=..." query just re-render the same overview
    // page (confirmed by diffing live responses). WAN/WiFi/LAN data is instead read from the
    // sub-sections that OverviewPath's page splits itself into - see InfoFieldParser.

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxRetries { get; init; } = 3;

    /// <summary>When set, every fetched page is saved here as a timestamped .html file for parser debugging.</summary>
    public string? RawDumpDirectory { get; init; }
}
