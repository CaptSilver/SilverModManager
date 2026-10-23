using System.Reactive.Subjects;
using SilverModManager.Abstractions.Logging;
using NLog;
using NLog.Targets;

namespace SilverModManager.App;

public class ObservableLoggingTarget : Target, IObservableExceptionSource
{
    public IObservable<LogMessage> Exceptions => _exceptions;
    
    private Subject<LogMessage> _exceptions = new();
    
    protected override void Write(LogEventInfo logEvent)
    {
        if (logEvent.Level == LogLevel.Error || logEvent.Level == LogLevel.Fatal)
            _exceptions.OnNext(new LogMessage(logEvent.Exception, logEvent.FormattedMessage));
    }
}
