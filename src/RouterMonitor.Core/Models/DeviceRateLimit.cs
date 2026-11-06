namespace RouterMonitor.Core.Models;

/// <summary>
/// A speed limit for one device, as currently configured in the router. <see cref="IpAddress"/> is
/// the address the router's rules match on (rules are IP-based); <see cref="Mac"/> identifies the
/// device so the rules can be moved when the device gets a different IP.
/// </summary>
public sealed record DeviceRateLimit(string Mac, string? IpAddress, int? DownloadKbps, int? UploadKbps);
