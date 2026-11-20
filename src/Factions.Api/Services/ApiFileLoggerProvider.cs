using Microsoft.Extensions.Logging;

namespace Factions.Api.Services
{
    /// <summary>Routes the framework's own warnings and errors — a hosted service that threw, a
    /// failed shutdown step, an unhandled request exception — into <see cref="ApiFileLog"/>.
    /// Below <see cref="LogLevel.Warning"/> is not written: Information is per-request.</summary>
    public sealed class ApiFileLoggerProvider : ILoggerProvider
    {
        private readonly ApiFileLog _log;

        public ApiFileLoggerProvider(ApiFileLog log) => _log = log;

        public ILogger CreateLogger(string categoryName) => new FileLogger(_log, categoryName);

        public void Dispose() { }

        private sealed class FileLogger : ILogger
        {
            private readonly ApiFileLog _log;
            private readonly string _category;

            public FileLogger(ApiFileLog log, string category)
            {
                _log = log;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel)
                => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                string line = _category + ": " + formatter(state, exception);
                if (exception != null) line += Environment.NewLine + exception;
                _log.Write(logLevel >= LogLevel.Error ? "ERROR" : "WARN", line);
            }
        }
    }
}
