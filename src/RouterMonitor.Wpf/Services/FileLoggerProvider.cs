using System.IO;
using Microsoft.Extensions.Logging;

namespace RouterMonitor.Wpf.Services;

/// <summary>Minimal rolling-file logger so alerts and errors leave a durable trail (spec: "wpis w logu"), not just tray toasts.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _writeLock = new();
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private DateTime _lastCleanupDate;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        DeleteOldLogs();
    }

    /// <summary>
    /// Deletes app-*.log files older than <see cref="Retention"/>. Runs at startup and again on the
    /// first write of each new day, since the app normally stays running in the tray for weeks.
    /// </summary>
    private void DeleteOldLogs()
    {
        _lastCleanupDate = DateTime.Today;
        var cutoff = DateTime.Now - Retention;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Write(string line)
    {
        var path = Path.Combine(_directory, $"app-{DateTime.Now:yyyyMMdd}.log");
        lock (_writeLock)
        {
            if (_lastCleanupDate != DateTime.Today)
                DeleteOldLogs();
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    private sealed class FileLogger(string categoryName, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{logLevel}] {categoryName}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            owner.Write(line);
        }
    }
}
