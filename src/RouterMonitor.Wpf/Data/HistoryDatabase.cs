using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Wpf.Data;

/// <summary>
/// SQLite-backed history: one row per poll for transfer/uptime, one row per device per poll
/// for presence, and a running "first/last seen" table per MAC used both to answer "who was
/// online yesterday" and to detect brand-new devices for alerting.
/// </summary>
public sealed class HistoryDatabase
{
    private readonly string _connectionString;
    private readonly ILogger<HistoryDatabase> _logger;

    public HistoryDatabase(string databasePath, ILogger<HistoryDatabase> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = $"Data Source={databasePath}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS RouterSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Uptime TEXT,
                DownstreamKbps REAL,
                UpstreamKbps REAL
            );
            CREATE TABLE IF NOT EXISTS DeviceSightings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                Mac TEXT NOT NULL,
                Name TEXT,
                IpAddress TEXT,
                ConnectionType TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_DeviceSightings_Timestamp ON DeviceSightings(Timestamp);
            CREATE INDEX IF NOT EXISTS IX_DeviceSightings_Mac ON DeviceSightings(Mac);
            CREATE TABLE IF NOT EXISTS KnownDevices (
                Mac TEXT PRIMARY KEY,
                Name TEXT,
                FirstSeen TEXT NOT NULL,
                LastSeen TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordTransferSampleAsync(
        DateTimeOffset timestamp, string? uptime, double? downstreamKbps, double? upstreamKbps,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO RouterSamples (Timestamp, Uptime, DownstreamKbps, UpstreamKbps) " +
            "VALUES ($ts, $uptime, $down, $up)";
        command.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$uptime", (object?)uptime ?? DBNull.Value);
        command.Parameters.AddWithValue("$down", (object?)downstreamKbps ?? DBNull.Value);
        command.Parameters.AddWithValue("$up", (object?)upstreamKbps ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TransferSample>> GetRecentTransferSamplesAsync(
        int count, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Timestamp, DownstreamKbps, UpstreamKbps FROM RouterSamples " +
            "ORDER BY Id DESC LIMIT $count";
        command.Parameters.AddWithValue("$count", count);

        var samples = new List<TransferSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var timestamp = DateTimeOffset.Parse(reader.GetString(0));
            var down = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
            var up = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
            samples.Add(new TransferSample(timestamp, down, up));
        }

        samples.Reverse(); // oldest -> newest, for left-to-right charting
        return samples;
    }

    /// <summary>
    /// Records this poll's device list and upserts KnownDevices' first/last-seen timestamps.
    /// Returns the MACs that were never in KnownDevices before this call, for new-device alerting.
    /// </summary>
    public async Task<IReadOnlyList<string>> RecordDeviceSightingsAsync(
        DateTimeOffset timestamp, IReadOnlyList<NetworkDevice> devices, CancellationToken cancellationToken = default)
    {
        var newMacs = new List<string>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.MacAddress))
            {
                _logger.LogWarning("Pominięto urządzenie '{Name}' bez adresu MAC — nie da się śledzić jego historii.", device.Name);
                continue;
            }

            var mac = device.MacAddress;

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO DeviceSightings (Timestamp, Mac, Name, IpAddress, ConnectionType) " +
                    "VALUES ($ts, $mac, $name, $ip, $conn)";
                insert.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
                insert.Parameters.AddWithValue("$mac", mac);
                insert.Parameters.AddWithValue("$name", (object?)device.Name ?? DBNull.Value);
                insert.Parameters.AddWithValue("$ip", (object?)device.IpAddress ?? DBNull.Value);
                insert.Parameters.AddWithValue("$conn", (object?)device.ConnectionType ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            bool alreadyKnown;
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(1) FROM KnownDevices WHERE Mac = $mac";
                check.Parameters.AddWithValue("$mac", mac);
                alreadyKnown = (long)(await check.ExecuteScalarAsync(cancellationToken))! > 0;
            }

            if (!alreadyKnown)
                newMacs.Add(mac);

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO KnownDevices (Mac, Name, FirstSeen, LastSeen) VALUES ($mac, $name, $ts, $ts)
                    ON CONFLICT(Mac) DO UPDATE SET Name = excluded.Name, LastSeen = excluded.LastSeen
                    """;
                upsert.Parameters.AddWithValue("$mac", mac);
                upsert.Parameters.AddWithValue("$name", (object?)device.Name ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$ts", timestamp.ToString("O"));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return newMacs;
    }

    /// <summary>Whether any device has ever been recorded — used to suppress "new device" alerts on the very first poll.</summary>
    public async Task<bool> HasAnyKnownDeviceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(1) FROM KnownDevices";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    /// <summary>Distinct devices seen within [from, to), for a "who was online yesterday" view.</summary>
    public async Task<IReadOnlyList<DeviceSightingSummary>> GetDevicesSeenBetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Mac,
                   (SELECT Name FROM DeviceSightings d2 WHERE d2.Mac = d.Mac AND d2.Timestamp >= $from AND d2.Timestamp < $to ORDER BY d2.Timestamp DESC LIMIT 1) AS Name,
                   (SELECT IpAddress FROM DeviceSightings d2 WHERE d2.Mac = d.Mac AND d2.Timestamp >= $from AND d2.Timestamp < $to ORDER BY d2.Timestamp DESC LIMIT 1) AS IpAddress,
                   (SELECT ConnectionType FROM DeviceSightings d2 WHERE d2.Mac = d.Mac AND d2.Timestamp >= $from AND d2.Timestamp < $to ORDER BY d2.Timestamp DESC LIMIT 1) AS ConnectionType,
                   MIN(Timestamp) AS FirstSeen,
                   MAX(Timestamp) AS LastSeen
            FROM DeviceSightings d
            WHERE Timestamp >= $from AND Timestamp < $to
            GROUP BY Mac
            ORDER BY LastSeen DESC
            """;
        command.Parameters.AddWithValue("$from", from.ToString("O"));
        command.Parameters.AddWithValue("$to", to.ToString("O"));

        var result = new List<DeviceSightingSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new DeviceSightingSummary(
                Mac: reader.GetString(0),
                Name: reader.IsDBNull(1) ? null : reader.GetString(1),
                IpAddress: reader.IsDBNull(2) ? null : reader.GetString(2),
                ConnectionType: reader.IsDBNull(3) ? null : reader.GetString(3),
                FirstSeenInRange: DateTimeOffset.Parse(reader.GetString(4)),
                LastSeenInRange: DateTimeOffset.Parse(reader.GetString(5))));
        }

        return result;
    }
}
