using System.Windows.Input;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A synchronous command. Its owner calls <see cref="RaiseCanExecuteChanged"/> when <c>canExecute</c> may have changed.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A synchronous command taking a <typeparamref name="T"/>; any other parameter cannot execute.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is T value && (canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value && CanExecute(value)) execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>An asynchronous command that cannot re-enter while it runs. Cancellation ends a run quietly; any other
/// exception goes to <c>onError</c> (by default <see cref="AppErrors.Report"/>, which reaches the status line), so a
/// command never faults its caller. <see cref="ExecuteAsync"/> is the awaitable form for code that sequences commands.</summary>
public abstract class AsyncCommandBase : ViewModelBase, ICommand
{
    private readonly Func<object?, CancellationToken, Task> _execute;
    private readonly Func<object?, bool> _canExecute;
    private readonly Action<Exception> _onError;
    private CancellationTokenSource? _cancellation;
    private Task _running = Task.CompletedTask;

    private protected AsyncCommandBase(Func<object?, CancellationToken, Task> execute, Func<object?, bool> canExecute,
        Action<Exception>? onError, string context)
    {
        _execute = execute; _canExecute = canExecute;
        _onError = onError ?? (exception => AppErrors.Report(exception, context));
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _cancellation is not null;

    /// <summary>Completes when the current run (if any) has finished, including its error handling.</summary>
    public Task Completion => _running;

    public bool CanExecute(object? parameter) => !IsRunning && _canExecute(parameter);

    // ICommand.Execute is void by contract; ExecuteAsync never throws, so nothing escapes this bridge.
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return Task.CompletedTask;
        return _running = RunAsync(parameter);
    }

    /// <summary>Requests cancellation of the current run; the run ends when the operation observes it.</summary>
    public void Cancel() => _cancellation?.Cancel();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunAsync(object? parameter)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        OnPropertyChanged(nameof(IsRunning));
        RaiseCanExecuteChanged();
        try { await _execute(parameter, cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _onError(exception); }
        finally
        {
            _cancellation = null;
            OnPropertyChanged(nameof(IsRunning));
            RaiseCanExecuteChanged();
        }
    }
}

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null,
    Action<Exception>? onError = null, string context = "Command")
    : AsyncCommandBase((_, token) => execute(token), _ => canExecute?.Invoke() ?? true, onError, context);

/// <summary>An <see cref="AsyncCommandBase"/> taking a <typeparamref name="T"/>; any other parameter cannot execute.</summary>
public sealed class AsyncCommand<T>(Func<T, CancellationToken, Task> execute, Func<T, bool>? canExecute = null,
    Action<Exception>? onError = null, string context = "Command")
    : AsyncCommandBase((parameter, token) => execute((T)parameter!, token),
        parameter => parameter is T value && (canExecute?.Invoke(value) ?? true), onError, context);
