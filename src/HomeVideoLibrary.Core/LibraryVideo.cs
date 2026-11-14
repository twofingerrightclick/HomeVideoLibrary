namespace HomeVideoLibrary.Core;

public sealed record LibraryVideo(Guid Id, string RelativePath, string DisplayName, TimeSpan Duration, DateTimeOffset ImportedAt,
    DateTimeOffset RecordedAt)
{
    // These fields are a rebuildable MKV metadata projection, never the catalog's authority.
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? SourceFingerprint { get; init; }
    public string Label => !string.IsNullOrWhiteSpace(Title) ? Title : DisplayName;
}

public sealed record VideoAlbum(Guid Id, string Name);
public sealed record IndexProgress(int Completed, int Total, string VideoName, string? Error = null);

/// <param name="Fraction">Overall 0..1 for the whole import; never goes backwards. Null means indeterminate:
/// show an indeterminate bar rather than resetting to 0.</param>
public sealed record ImportProgress(string Stage, double? Fraction = null);

public enum ImportOutcome { Imported, AlreadyInLibrary, PreviouslyRemoved }

public sealed record ImportResult(ImportOutcome Outcome, LibraryVideo? Video, IReadOnlyList<string> Warnings)
{
    public DeletionHistory? History { get; init; }
}

public enum DeletionKind { Trashed, PermanentlyDeleted }

public sealed record DeletionHistory(string Fingerprint, string Title, DateTimeOffset RemovedAt, DeletionKind Kind);

public interface IVideoImporter
{
    /// <summary>Imports normally; the optional fingerprint bypasses only that source's deletion history, never live duplicates.</summary>
    Task<ImportResult> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default, string? allowedDeletionFingerprint = null);
}
