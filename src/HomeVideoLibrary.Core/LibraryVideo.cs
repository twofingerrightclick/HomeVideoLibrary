namespace HomeVideoLibrary.Core;

public sealed record LibraryVideo(Guid Id, string RelativePath, string DisplayName, TimeSpan Duration, DateTimeOffset ImportedAt,
    DateTimeOffset RecordedAt);

public sealed record ImportProgress(string Stage, double? Fraction = null);

public interface IVideoImporter
{
    Task<LibraryVideo> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
