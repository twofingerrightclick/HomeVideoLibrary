using HomeVideoLibrary.Desktop.ViewModels;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class CommandsTests
{
    [Fact]
    public async Task AnAsyncCommandCannotReenterWhileItRuns()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncCommand(async _ => { runs++; await gate.Task; });

        command.Execute(null);
        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));
        command.Execute(null);

        gate.SetResult();
        await command.Completion;
        Assert.Equal(1, runs);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task AFailureIsReportedAndTheCommandBecomesRunnableAgain()
    {
        var reported = new List<Exception>();
        var command = new AsyncCommand(_ => throw new InvalidOperationException("boom"), onError: reported.Add);
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        await command.ExecuteAsync();

        Assert.Equal("boom", Assert.Single(reported).Message);
        Assert.False(command.IsRunning);
        Assert.Equal(2, changes); // Once when the run starts, once when it ends.
    }

    [Fact]
    public async Task CancelEndsARunQuietly()
    {
        var reported = new List<Exception>();
        var command = new AsyncCommand(token => Task.Delay(Timeout.Infinite, token), onError: reported.Add);

        var run = command.ExecuteAsync();
        command.Cancel();
        await run;

        Assert.Empty(reported);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public void ATypedCommandRejectsOtherParameters()
    {
        var command = new AsyncCommand<string>((_, _) => Task.CompletedTask);
        Assert.True(command.CanExecute("tag"));
        Assert.False(command.CanExecute(null));
        Assert.False(command.CanExecute(3));
    }
}
