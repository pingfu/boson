using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Boson.Util;

/// <summary>
/// JSONL file log at /var/log/boson/boson.log, rolling 10 MB × 5 (spec §17).
/// </summary>
public sealed class RollingFileLoggerProvider(
    string path, long maxBytes = 10 * 1024 * 1024, int keep = 5) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTimeOffset.UtcNow.ToString("O"),
            level = level.ToString().ToLowerInvariant(),
            category,
            message,
            exception = exception?.ToString(),
        });

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= maxBytes) Roll();
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the daemon down.
            }
        }
    }

    private void Roll()
    {
        var oldest = $"{path}.{keep}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = keep - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            if (File.Exists(from)) File.Move(from, $"{path}.{i + 1}", overwrite: true);
        }
        File.Move(path, $"{path}.1", overwrite: true);
    }

    public void Dispose() { }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
