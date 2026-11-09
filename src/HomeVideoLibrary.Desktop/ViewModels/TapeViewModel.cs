using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class TapeViewModel(LibraryVideo video, Action selectionChanged, bool isSelected = false) : ViewModelBase
{
    private bool _isSelected = isSelected;
    public LibraryVideo Video { get; } = video;
    public string Label => Video.Label.Replace('\n', ' ').Replace('\r', ' ');
    public string DateLabel => Video.RecordedAt.ToString("MMM yyyy");
    public string DurationLabel => Video.Duration.TotalHours >= 1 ? Video.Duration.ToString(@"h\:mm\:ss") : Video.Duration.ToString(@"m\:ss");
    public string Accent => new[] { "#947055", "#526F7B", "#7A5559", "#607563" }[Video.Id.ToByteArray()[0] % 4];
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) selectionChanged(); } }
}

public sealed record TapeRow(TapeViewModel Left, TapeViewModel? Right)
{
    public bool HasRight => Right is not null;
}
