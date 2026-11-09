using System.Diagnostics;
using System.Text;

namespace HomeVideoLibrary.Infrastructure;

public sealed record MediaProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs media tools without a shell, with bounded output and whole-tree cancellation.</summary>
public sealed class MediaProcess
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<MediaProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default, Action<string>? outputReceived = null)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true,
            // The tools print UTF-8 (paths, tag text) on every platform; the Windows default code page would garble it.
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"Cannot start {executable}. Install the required media tools or configure their executable paths.", exception);
        }
        using var registration = linked.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = ReadBoundedAsync(process.StandardOutput, 4 * 1024 * 1024, outputReceived);
        var stderr = ReadBoundedAsync(process.StandardError, 32 * 1024, null);
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await stdout;
        var error = await stderr;
        cancellationToken.ThrowIfCancellationRequested();
        if (timeoutSource.IsCancellationRequested)
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded its time limit ({timeout}).");
        return new MediaProcessResult(process.ExitCode, output, error);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, Action<string>? received)
    {
        var builder = new StringBuilder();
        Exception? callbackError = null;
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer)) > 0)
        {
            var chunk = new string(buffer, 0, length);
            if (callbackError is null)
            {
                try { received?.Invoke(chunk); }
                catch (Exception exception) { callbackError = exception; }
            }
            builder.Append(chunk);
            if (builder.Length > limit)
                builder.Remove(0, builder.Length - limit);
        }
        if (callbackError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(callbackError).Throw();
        return builder.ToString();
    }
}
