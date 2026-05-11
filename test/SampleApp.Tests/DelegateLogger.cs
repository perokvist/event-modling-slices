using Microsoft.Extensions.Logging;

namespace SampleApp.Tests;

public class DelegateLoggerProvider : ILoggerProvider
{
    private readonly Action<string> _log;

    public DelegateLoggerProvider(Action<string> log)
    {
        _log = log;
    }

    public ILogger CreateLogger(string categoryName)
        => new DelegateLogger(_log, categoryName);

    public void Dispose() { }

    private class DelegateLogger : ILogger
    {
        private readonly Action<string> _log;
        private readonly string _category;

        public DelegateLogger(Action<string> log, string category)
        {
            _log = log;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            _log($"[{logLevel}] {_category}: {message}");
        }
    }
}

