namespace HomeVideoLibrary.Desktop.ViewModels;

public enum EditorExitDestination { Stay, Library, Window }

/// <summary>Scopes a pending exit destination to the current editor confirmation.</summary>
public sealed class EditorExitState
{
    private EditorExitDestination _destination = EditorExitDestination.Library;
    public void Request(EditorExitDestination destination) => _destination = destination;
    public void Cancel() => _destination = EditorExitDestination.Library;
    public EditorExitDestination Complete()
    {
        var destination = _destination;
        Cancel();
        return destination;
    }
}
