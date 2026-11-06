namespace RouterMonitor.Wpf.Data;

public sealed record TransferSample(DateTimeOffset Timestamp, double? DownstreamKbps, double? UpstreamKbps);

