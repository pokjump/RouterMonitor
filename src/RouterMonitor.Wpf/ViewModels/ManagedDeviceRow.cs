using CommunityToolkit.Mvvm.ComponentModel;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Wpf.ViewModels;

/// <summary>Wraps a <see cref="NetworkDevice"/> with a mutable, checkbox-bindable selection flag for the device-cleanup view.</summary>
public partial class ManagedDeviceRow(NetworkDevice device, bool isSelected) : ObservableObject
{
    public NetworkDevice Device { get; } = device;

    [ObservableProperty] private bool isSelected = isSelected;

    public string Name => Device.Name;
    public string? IpAddress => Device.IpAddress;
    public string? MacAddress => Device.MacAddress;
    public string? ConnectionType => Device.ConnectionType;
}
