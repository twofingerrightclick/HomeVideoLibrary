namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Turns the chunks a <see cref="MediaProcess"/> observer receives into whole lines.</summary>
internal sealed class LineSplitter(Action<string> line)
{
    private const int MaxPendingLength = 64 * 1024;
    private string _pending = "";

    public void Append(string chunk)
    {
        _pending += chunk;
        int end;
        while ((end = _pending.IndexOf('\n')) >= 0)
        {
            line(_pending[..end].Trim());
            _pending = _pending[(end + 1)..];
        }
        if (_pending.Length > MaxPendingLength) _pending = "";
    }
}
