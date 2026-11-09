namespace HomeVideoLibrary.Core;

/// <param name="Fraction">Overall 0..1 for the whole operation; never goes backwards. Null means indeterminate:
/// show an indeterminate bar rather than resetting to 0.</param>
/// <param name="Warning">Something the user should know about a result that still succeeded, reported once.</param>
public sealed record ClipEditProgress(string Stage, double? Fraction = null, string? Warning = null);
public sealed record ClipOutputFormat(int Width, int Height, string FrameRate, string SampleAspectRatio);

public enum ClipCutMode
{
    /// <summary>Copies whole groups of pictures; cuts snap to keyframes so nothing requested is lost.</summary>
    Fast,

    /// <summary>Cuts at the exact frames by re-encoding only the frames between each cut and its nearest keyframe.
    /// Falls back to <see cref="Fast"/> for sources where that cannot be done safely.</summary>
    Precise,
}

/// <summary>What a cut will really produce, in source time.</summary>
/// <param name="IsLossless">True when no frame is re-encoded.</param>
/// <param name="Note">A short explanation for the user, including why a precise cut fell back to keyframes.</param>
public sealed record ClipCutPlan(TimeSpan ActualStart, TimeSpan ActualEnd, bool IsLossless, string Note);

/// <param name="ReencodedClips">How many sources have their video or audio re-encoded to match the others.</param>
public sealed record ClipMergePlan(bool IsLossless, int ReencodedClips, string Note);

public interface IClipEditingService
{
    Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default);

    Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
        CancellationToken cancellationToken = default);

    /// <summary>Plans a split; <see cref="ClipCutPlan.ActualStart"/> is where the second part will start.</summary>
    Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default);

    Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise);

    /// <summary>Splits losslessly at the keyframe nearest <paramref name="position"/>.</summary>
    Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default);
}
