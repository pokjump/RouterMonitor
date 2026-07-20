using RouterMonitor.Core.Parsing;

namespace RouterMonitor.Core.Models;

/// <summary>One WiFi band section from the overview page, e.g. "WiFi-1.1 (2.4GHz)".</summary>
public sealed record WifiBand(string Title, string? Ssid, string? Encryption, string? Enabled, IReadOnlyDictionary<string, string> RawFields)
{
    public static WifiBand FromSection(InfoSection section)
    {
        var ssid = LabelMatcher.FindByAnyContains(section.Fields, "ssid", "nazwa sieci");
        var encryption = LabelMatcher.FindByAnyContains(section.Fields, "ochrona", "szyfrowanie", "encryption", "security", "zabezpiecz");

        // The band's own on/off state isn't a separate labeled field — it's the value of the
        // section's own title field (e.g. label "WiFi-1.1 (2.4GHz)" -> value "Aktywne"), since
        // that field both names the section and reports its status. Fall back to a labeled
        // "stan"/"status" field in case a firmware variant does expose one separately.
        var enabled = section.Fields.TryGetValue(section.Title, out var titleValue)
            ? titleValue
            : LabelMatcher.FindByAnyContains(section.Fields, "stan", "status", "enabled", "aktywn");

        return new WifiBand(section.Title, ssid, encryption, enabled, section.Fields);
    }
}

/// <summary>Snapshot of WiFi bands, typically 2.4GHz and 5GHz.</summary>
public sealed record WifiStatus(IReadOnlyList<WifiBand> Bands);
