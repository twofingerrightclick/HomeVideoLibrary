using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.Editing;

namespace HomeVideoLibrary.Desktop.UiTests;

/// <summary>Plans never finish and edits are not supported, so views show their pending state without touching media.</summary>
internal sealed class NoClipEditing : IClipEditingService
{
    /// <summary>Edits that plan nothing and queue into a queue of their own.</summary>
    public static ClipEdits Edits() => new(new NoClipEditing(), new JobQueue(_ => Task.CompletedTask), _ => Task.CompletedTask);

    public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
        CancellationToken cancellationToken = default) => new TaskCompletionSource<ClipCutPlan>().Task;
    public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) =>
        new TaskCompletionSource<ClipCutPlan>().Task;
    public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default) =>
        new TaskCompletionSource<ClipMergePlan>().Task;
    public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise) => throw new NotSupportedException();
    public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
