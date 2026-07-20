namespace RouterMonitor.Wpf.Data;

public sealed record TransferSample(DateTimeOffset Timestamp, double? DownstreamKbps, double? UpstreamKbps);

/// <summary>One device's known window of activity, used by the "who was online" history view.</summary>
public sealed record DeviceSightingSummary(
    string Mac,
    string? Name,
    string? IpAddress,
    string? ConnectionType,
    DateTimeOffset FirstSeenInRange,
    DateTimeOffset LastSeenInRange);
