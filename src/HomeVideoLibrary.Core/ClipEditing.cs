namespace HomeVideoLibrary.Core;

/// <param name="Fraction">Overall 0..1 for the whole operation; never goes backwards. Null means indeterminate:
/// show an indeterminate bar rather than resetting to 0.</param>
/// <param name="Warning">Something the user should know about a result that still succeeded, reported once.</param>
public sealed record ClipEditProgress(string Stage, double? Fraction = null, string? Warning = null);

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
public sealed record ClipCutPlan(TimeSpan ActualStart, TimeSpan ActualEnd, bool IsLossless, string Note)
{
    /// <summary>How much of the kept picture is re-encoded: zero for a copy, the frames next to each cut for a precise
    /// cut, and all of it when no keyframe falls inside the range.</summary>
    public TimeSpan ReencodedDuration { get; init; }

    /// <summary>Whether the picture is really re-encoded: nothing can be copied because no keyframe falls inside the
    /// range. Re-encoding only the frames next to the cuts is not rendering.</summary>
    public bool RendersVideo => ReencodedDuration > TimeSpan.Zero && ReencodedDuration >= ActualEnd - ActualStart - TimeSpan.FromMilliseconds(1);
}

/// <param name="ReencodedClips">How many sources have their video or audio re-encoded to match the others.</param>
public sealed record ClipMergePlan(bool IsLossless, int ReencodedClips, string Note)
{
    /// <summary>One entry per source, in merge order.</summary>
    public IReadOnlyList<ClipMergeSourcePlan> Clips { get; init; } = [];

    /// <summary>Whether any clip's picture is re-encoded. Converting only sound is not rendering.</summary>
    public bool RendersVideo => Clips.Any(clip => clip.ReencodesVideo);
}

/// <summary>What a merge does with one source.</summary>
/// <param name="Duration">The source's video length.</param>
/// <param name="Reason">Why the source is re-encoded, in plain words ("it is 720×480; the merged video is 1920×1080");
/// null when it is copied.</param>
public sealed record ClipMergeSourcePlan(bool ReencodesVideo, bool ReencodesAudio, TimeSpan Duration, string? Reason);

public interface IClipEditingService
{
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
