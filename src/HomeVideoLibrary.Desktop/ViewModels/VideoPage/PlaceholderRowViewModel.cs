namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>The one "new chapter goes here" row. It follows the playhead through the chapter list by moving, and is
/// hidden (never removed or promoted) while a chapter already starts at the playhead.</summary>
public sealed class PlaceholderRowViewModel : ViewModelBase
{
    private TimeSpan _start;
    private bool _isShown;

    public TimeSpan Start { get => _start; set => SetProperty(ref _start, value); }
    public bool IsShown { get => _isShown; set => SetProperty(ref _isShown, value); }
}
