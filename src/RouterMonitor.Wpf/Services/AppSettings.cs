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
                    return loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to in-memory defaults below; the settings file is a convenience,
            // not something the app should refuse to start over.
        }

        var defaults = new AppSettings();
        TrySaveDefaults(defaults);
        return defaults;
    }

    private static void TrySaveDefaults(AppSettings defaults)
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
