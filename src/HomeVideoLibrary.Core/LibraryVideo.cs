namespace HomeVideoLibrary.Core;

public sealed record LibraryVideo(Guid Id, string RelativePath, string DisplayName, TimeSpan Duration, DateTimeOffset ImportedAt,
    DateTimeOffset RecordedAt)
{
    // These fields are a rebuildable MKV metadata projection, never the catalog's authority.
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Label => !string.IsNullOrWhiteSpace(Description) ? Description : !string.IsNullOrWhiteSpace(Title) ? Title : DisplayName;
}

public sealed record VideoAlbum(Guid Id, string Name);
public sealed record IndexProgress(int Completed, int Total, string VideoName, string? Error = null);

public sealed record ImportProgress(string Stage, double? Fraction = null);

public interface IVideoImporter
{
    Task<LibraryVideo> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
