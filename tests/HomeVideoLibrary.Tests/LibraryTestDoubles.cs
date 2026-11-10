using System.Collections.Concurrent;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Tests;

/// <summary>An in-memory stand-in for the MKV tools: text metadata per file path, and fake JPEG output.</summary>
internal sealed class FakeMatroska : IMatroskaFileEditor
{
    private int _reads, _extracts, _captures, _keyframes;
    public ConcurrentDictionary<string, VideoTextMetadata> Content { get; } = new();
    public ConcurrentDictionary<string, string> Failures { get; } = new();
    public Func<string, CancellationToken, Task>? BeforeRead { get; set; }
    public Func<CancellationToken, Task>? BeforeCapture { get; set; }
    /// <summary>Runs before each keyframe capture, with the video path.</summary>
    public Func<string, CancellationToken, Task>? BeforeKeyframe { get; set; }
    public Func<CancellationToken, Task>? BeforeTile { get; set; }
    public string OutputDirectory { get; } = Path.Combine(Path.GetTempPath(), "hvl-fake-" + Guid.NewGuid().ToString("N"));
    public int Reads => _reads;
    public int Extracts => _extracts;
    public int Captures => _captures;
    public int Keyframes => _keyframes;
    /// <summary>The media identity every read reports. Null by default, like a reader that cannot tell: such records get no
    /// hover strips, and a cache entry without one is read again.</summary>
    public string? MediaIdentity { get; init; }

    public static VideoTextMetadata Text(VideoMetadata metadata, TimeSpan? duration = null, params VideoThumbnailRef[] thumbnails) =>
        new(metadata, thumbnails, duration ?? TimeSpan.FromSeconds(10), []);

    public void Set(string path, VideoMetadata metadata, TimeSpan? duration = null, params VideoThumbnailRef[] thumbnails) =>
        Content[Path.GetFullPath(path)] = Text(metadata, duration, thumbnails);

    public async Task<VideoTextMetadata> ReadTextAsync(string mkvPath, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        // Like a real read, the content is what the file held when the read began.
        var path = Path.GetFullPath(mkvPath);
        var failed = Failures.TryGetValue(path, out var failure);
        var text = Content.TryGetValue(path, out var content) ? content : Text(VideoMetadata.Empty);
        if (BeforeRead is { } before) await before(mkvPath, cancellationToken);
        return failed ? throw new InvalidDataException(failure) : text with { MediaIdentity = MediaIdentity };
    }

    public Task WriteStagedAsync(string mkvPath, VideoMetadata metadata, MetadataWriteOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<string> ExtractThumbnailAsync(string mkvPath, Guid videoId, VideoThumbnailRef thumbnail, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _extracts);
        Directory.CreateDirectory(OutputDirectory);
        var path = Path.Combine(OutputDirectory, $"{thumbnail.Id:N}.jpg");
        await File.WriteAllTextAsync(path, "chosen", cancellationToken);
        return path;
    }

    public async Task CaptureFrameAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth = 960,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _captures);
        if (BeforeCapture is { } before) await before(cancellationToken);
        await File.WriteAllTextAsync(outputPath, $"frame {position}", cancellationToken);
    }

    public async Task CaptureKeyframeAsync(string mkvPath, TimeSpan position, string outputPath, int maxWidth,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _keyframes);
        if (BeforeKeyframe is { } before) await before(mkvPath, cancellationToken);
        await File.WriteAllTextAsync(outputPath, $"keyframe {position}", cancellationToken);
    }

    /// <summary>Writes the images' text, one per line, in tile order.</summary>
    public async Task TileAsync(IReadOnlyList<string> images, string outputPath, int columns, CancellationToken cancellationToken = default)
    {
        if (BeforeTile is { } before) await before(cancellationToken);
        var lines = new List<string>();
        foreach (var image in images) lines.Add(await File.ReadAllTextAsync(image, cancellationToken));
        await File.WriteAllLinesAsync(outputPath, lines, cancellationToken);
    }
}

internal static class TestLibrary
{
    public static async Task<LibraryCatalog> CreateCatalogAsync(string root)
    {
        var catalog = new LibraryCatalog(new LibraryPaths(root));
        await catalog.InitializeAsync();
        return catalog;
    }

    /// <summary>Registers a placeholder video file (not real media) in the catalog.</summary>
    public static async Task<LibraryVideo> AddVideoAsync(LibraryCatalog catalog, string name, DateTimeOffset? recordedAt = null)
    {
        var id = Guid.NewGuid();
        var video = new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), name, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow,
            recordedAt ?? DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(catalog.GetVideoPath(video.RelativePath), "source " + name);
        await catalog.AddAsync(video);
        return video;
    }

    public static VideoRecord Record(string name, DateTimeOffset? recordedAt = null, string title = "", string description = "",
        IReadOnlyList<string>? tags = null, IReadOnlyList<VideoChapterRecord>? chapters = null, VideoRecordState state = VideoRecordState.Ok)
    {
        var id = Guid.NewGuid();
        return new VideoRecord(id, LibraryPaths.VideoRelativePath(id), name, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow,
            recordedAt ?? DateTimeOffset.UtcNow)
        {
            Title = title, Description = description, Tags = tags ?? [], Chapters = chapters ?? [], State = state,
            Stamp = state == VideoRecordState.Pending ? null : new FileStamp(1, DateTime.UtcNow.Ticks),
        };
    }

    public static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}

/// <summary>Answers modal questions from a queue (KeepEditing when it runs out) and counts them. Set
/// <see cref="Pending"/> to hold the next answer until the test completes it.</summary>
internal sealed class FakeInteraction : HomeVideoLibrary.Desktop.ViewModels.IInteraction
{
    public Queue<HomeVideoLibrary.Desktop.ViewModels.LeaveChoice> Answers { get; } = new();
    public TaskCompletionSource<HomeVideoLibrary.Desktop.ViewModels.LeaveChoice>? Pending { get; set; }
    public int Asked { get; private set; }
    /// <summary>The reasons the leave questions gave for offering no save, in order (null when they offered one).</summary>
    public List<string?> CannotSave { get; } = [];

    public Task<HomeVideoLibrary.Desktop.ViewModels.LeaveChoice> AskLeaveAsync(string title, string? cannotSave)
    {
        Asked++;
        CannotSave.Add(cannotSave);
        if (Pending is { } pending) { Pending = null; return pending.Task; }
        return Task.FromResult(Answers.TryDequeue(out var answer) ? answer : HomeVideoLibrary.Desktop.ViewModels.LeaveChoice.KeepEditing);
    }

    public List<string> Errors { get; } = [];
    public Task ShowErrorAsync(string title, string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }

    /// <summary>Answers from <see cref="ConfirmAnswers"/>, declining when it runs out, so nothing is ever destroyed by default.</summary>
    public Queue<bool> ConfirmAnswers { get; } = new();
    public List<HomeVideoLibrary.Desktop.ViewModels.Confirmation> Confirmations { get; } = [];
    public Task<bool> ConfirmAsync(HomeVideoLibrary.Desktop.ViewModels.Confirmation confirmation)
    {
        Confirmations.Add(confirmation);
        return Task.FromResult(ConfirmAnswers.TryDequeue(out var answer) && answer);
    }

    public HomeVideoLibrary.Desktop.ViewModels.CloseChoice CloseChoice { get; set; } = HomeVideoLibrary.Desktop.ViewModels.CloseChoice.CancelJobs;
    public int CloseAsked { get; private set; }
    public Task<HomeVideoLibrary.Desktop.ViewModels.CloseChoice> AskCloseWithJobsAsync(int activeJobs)
    {
        CloseAsked++;
        return Task.FromResult(CloseChoice);
    }

    public HomeVideoLibrary.Desktop.ViewModels.PickedFiles Picked { get; set; } = HomeVideoLibrary.Desktop.ViewModels.PickedFiles.None;
    public Task<HomeVideoLibrary.Desktop.ViewModels.PickedFiles> PickVideosAsync() => Task.FromResult(Picked);
    public string? Folder { get; set; }
    /// <summary>The titles of the folder pickers shown, in order.</summary>
    public List<string> FolderTitles { get; } = [];
    public Task<string?> PickFolderAsync(string title)
    {
        FolderTitles.Add(title);
        return Task.FromResult(Folder);
    }
    public Task ShowSettingsAsync(HomeVideoLibrary.Desktop.ViewModels.SettingsViewModel settings) => Task.CompletedTask;
}
