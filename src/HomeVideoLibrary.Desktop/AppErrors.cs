using System.Diagnostics;
using Avalonia.Threading;

namespace HomeVideoLibrary.Desktop;

/// <summary>Last-resort error reporting: unexpected exceptions are written to a daily log under
/// <c>LocalApplicationData/HomeVideoLibrary/logs</c> and announced through <see cref="Reported"/> instead of crashing.</summary>
public static class AppErrors
{
    private static readonly object LogLock = new();
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(1);
    private static string? _lastSignature;
    private static DateTime _lastReportedUtc;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeVideoLibrary", "logs");

    /// <summary>A short user-facing message. Raised on the reporting thread.</summary>
    public static event Action<string>? Reported;

    /// <summary>Hooks the process-wide handlers; call first in <c>Main</c> so startup failures are logged too.
    /// <see cref="AppDomain.UnhandledException"/> can only log: the runtime still terminates afterwards.</summary>
    public static void InstallProcessHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) Report(exception, "Unhandled");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Report(args.Exception, "Background task");
            args.SetObserved();
        };
    }

    /// <summary>Catches exceptions from UI-thread callbacks (including <c>async void</c> handlers) so they are reported
    /// instead of ending the app. Needs the dispatcher, so call once the framework is initialized.</summary>
    public static void InstallDispatcherHandler() =>
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Report(args.Exception, "Interface");
            args.Handled = true;
        };

    /// <summary>Logs and announces an exception. A repeat of the same failure within a second (for example one thrown
    /// on every rendered frame) is dropped, so the log and the status line cannot flood.</summary>
    public static void Report(Exception exception, string context)
    {
        var signature = $"{context}|{exception.GetType().FullName}|{exception.Message}|{exception.StackTrace?.Split('\n').FirstOrDefault()}";
        var now = DateTime.UtcNow;
        lock (LogLock)
        {
            if (signature == _lastSignature && now - _lastReportedUtc < RepeatWindow) return;
            _lastSignature = signature; _lastReportedUtc = now;
        }
        var path = Write(exception, context);
        var message = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0].Message : exception.Message;
        // A failing subscriber must not throw from here: this can run on the finalizer thread.
        try { Reported?.Invoke(path is null ? $"Something went wrong: {message}" : $"Something went wrong: {message} Details are in {path}."); }
        catch (Exception subscriberFailure) { Trace.TraceError($"Error reporting failed: {subscriberFailure}"); }
    }

    /// <summary>Runs an event handler's work so a failure is reported rather than escaping an <c>async void</c>.</summary>
    public static async void Run(Func<Task> action, string context)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Report(exception, context); }
    }

    private static string? Write(Exception exception, string context)
    {
        Trace.TraceError($"{context}: {exception}");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"app-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (LogLock) File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{context}] {exception}{Environment.NewLine}");
            return path;
        }
        catch (Exception logFailure) when (logFailure is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"Could not write the error log: {logFailure.Message}");
            return null;
        }
    }
}
