using System.IO;
using System.Text.Json;

namespace RouterMonitor.Wpf.Services;

public sealed record AppSettings
{
    public string BaseAddress { get; init; } = "http://192.168.1.1";
    public string Username { get; init; } = "admin";
    public string Password { get; init; } = "admin_netia";
    public int PollIntervalSeconds { get; init; } = 30;
    public string? RawDumpDirectory { get; init; }

    /// <summary>Serves a mobile web view of the app on the LAN (http://&lt;this PC's IP&gt;:WebServerPort).</summary>
    public bool WebServerEnabled { get; init; } = true;
    public int WebServerPort { get; init; } = 1234;

    /// <summary>PIN the phone must enter to open the web view; generated on first run if missing.</summary>
    public string? WebPin { get; init; }

    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RouterMonitor");

    private static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public static string DatabasePath => Path.Combine(AppDataDirectory, "history.db");

    /// <summary>
    /// Loads settings.json from %AppData%\RouterMonitor, creating it with defaults on first run
    /// so the user has an obvious file to edit (credentials, poll interval, base address).
    /// </summary>
    public static AppSettings LoadOrCreateDefault()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                    return EnsureWebPin(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to in-memory defaults below; the settings file is a convenience,
            // not something the app should refuse to start over.
        }

        var defaults = new AppSettings { WebPin = GeneratePin() };
        TrySave(defaults);
        return defaults;
    }

    /// <summary>Older settings.json files predate the web view - give them a PIN and write it back so it stays stable.</summary>
    private static AppSettings EnsureWebPin(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WebPin))
            return settings;

        var updated = settings with { WebPin = GeneratePin() };
        TrySave(updated);
        return updated;
    }

    private static string GeneratePin() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static void TrySave(AppSettings defaults)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal - the app just runs with in-memory defaults for this session.
        }
    }
}
