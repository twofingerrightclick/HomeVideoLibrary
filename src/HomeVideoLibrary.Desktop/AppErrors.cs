using System.Diagnostics;
using Avalonia.Threading;

namespace HomeVideoLibrary.Desktop;

/// <summary>Last-resort error reporting: unexpected exceptions are written to a daily log under
/// <c>LocalApplicationData/HomeVideoLibrary/logs</c> and announced through <see cref="Reported"/> instead of crashing.</summary>
public static class AppErrors
{
    private static readonly object LogLock = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeVideoLibrary", "logs");

    /// <summary>A short user-facing message. Raised on the reporting thread.</summary>
    public static event Action<string>? Reported;

    /// <summary>Hooks process-wide handlers. <see cref="AppDomain.UnhandledException"/> can only log: the runtime still
    /// terminates afterwards, so UI and task failures are caught earlier by the other two handlers.</summary>
    public static void Install()
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
        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Report(args.Exception, "Interface");
            args.Handled = true;
        };
    }

    public static void Report(Exception exception, string context)
    {
        var path = Write(exception, context);
        var message = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0].Message : exception.Message;
        Reported?.Invoke(path is null ? $"Something went wrong: {message}" : $"Something went wrong: {message} Details are in {path}.");
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
