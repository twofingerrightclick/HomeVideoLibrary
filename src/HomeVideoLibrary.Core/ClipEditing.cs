namespace HomeVideoLibrary.Core;

/// <param name="Fraction">Overall 0..1 for the whole operation; never goes backwards. Null means indeterminate:
/// show an indeterminate bar rather than resetting to 0.</param>
public sealed record ClipEditProgress(string Stage, double? Fraction = null);
public sealed record ClipOutputFormat(int Width, int Height, string FrameRate, string SampleAspectRatio);

public interface IClipEditingService
{
    Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default);
}
