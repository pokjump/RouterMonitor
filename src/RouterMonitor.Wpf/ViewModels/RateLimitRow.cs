using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterMonitor.Wpf.Services;

namespace RouterMonitor.Wpf.ViewModels;

/// <summary>
/// One device in the "Limity" tab. The two text boxes hold what the user is typing (Mb/s); they are
/// only overwritten from the router while the user hasn't edited them, so a background poll doesn't
/// wipe out a half-typed value.
/// </summary>
public partial class RateLimitRow : ObservableObject
{
    private bool _dirty;
    private bool _updatingFromRouter;

    public RateLimitRow(RateLimitEntry entry)
    {
        Mac = entry.Mac;
        Update(entry);
    }

    public string Mac { get; }

    [ObservableProperty] private string name = "";
    [ObservableProperty] private string meta = "";
    [ObservableProperty] private string currentText = "";
    [ObservableProperty] private bool hasLimit;
    [ObservableProperty] private string downloadText = "";
    [ObservableProperty] private string uploadText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string message = "";

    partial void OnDownloadTextChanged(string value) => MarkDirty();
    partial void OnUploadTextChanged(string value) => MarkDirty();

    private void MarkDirty()
    {
        if (!_updatingFromRouter)
            _dirty = true;
    }

    public void Update(RateLimitEntry entry)
    {
        Name = entry.Name;
        Meta = string.Join(" · ", new[] { entry.IpAddress, entry.Mac, entry.IsOnline ? null : "poza siecią" }.Where(s => !string.IsNullOrEmpty(s)));
        HasLimit = entry.DownloadKbps is not null || entry.UploadKbps is not null;
        CurrentText = HasLimit
            ? $"Limit: ↓ {FormatMbps(entry.DownloadKbps) ?? "bez limitu"} · ↑ {FormatMbps(entry.UploadKbps) ?? "bez limitu"}"
            : "Bez limitu";

        if (_dirty)
            return;

        _updatingFromRouter = true;
        DownloadText = FormatMbps(entry.DownloadKbps, withUnit: false) ?? "";
        UploadText = FormatMbps(entry.UploadKbps, withUnit: false) ?? "";
        _updatingFromRouter = false;
    }

    public void ResetEdits() => _dirty = false;

    /// <summary>Parses a Mb/s box ("5", "2,5", "2.5", empty = no limit) into kbps.</summary>
    public static bool TryParseMbps(string text, out int? kbps, out string? error)
    {
        kbps = null;
        error = null;
        text = text.Trim();
        if (text.Length == 0)
            return true;

        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps <= 0 || mbps > 1000)
        {
            error = $"„{text}” to nie jest poprawna prędkość (podaj liczbę Mb/s, np. 5 lub 2,5).";
            return false;
        }

        kbps = (int)Math.Round(mbps * 1000);
        return true;
    }

    public static string? FormatMbps(int? kbps, bool withUnit = true)
    {
        if (kbps is null)
            return null;
        var text = (kbps.Value / 1000.0).ToString("0.###", CultureInfo.GetCultureInfo("pl-PL"));
        return withUnit ? text + " Mb/s" : text;
    }
}
