using RouterMonitor.Core.Parsing;

namespace RouterMonitor.Core.Models;

/// <summary>
/// One device panel from /ui/dboard/homenet ("Informacje o urządzeniu"). The homenet page
/// lists every device the router has ever seen, not just those currently online - each has
/// its own "Połączony:" (Tak/Nie) field for that. Use <see cref="IsConnected"/> to filter for
/// devices that are actually on the network right now. <see cref="HostId"/> identifies the
/// device for the router's own delete action and is only populated when parsed from the
/// homenet page itself (null otherwise).
/// </summary>
public sealed record NetworkDevice(
    string Name,
    string? IpAddress,
    string? MacAddress,
    string? ConnectionType,
    bool? IsConnected,
    int? HostId,
    IReadOnlyDictionary<string, string> RawFields)
{
    public static NetworkDevice FromSection(InfoSection section, int? hostId = null)
    {
        var name = LabelMatcher.FindByAnyContains(section.Fields, "nazwa", "name", "hostname", "nom")
                   ?? section.Title;
        var ip = LabelMatcher.FindByAnyContains(section.Fields, "adres ip", "ip address", "adresse ip", "ip");
        var mac = LabelMatcher.FindByAnyContains(section.Fields, "mac");
        var connection = LabelMatcher.FindByAnyContains(section.Fields, "połączenie", "connexion", "connection", "typ", "interfejs");
        var connectedRaw = LabelMatcher.FindByAnyContains(section.Fields, "połączony", "connected");
        var isConnected = ParseConnected(connectedRaw);

        return new NetworkDevice(name, ip, mac, connection, isConnected, hostId, section.Fields);
    }

    private static bool? ParseConnected(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "tak" or "yes" or "connected" or "true" => true,
        "nie" or "no" or "disconnected" or "false" => false,
        _ => null,
    };
}
