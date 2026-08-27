using RouterMonitor.Core.Parsing;

namespace RouterMonitor.Core.Models;

/// <summary>
/// WAN/DSL snapshot. Built from the overview page's "Połączenie internetowe" and "Połączenie
/// DSL" sub-sections (there's no separate, more detailed /ui/dboard/wan page on this firmware
/// without an edit-form query string) - see AdbVV5822Provider.GetWanStatusAsync.
/// </summary>
public sealed record WanStatus(IReadOnlyList<InfoSection> Sections)
{
    public string? Find(params string[] labelCandidates)
    {
        foreach (var section in Sections)
        {
            var value = LabelMatcher.FindByAnyContains(section.Fields, labelCandidates);
            if (value is not null)
                return value;
        }

        return null;
    }

    public string? IpAddress => Find("adres ip", "ip address");
    public string? Gateway => Find("brama", "gateway");
    public string? ConnectionStatus => Find("połączenie internetowe", "stan", "status", "état");

    // Stems, not full words: Polish inflects "pobierania"/"wysyłania" (genitive) vs.
    // "pobieranie"/"wysyłanie" (nominative) depending on firmware wording.
    public string? DownstreamKbps => Find("download", "pobiera", "downstream");
    public string? UpstreamKbps => Find("upload", "wysył", "upstream");
}
