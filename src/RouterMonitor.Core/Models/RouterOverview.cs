using RouterMonitor.Core.Parsing;

namespace RouterMonitor.Core.Models;

/// <summary>
/// Snapshot of /ui/dboard. Kept section-based (rather than flattened into named properties)
/// because the exact label wording is only confirmed by reading real HTML dumps; use
/// <see cref="Find"/> for resilient lookups and <see cref="Sections"/> for the raw data.
/// </summary>
public sealed record RouterOverview(IReadOnlyList<InfoSection> Sections)
{
    /// <summary>Finds the first field across all sections whose label contains any of the candidates.</summary>
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

    public InfoSection? FindSection(params string[] titleCandidates)
    {
        foreach (var section in Sections)
        {
            foreach (var candidate in titleCandidates)
            {
                if (section.Title.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    return section;
            }
        }

        return null;
    }

    public string? Model => Find("model");
    public string? Firmware => Find("firmware", "wersja oprogramowania", "software version");
    public string? Mac => Find("mac", "adres mac");
    public string? Uptime => Find("uptime", "czas pracy", "czas działania", "czas od włączenia");
    public string? WanIp => Find("wan ip", "adres ip wan", "public ip", "adres ip publiczny", "adres ip");
    public string? Gateway => Find("brama", "gateway");
    public string? Dns => Find("dns", "nazwa serwera", "serwer dns", "serwery dns");
}
