using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Infrastructure;
using LibVLCSharp.Shared;

// Runs from a relocated COPY of the packaged MacOS directory, using its own .NET runtime.
internal static class Program
{
    private static async Task Main(string[] args)
    {
        var root = args[0];
        var nativeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../Resources/Native"));
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        Require(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('/') == AppContext.BaseDirectory.TrimEnd('/'), "The test did not load the packaged .NET runtime.");
        var tools = MediaTools.Discover();
        foreach (var tool in new[] { tools.Ffmpeg, tools.Ffprobe, tools.MkvMerge, tools.MkvExtract, tools.MkvPropEdit })
            Require(tool.StartsWith(nativeRoot + "/bin/", StringComparison.Ordinal), "Discovered a non-bundled tool: " + tool);
        Require((await tools.FindUnavailableAsync()).Count == 0, "A bundled tool failed its version probe.");
        var process = new MediaProcess();
        async Task<string> ToolAsync(string executable, params string[] arguments)
        {
            var result = await process.RunAsync(executable, arguments, TimeSpan.FromSeconds(60));
            Require(result.ExitCode == 0, executable + ": " + result.StandardError);
            return result.StandardOutput;
        }

        var source = Path.Combine(root, "generated source.mp4");
        await ToolAsync(tools.Ffmpeg, "-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=160x96:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(root, "test library")));
        await catalog.InitializeAsync();
        var import = await new VideoImporter(catalog, tools).ImportAsync(source);
        Require(import.Outcome == ImportOutcome.Imported, "MP4 import did not complete.");
        var sourceHashAfterImport = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Require(sourceHash.SequenceEqual(sourceHashAfterImport), "Import modified the source.");
        var video = import.Video;
        var path = catalog.GetVideoPath(video.RelativePath);
        var probe = await ToolAsync(tools.Ffprobe, "-v", "error", "-show_entries", "format=format_name,duration", "-of", "json", path);
        Require(probe.Contains("matroska", StringComparison.Ordinal), "Imported output is not MKV.");
        // Exercise mkvmerge separately: the importer may use only ffmpeg for this source.
        await ToolAsync(tools.MkvMerge, "--ui-language", "en_US", "-o", Path.Combine(root, "remuxed.mkv"), path);

        var service = new MatroskaMetadataService(catalog, tools);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        var metadata = new VideoMetadata("Bundled tools ✓", "Description & memories", ["family", "package smoke"],
            new DateTimeOffset(1997, 7, 3, 12, 0, 0, TimeSpan.Zero),
            [new VideoChapter(100, TimeSpan.Zero, "Arrival", "At home", ["hello"]),
                new VideoChapter(200, TimeSpan.FromSeconds(1.25), "Cake", "Make a wish", ["birthday"])], [thumbnail]);
        await service.SaveAsync(video, metadata);
        var read = await new MatroskaMetadataService(new LibraryCatalog(catalog.Paths), tools).ReadAsync(video);
        Require(read.Title == metadata.Title && read.Description == metadata.Description && read.RecordedAt == metadata.RecordedAt,
            "Metadata text did not round trip.");
        Require(read.Tags.SequenceEqual(metadata.Tags) && read.Chapters.Count == 2 && read.Chapters[1].Uid == 200 &&
            read.Chapters[1].Position == TimeSpan.FromSeconds(1.25) && read.Chapters[1].Description == "Make a wish" &&
            read.Thumbnails.Count == 1 && read.Thumbnails[0].Id == thumbnail.Id, "Chapters/tags/thumbnail did not round trip.");

        // Exercise the application's actual LibVLC initialization, including its native environment setup.
        var getEngine = typeof(LibVlcPlayback).GetMethod("GetEngine", BindingFlags.NonPublic | BindingFlags.Static)!;
        var engine = (LibVLC)getEngine.Invoke(null, null)!;
        using var media = new Media(engine, new Uri(path));
        media.AddOption(":avcodec-hw=none");
        media.AddOption(":no-audio");
        using var player = new MediaPlayer(engine);
        var frameCount = 0;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pixels = Marshal.AllocHGlobal(160 * 96 * 4);
        MediaPlayer.LibVLCVideoLockCb lockFrame = (_, planes) => { Marshal.WriteIntPtr(planes, pixels); return IntPtr.Zero; };
        MediaPlayer.LibVLCVideoDisplayCb displayFrame = (_, _) => Interlocked.Increment(ref frameCount);
        player.SetVideoFormat("RV32", 160, 96, 160 * 4);
        player.SetVideoCallbacks(lockFrame, null, displayFrame);
        player.EndReached += (_, _) => finished.TrySetResult();
        player.EncounteredError += (_, _) => finished.TrySetException(new InvalidOperationException("Bundled LibVLC decode failed."));
        try
        {
            Require(player.Play(media), "LibVLC did not start playback.");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Require(frameCount >= 48, $"Too few decoded frames: {frameCount}.");
            var loaded = Enumerable.Range(0, checked((int)ImageCount())).Select(index => Marshal.PtrToStringUTF8(ImageName((uint)index))!)
                .Where(name => name.Contains("libvlc", StringComparison.Ordinal) || name.Contains("_plugin.dylib", StringComparison.Ordinal)).ToArray();
            Require(loaded.Any(name => Path.GetFileName(name).StartsWith("libvlc.", StringComparison.Ordinal)), "LibVLC was not found in dyld's loaded images: " + string.Join(", ", loaded));
            Require(loaded.All(name => name.StartsWith(nativeRoot + "/", StringComparison.Ordinal)), "A non-bundled LibVLC/plugin was loaded: " + string.Join(", ", loaded));
            Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", runtime = RuntimeEnvironment.GetRuntimeDirectory(),
                localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                tools, importedSeconds = video.Duration.TotalSeconds, metadata = "title, description, tags, recording date, chapters and JPEG round trip",
                decodedFrames = frameCount, loadedVlcImages = loaded }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            player.Stop();
            player.Dispose();
            Marshal.FreeHGlobal(pixels);
            GC.KeepAlive(lockFrame);
            GC.KeepAlive(displayFrame);
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_image_count")]
    private static extern uint ImageCount();
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_dyld_get_image_name")]
    private static extern IntPtr ImageName(uint index);
}
