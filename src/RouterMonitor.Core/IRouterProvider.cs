using RouterMonitor.Core.Models;

namespace RouterMonitor.Core;

/// <summary>
/// Abstraction over "a home router's web admin panel", so other router models/firmwares
/// can be added later without touching the UI or data layers.
/// </summary>
public interface IRouterProvider
{
    Task LoginAsync(CancellationToken cancellationToken = default);

    Task<RouterOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Devices currently on the network (excludes ones the router only remembers but that are offline).</summary>
    Task<IReadOnlyList<NetworkDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every device the router has ever seen, connected or not — for device-list management/cleanup.</summary>
    Task<IReadOnlyList<NetworkDevice>> GetAllKnownDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes a device from the router's known-devices list (requires <see cref="NetworkDevice.HostId"/>).</summary>
    Task DeleteDeviceAsync(int hostId, CancellationToken cancellationToken = default);

    Task<WanStatus> GetWanStatusAsync(CancellationToken cancellationToken = default);

    Task<WifiStatus> GetWifiAsync(CancellationToken cancellationToken = default);

    /// <summary>Reboots the router. The session will be invalid afterwards since the device restarts.</summary>
    Task RebootAsync(CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);
}
