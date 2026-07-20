using System.IO;
using Microsoft.Extensions.Logging;

namespace RouterMonitor.Wpf.Services;

/// <summary>Minimal rolling-file logger so alerts and errors leave a durable trail (spec: "wpis w logu"), not just tray toasts.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
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
