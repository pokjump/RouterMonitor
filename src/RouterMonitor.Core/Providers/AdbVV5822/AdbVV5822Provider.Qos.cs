using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Core.Providers.AdbVV5822;

/// <summary>
/// Per-device speed limits via the router's own QoS ("Routing i QoS" → "QoS Policers" and
/// "Klasyfikacja ruchu"). Each limited direction is one policer (a token bucket that drops traffic
/// above the rate) plus one classification rule that sends the device's traffic through it:
/// <list type="bullet">
/// <item>download: egress towards the LAN/Wi-Fi bridge, destination IP = device. It has to be
/// egress - on ingress from the WAN the packets still carry the router's public IP (pre-NAT), so a
/// destination-IP rule there never matches;</item>
/// <item>upload: ingress on any interface, source IP = device.</item>
/// </list>
/// Both rules bypass the hardware flow accelerator, which would otherwise skip QoS. Everything this
/// app creates is named <c>RM-&lt;mac hex&gt;-dl|ul</c>; entries with any other name (the ISP's own
/// rules for the TV box and VoIP) are never touched. Verified live on firmware 7.6.0.0010.
/// </summary>
public sealed partial class AdbVV5822Provider
{
    private const string QosBase = "/ui/dboard/settings/routing";
    private const string LanBridgeInterface = "Device.Bridging.Bridge.1.Port.1";

    [GeneratedRegex("^RM-([0-9a-f]{12})-(dl|ul)$")]
    private static partial Regex OwnQosNameRegex();

    private readonly SemaphoreSlim _qosLock = new(1, 1);

    private enum Direction { Download, Upload }

    public async Task<IReadOnlyList<DeviceRateLimit>> GetRateLimitsAsync(CancellationToken cancellationToken = default)
    {
        await _qosLock.WaitAsync(cancellationToken);
        try
        {
            await EnterAdvancedModeAsync(cancellationToken);
            return await ReadRateLimitsAsync(cancellationToken);
        }
        finally
        {
            _qosLock.Release();
        }
    }

    public async Task SetRateLimitAsync(string mac, string ipAddress, int? downloadKbps, int? uploadKbps, CancellationToken cancellationToken = default)
    {
        var macHex = NormalizeMac(mac);
        var ip = CleanIp(ipAddress);

        await _qosLock.WaitAsync(cancellationToken);
        try
        {
            await EnterAdvancedModeAsync(cancellationToken);
            _logger.LogInformation("Ustawianie limitu dla {Mac} ({Ip}): pobieranie {Down} kbps, wysyłanie {Up} kbps.",
                mac, ip, downloadKbps?.ToString() ?? "brak", uploadKbps?.ToString() ?? "brak");

            await ApplyDirectionAsync(macHex, ip, Direction.Download, downloadKbps, cancellationToken);
            await ApplyDirectionAsync(macHex, ip, Direction.Upload, uploadKbps, cancellationToken);

            // Read back what the router actually holds, so a silently ignored form is an error, not a "saved".
            var actual = (await ReadRateLimitsAsync(cancellationToken)).FirstOrDefault(l => NormalizeMac(l.Mac) == macHex);
            if (actual?.DownloadKbps != downloadKbps || actual?.UploadKbps != uploadKbps
                || ((downloadKbps ?? uploadKbps) is not null && actual?.IpAddress != ip))
            {
                throw new InvalidOperationException(
                    $"Router nie zapisał limitu zgodnie z żądaniem (odczytano: pobieranie {actual?.DownloadKbps?.ToString() ?? "brak"}, " +
                    $"wysyłanie {actual?.UploadKbps?.ToString() ?? "brak"}, IP {actual?.IpAddress ?? "brak"}).");
            }
        }
        finally
        {
            _qosLock.Release();
        }
    }

    public async Task RemoveRateLimitAsync(string mac, CancellationToken cancellationToken = default)
    {
        var macHex = NormalizeMac(mac);

        await _qosLock.WaitAsync(cancellationToken);
        try
        {
            await EnterAdvancedModeAsync(cancellationToken);
            _logger.LogInformation("Usuwanie limitu dla {Mac}.", mac);
            await RemoveDirectionAsync(macHex, Direction.Download, cancellationToken);
            await RemoveDirectionAsync(macHex, Direction.Upload, cancellationToken);
        }
        finally
        {
            _qosLock.Release();
        }
    }

    private async Task<IReadOnlyList<DeviceRateLimit>> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var policers = QosPageParser.ParsePolicers(await GetQosHtmlAsync($"{QosBase}/policers", cancellationToken));
        var classes = QosPageParser.ParseClasses(await GetQosHtmlAsync($"{QosBase}/classes", cancellationToken));

        var byMac = new Dictionary<string, (string? Ip, int? Down, int? Up)>();

        foreach (var policer in policers)
        {
            var match = OwnQosNameRegex().Match(policer.Name);
            if (!match.Success)
                continue;

            var editHtml = await GetQosHtmlAsync($"{QosBase}/policers/policersedit?instance={policer.Instance}", cancellationToken);
            var cir = QosPageParser.ReadIntInput(editHtml, "cir");
            var entry = byMac.GetValueOrDefault(match.Groups[1].Value);
            byMac[match.Groups[1].Value] = match.Groups[2].Value == "dl" ? entry with { Down = cir } : entry with { Up = cir };
        }

        foreach (var cls in classes)
        {
            var match = OwnQosNameRegex().Match(cls.Name);
            if (!match.Success)
                continue;

            var ipValue = cls.Criteria.FirstOrDefault(kv => kv.Key.StartsWith("Adres IP", StringComparison.OrdinalIgnoreCase)).Value;
            var ip = ipValue?.Split('/')[0].Trim();
            var entry = byMac.GetValueOrDefault(match.Groups[1].Value);
            byMac[match.Groups[1].Value] = entry with { Ip = entry.Ip ?? ip };
        }

        return byMac
            .Select(kv => new DeviceRateLimit(FormatMac(kv.Key), kv.Value.Ip, kv.Value.Down, kv.Value.Up))
            .ToList();
    }

    private async Task ApplyDirectionAsync(string macHex, string ip, Direction direction, int? kbps, CancellationToken cancellationToken)
    {
        if (kbps is null)
        {
            await RemoveDirectionAsync(macHex, direction, cancellationToken);
            return;
        }

        if (kbps <= 0)
            throw new ArgumentOutOfRangeException(nameof(kbps), "Limit musi być większy od zera.");

        var name = EntryName(macHex, direction);
        var policerInstance = await EnsurePolicerAsync(name, kbps.Value, cancellationToken);
        await EnsureClassAsync(name, ip, direction, policerInstance, cancellationToken);
    }

    private async Task<int> EnsurePolicerAsync(string name, int kbps, CancellationToken cancellationToken)
    {
        var listHtml = await GetQosHtmlAsync($"{QosBase}/policers", cancellationToken);
        var before = QosPageParser.ParsePolicers(listHtml);
        var instance = before.FirstOrDefault(p => p.Name == name)?.Instance;
        var created = false;

        if (instance is null)
        {
            // The "add" link is a plain GET that immediately creates a disabled, default-named policer.
            var afterHtml = await GetQosHtmlAsync($"{QosBase}/policers?action=addqospolicer", cancellationToken);
            instance = NewInstance(before.Select(p => p.Instance), QosPageParser.ParsePolicers(afterHtml).Select(p => p.Instance))
                ?? throw new InvalidOperationException("Router nie utworzył nowego ogranicznika (QoS Policer).");
            created = true;
        }

        try
        {
            var editPath = $"{QosBase}/policers/policersedit?instance={instance}";
            var fields = QosPageParser.ReadFormDefaults(await GetQosHtmlAsync(editPath, cancellationToken));
            fields["name"] = name;
            fields["enablepolicer"] = "true";
            fields["meter"] = "stb";
            fields["cir"] = kbps.ToString();
            // Burst of ~100 ms of traffic, never below the 15 000 B the panel warns about.
            fields["cbs"] = Math.Max(30_000, kbps * 125 / 10).ToString();
            fields["act_green"] = "null";
            fields["act_yellow"] = "drop";
            fields["act_red"] = "drop";
            fields["apply"] = "Zastosuj";
            await PostQosFormAsync(editPath, fields, cancellationToken);
            return instance.Value;
        }
        catch when (created)
        {
            await TryDeleteAsync($"{QosBase}/policers?action=delqospolicer&instance={instance}", cancellationToken);
            throw;
        }
    }

    private async Task EnsureClassAsync(string name, string ip, Direction direction, int policerInstance, CancellationToken cancellationToken)
    {
        var listHtml = await GetQosHtmlAsync($"{QosBase}/classes", cancellationToken);
        var before = QosPageParser.ParseClasses(listHtml);
        var instance = before.FirstOrDefault(c => c.Name == name)?.Instance;
        var created = false;

        if (instance is null)
        {
            var afterHtml = await GetQosHtmlAsync($"{QosBase}/classes?action=addcls", cancellationToken);
            instance = NewInstance(before.Select(c => c.Instance), QosPageParser.ParseClasses(afterHtml).Select(c => c.Instance))
                ?? throw new InvalidOperationException("Router nie utworzył nowej reguły klasyfikacji ruchu.");
            created = true;
        }

        try
        {
            var criteriaPath = $"{QosBase}/classes/criteria?instance={instance}";
            var criteria = QosPageParser.ReadFormDefaults(await GetQosHtmlAsync(criteriaPath, cancellationToken));
            var (on, off) = direction == Direction.Download ? ("dest_ipv4addr", "source_ipv4addr") : ("source_ipv4addr", "dest_ipv4addr");
            criteria.Remove(off + "Enabled");
            criteria[on + "Enabled"] = "true";
            criteria[on + "_addr"] = ip;
            criteria[on + "_mask"] = "255.255.255.255";
            criteria["apply"] = "Zastosuj";
            await PostQosFormAsync(criteriaPath, criteria, cancellationToken);

            var classPath = $"{QosBase}/classes/classification?instance={instance}";
            var classHtml = await GetQosHtmlAsync(classPath, cancellationToken);
            var policerValue = QosPageParser.FindOptionValue(classHtml, "policer", name) ?? $"Device.QoS.Policer.{policerInstance}";
            var fields = QosPageParser.ReadFormDefaults(classHtml);
            fields["alias"] = name;
            fields["enableQosClass"] = "true";
            fields["interface_type"] = direction == Direction.Download ? "Egress" : "Ingress";
            fields["interface"] = direction == Direction.Download ? LanBridgeInterface : "ALL_INTERFACES";
            fields["forwarding"] = "0";
            fields["skipfc"] = "Both";
            fields["result"] = "queue";
            fields["queue"] = "NONE";
            fields["policer"] = policerValue;
            fields["apply"] = "Zastosuj";
            await PostQosFormAsync(classPath, fields, cancellationToken);
        }
        catch when (created)
        {
            await TryDeleteAsync($"{QosBase}/classes?action=delcls&instance={instance}", cancellationToken);
            throw;
        }
    }

    private async Task RemoveDirectionAsync(string macHex, Direction direction, CancellationToken cancellationToken)
    {
        var name = EntryName(macHex, direction);

        // The class references the policer, so it goes first.
        var classes = QosPageParser.ParseClasses(await GetQosHtmlAsync($"{QosBase}/classes", cancellationToken));
        foreach (var cls in classes.Where(c => c.Name == name).OrderByDescending(c => c.Instance))
            await GetQosHtmlAsync($"{QosBase}/classes?action=delcls&instance={cls.Instance}", cancellationToken);

        var policers = QosPageParser.ParsePolicers(await GetQosHtmlAsync($"{QosBase}/policers", cancellationToken));
        foreach (var policer in policers.Where(p => p.Name == name).OrderByDescending(p => p.Instance))
            await GetQosHtmlAsync($"{QosBase}/policers?action=delqospolicer&instance={policer.Instance}", cancellationToken);
    }

    private async Task PostQosFormAsync(string path, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        if (!fields.ContainsKey("action__key"))
            throw new InvalidOperationException($"Formularz {path} nie zawiera klucza action__key - sesja mogła wygasnąć.");

        var html = await _http.PostFormAsync(path, fields, cancellationToken);
        DumpIfEnabled(html, "qos-post");
        if (!html.Contains(QosPageParser.SavedMarker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Router nie potwierdził zapisu ustawień QoS ({path}).");
    }

    private async Task TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await GetQosHtmlAsync(path, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się posprzątać częściowo utworzonego wpisu QoS ({Path}).", path);
        }
    }

    /// <summary>The QoS pages only render in the panel's session-wide "advanced" mode.</summary>
    private Task EnterAdvancedModeAsync(CancellationToken cancellationToken) =>
        GetHtmlEnsureSessionAsync("/ui/dboard?level=2", cancellationToken);

    /// <summary>
    /// Like <see cref="GetHtmlEnsureSessionAsync"/>, but if the session was renewed meanwhile (which
    /// drops advanced mode and makes QoS pages answer "Dostęp zabroniony"), re-enters it and retries once.
    /// </summary>
    private async Task<string> GetQosHtmlAsync(string path, CancellationToken cancellationToken)
    {
        var html = await GetHtmlEnsureSessionAsync(path, cancellationToken);
        if (!html.Contains("Dostęp zabroniony", StringComparison.OrdinalIgnoreCase))
            return html;

        await EnterAdvancedModeAsync(cancellationToken);
        return await GetHtmlEnsureSessionAsync(path, cancellationToken);
    }

    private static int? NewInstance(IEnumerable<int> before, IEnumerable<int> after)
    {
        var known = before.ToHashSet();
        var added = after.Where(i => !known.Contains(i)).ToList();
        return added.Count > 0 ? added.Max() : null;
    }

    private static string EntryName(string macHex, Direction direction) =>
        $"RM-{macHex}-{(direction == Direction.Download ? "dl" : "ul")}";

    private static string NormalizeMac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        if (hex.Length != 12)
            throw new ArgumentException($"Nieprawidłowy adres MAC: {mac}", nameof(mac));
        return hex;
    }

    private static string FormatMac(string hex) =>
        string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));

    /// <summary>The homenet page shows e.g. "192.168.1.38 (DHCP)" - keep just the address.</summary>
    private static string CleanIp(string ipAddress)
    {
        var ip = ipAddress.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!System.Net.IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException($"Nieprawidłowy adres IPv4 urządzenia: {ipAddress}", nameof(ipAddress));
        return ip;
    }
}
