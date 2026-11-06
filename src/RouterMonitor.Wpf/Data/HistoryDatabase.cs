using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RouterMonitor.Core.Models;

namespace RouterMonitor.Wpf.Data;

/// <summary>
/// SQLite-backed storage holding only the table of MACs ever seen, used to tell a brand-new
/// device apart from a returning one for alerting. No per-poll history (transfer samples or
/// "who was online when") is collected.
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
            DROP TABLE IF EXISTS RouterSamples;
            DROP TABLE IF EXISTS DeviceSightings;
            CREATE TABLE IF NOT EXISTS KnownDevices (
                Mac TEXT PRIMARY KEY,
                Name TEXT,
                FirstSeen TEXT NOT NULL,
                LastSeen TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Upserts this poll's devices into KnownDevices (no per-poll presence rows are kept).
    /// Returns the MACs that were never in KnownDevices before this call, for new-device alerting.
    /// </summary>
    public async Task<IReadOnlyList<string>> RegisterKnownDevicesAsync(
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
                _logger.LogWarning("Pominięto urządzenie '{Name}' bez adresu MAC - nie da się rozpoznać, czy jest nowe.", device.Name);
                continue;
            }

            var mac = device.MacAddress;

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
}
