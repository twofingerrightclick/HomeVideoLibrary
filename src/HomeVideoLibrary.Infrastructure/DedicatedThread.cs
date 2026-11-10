using System.Runtime.ExceptionServices;

namespace HomeVideoLibrary.Infrastructure;

internal static class DedicatedThread
{
    /// <summary>Runs <paramref name="work"/> on a new thread in <paramref name="apartment"/> (a COM apartment, so Windows
    /// only) and waits for it. An exception from <paramref name="work"/> is rethrown on the caller with its original stack;
    /// on the new thread it would end the process. The thread does not keep the process alive, so quitting while a shell
    /// dialog it shows is unanswered still exits.</summary>
    public static T Run<T>(ApartmentState apartment, Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        }) { IsBackground = true };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(apartment);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
