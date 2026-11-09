using System.Diagnostics;
using System.Security.Cryptography;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Creates new videos from library MKVs by stream copy wherever possible; sources are always read-only.
/// mkvmerge carries every track, track tag and foreign attachment; this app's metadata is rewritten on the new timeline.</summary>
public sealed class ClipEditingService : IClipEditingService
{
    private const double MetadataReadWeight = 2, RenderWeight = 80, FinishWeight = 15, PublishWeight = 3;
    private static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(100);
    private readonly LibraryCatalog _catalog;
    private readonly IVideoMetadataService _metadata;
    private readonly IMatroskaFileEditor _editor;
    private readonly MediaProbe _probe;
    private readonly MediaValidator _validator;
    private readonly CutEditor _cuts;
    private readonly MergeEditor _merges;
    private readonly MediaTools _tools;
    private readonly MediaProcess _process = new();

    public ClipEditingService(LibraryCatalog catalog, MediaTools tools, IVideoMetadataService? metadataService = null,
        IMatroskaFileEditor? fileEditor = null)
    {
        _catalog = catalog;
        _tools = tools;
        var matroska = new MatroskaMetadataService(catalog, tools);
        _metadata = metadataService ?? matroska;
        _editor = fileEditor ?? metadataService as IMatroskaFileEditor ?? matroska;
        _probe = new MediaProbe(tools, _process);
        var commands = new MediaCommands(tools, _process);
        _validator = new MediaValidator(tools, _process);
        _cuts = new CutEditor(tools, _process, _probe, commands);
        _merges = new MergeEditor(_probe, commands);
    }

    public async Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default)
    {
        var video = (await ProbeAsync(source, cancellationToken)).Video;
        return new ClipOutputFormat(video.Width, video.Height, video.AverageFrameRate,
            video.SampleAspectRatio is { Length: > 0 } and not "0:1" ? video.SampleAspectRatio : "1:1");
    }

    public async Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
        CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        ValidateRange(probe, start, end);
        var plan = await WithScratchAsync(scratch => _cuts.PlanTrimAsync(probe, start, end, mode, scratch, cancellationToken));
        return plan.ToPublic();
    }

    public async Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        ValidateRange(probe, TimeSpan.Zero, position);
        ValidateRange(probe, position, probe.VideoEnd);
        var split = await _cuts.PlanSplitAsync(probe, position, cancellationToken);
        var note = (split - position).Duration() <= CutPlanner.Epsilon
            ? "Lossless copy; the split falls on a keyframe."
            : $"Lossless copy; the split snaps to the keyframe at {Timecode.Format(split)}.";
        return new ClipCutPlan(split, probe.VideoEnd, true, note);
    }

    public async Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default) =>
        (await PlanMergeCoreAsync(sources, cancellationToken)).ToPublic();

    public async Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        ValidateRange(probe, start, end);
        var name = TitleOrDefault(title, source.DisplayName + " · trim");
        return await ExecuteAsync([source], progress, async (scratch, report) =>
        {
            var plan = await _cuts.PlanTrimAsync(probe, start, end, mode, scratch, cancellationToken);
            EnsureFreeSpace(scratch, probe, plan.Duration, plan.IsLossless ? 1.1 : 2.2);
            var staged = Path.Combine(scratch, "trim.mkv");
            report.Begin(plan.IsLossless ? "Copying the trimmed range" : "Cutting at the exact frames", RenderWeight);
            try { await _cuts.RenderTrimAsync(probe, plan, staged, scratch, local => report.Report(local), cancellationToken); }
            catch (InvalidDataException exception) when (!plan.IsLossless)
            {
                // A smart cut that the encoder or muxer rejects still has a safe lossless alternative.
                Trace.TraceWarning($"Frame-accurate trim of {probe.Path} failed; using keyframes. {exception}");
                plan = await _cuts.PlanTrimAsync(probe, start, end, ClipCutMode.Fast, scratch, cancellationToken);
                ScratchFiles.TryDelete(staged);
                await _cuts.RenderTrimAsync(probe, plan, staged, scratch, local => report.Report(local), cancellationToken);
                report.Warn("A frame-accurate cut was not possible; the cuts snap to keyframes instead.");
            }
            return [new RenderedOutput(name, staged, [new Segment(source, plan.ActualStart, plan.ActualEnd, TimeSpan.Zero)], plan.Duration,
                probe.Mkv.Tracks.Select(track => track.Expected).ToArray(), plan.Joins, [], false)];
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        ValidateRange(probe, TimeSpan.Zero, position);
        ValidateRange(probe, position, probe.VideoEnd);
        var name = TitleOrDefault(title, source.DisplayName);
        return await ExecuteAsync([source], progress, async (scratch, report) =>
        {
            var split = await _cuts.PlanSplitAsync(probe, position, cancellationToken);
            EnsureFreeSpace(scratch, probe, probe.VideoEnd, 1.1);
            var first = Path.Combine(scratch, "part-1.mkv");
            var second = Path.Combine(scratch, "part-2.mkv");
            report.Begin("Splitting at a keyframe", RenderWeight);
            await _cuts.RenderSplitAsync(probe, split, first, second, scratch, local => report.Report(local), cancellationToken);
            var tracks = probe.Mkv.Tracks.Select(track => track.Expected).ToArray();
            return [new RenderedOutput(name + " · part 1", first, [new Segment(source, TimeSpan.Zero, split, TimeSpan.Zero)], split, tracks, [], [], false),
                new RenderedOutput(name + " · part 2", second, [new Segment(source, split, probe.VideoEnd, TimeSpan.Zero)], probe.VideoEnd - split, tracks, [], [], false)];
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        var plan = await PlanMergeCoreAsync(sources, cancellationToken);
        var name = TitleOrDefault(title, "Merged home video");
        return await ExecuteAsync(sources, progress, async (scratch, report) =>
        {
            var staged = Path.Combine(scratch, "merge.mkv");
            var sourceBytes = plan.Clips.Sum(clip => new FileInfo(clip.Source.Path).Length);
            ScratchFiles.EnsureFreeSpace(scratch, (long)(sourceBytes * (plan.Append ? 1.1 : 2.2)));
            MergeOutput? output = null;
            if (plan.Append)
            {
                report.Begin("Joining the clips without re-encoding", RenderWeight);
                output = await _merges.AppendAsync(plan.Clips.Select(clip => clip.Source).ToArray(), staged, local => report.Report(local), cancellationToken);
                if (output is null)
                {
                    Trace.TraceWarning("mkvmerge refused to append the merge sources; joining them as raw streams instead.");
                    plan = MergePlanner.Plan(plan.Clips.Select(clip => clip.Source).ToArray(), await CanEncodeHevcAsync(), forceComposite: true);
                }
            }
            if (output is null)
            {
                report.Begin(plan.IsLossless ? "Joining the clips" : "Re-encoding clips that do not match", RenderWeight);
                output = await _merges.CompositeAsync(plan, staged, scratch, local => report.Report(local), cancellationToken);
                if (!plan.IsLossless) report.Warn(plan.Note);
            }
            var offsets = new[] { TimeSpan.Zero }.Concat(output.Joins).ToArray();
            var segments = plan.Clips.Select((clip, index) => new Segment(sources[index], TimeSpan.Zero, clip.Source.VideoEnd, offsets[index])).ToArray();
            var tracks = plan.Append ? plan.Clips[0].Source.Mkv.Tracks.Select(track => track.Expected).ToArray() : CompositeTracks(plan);
            var planned = plan.Append ? offsets[^1] + plan.Clips[^1].Source.VideoEnd : output.Duration;
            return [new RenderedOutput(name, staged, segments, planned, tracks, output.Joins, output.ReencodedRanges, true)];
        }, cancellationToken);
    }

    private async Task<MergePlan> PlanMergeCoreAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        var probes = new List<EditSource>();
        foreach (var source in sources) probes.Add(await ProbeAsync(source, cancellationToken));
        return MergePlanner.Plan(probes, await CanEncodeHevcAsync());
    }

    private Task<bool> CanEncodeHevcAsync() => VideoEncoding.CanEncodeAsync(_tools, _process, "hevc");

    private static ExpectedTrack[] CompositeTracks(MergePlan plan)
    {
        var video = plan.Clips.FirstOrDefault(clip => clip.CopyVideo)?.Source.VideoTrack.Codec ?? (plan.Video!.CodecName == "hevc" ? "HEVC/H.265/MPEG-H" : "AVC/H.264/MPEG-4p10");
        return [new ExpectedTrack("video", video, plan.Clips[0].Source.VideoTrack.Language),
            .. plan.Audio.Select(target => new ExpectedTrack("audio",
                VideoEncoding.AudioEncoder(target.Reference) is null ? "AAC" : target.Reference.Codec, target.Reference.Language))];
    }

    /// <summary>Renders into a private scratch directory, rewrites and validates each output's metadata, then publishes
    /// every output and commits them to the catalog in one transaction, or none of them.</summary>
    private async Task<IReadOnlyList<LibraryVideo>> ExecuteAsync(IReadOnlyList<LibraryVideo> sources, IProgress<ClipEditProgress>? progress,
        Func<string, EditProgress, Task<IReadOnlyList<RenderedOutput>>> render, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scratch = Path.Combine(_catalog.Paths.ScratchDirectory, "edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var report = new EditProgress(progress, MetadataReadWeight + RenderWeight + FinishWeight + PublishWeight);
        var published = new List<string>();
        var committed = false;
        try
        {
            report.Begin("Reading descriptions, chapters and thumbnails", MetadataReadWeight, determinate: false);
            var metadata = new Dictionary<string, VideoMetadata>();
            foreach (var source in sources.DistinctBy(source => source.RelativePath))
                metadata[source.RelativePath] = await ReadSourceMetadataAsync(source, report, cancellationToken);

            var outputs = await render(scratch, report);
            var prepared = new List<(LibraryVideo Video, string Staged)>();
            foreach (var output in outputs)
            {
                report.Begin("Writing descriptions, chapters and thumbnails", FinishWeight / outputs.Count / 3, determinate: false);
                var content = MapMetadata(output, metadata);
                await _editor.WriteStagedAsync(output.StagedPath, content, cancellationToken: cancellationToken);
                report.Begin("Checking the new video", FinishWeight / outputs.Count * 2 / 3, determinate: false);
                foreach (var warning in await _validator.ValidateStructureAsync(output.StagedPath, output.Tracks, output.Duration, output.Boundaries, cancellationToken))
                    report.Warn(warning);
                foreach (var (start, end) in output.ReencodedRanges)
                {
                    var decoded = await _validator.ValidateDecodedRangeAsync(output.StagedPath, start, end - start,
                        DecodeTolerance.ForEdit(TimeSpan.FromSeconds(0.1)), cancellationToken);
                    if (decoded.ErrorCount > 0) report.Warn($"{decoded.ErrorCount} decoder error(s) in a re-encoded part.");
                }
                var id = Guid.NewGuid();
                var duration = (await _probe.ReadVideoEndAsync(output.StagedPath, cancellationToken)).End;
                var first = output.Segments[0].Source;
                prepared.Add((new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), output.Title, duration, DateTimeOffset.UtcNow,
                    content.RecordedAt ?? first.RecordedAt) { Title = content.Title, Description = content.Description }, output.StagedPath));
            }

            report.Begin("Publishing the new videos", PublishWeight, determinate: false);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (video, staged) in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using (var stream = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(flushToDisk: true);
                var destination = _catalog.GetVideoPath(video.RelativePath);
                File.Move(staged, destination, overwrite: false);
                published.Add(destination);
            }
            await _catalog.AddBatchAsync(prepared.Select(item => item.Video).ToArray(), cancellationToken);
            committed = true;
            report.Complete("Saved new videos; originals unchanged");
            return prepared.Select(item => item.Video).ToArray();
        }
        finally
        {
            if (!committed) foreach (var path in published) ScratchFiles.TryDelete(path);
            ScratchFiles.TryDeleteDirectory(scratch);
        }
    }

    private async Task<VideoMetadata> ReadSourceMetadataAsync(LibraryVideo source, EditProgress report, CancellationToken cancellationToken)
    {
        try { return await _metadata.ReadAsync(source, cancellationToken); }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            Trace.TraceWarning($"Reading {source.RelativePath} for an edit failed; using its text metadata only. {exception}");
            var text = await _editor.ReadTextAsync(_catalog.GetVideoPath(source.RelativePath), cancellationToken);
            report.Warn($"Thumbnails of \"{source.DisplayName}\" could not be read and were left out.");
            return text.Metadata;
        }
    }

    /// <summary>Carries each source's descriptions, chapters and thumbnails onto the output timeline, using the actual
    /// (keyframe-snapped) ranges. Each merged clip that lacks a chapter at its start gets one named after it.</summary>
    private static VideoMetadata MapMetadata(RenderedOutput output, IReadOnlyDictionary<string, VideoMetadata> metadata)
    {
        var chapters = new List<VideoChapter>();
        var thumbnails = new List<VideoThumbnail>();
        var uids = new HashSet<ulong>();
        ulong UniqueUid(ulong uid)
        {
            while (uid == 0 || !uids.Add(uid)) uid = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
            return uid;
        }
        for (var index = 0; index < output.Segments.Count; index++)
        {
            var segment = output.Segments[index];
            var next = index + 1 < output.Segments.Count ? output.Segments[index + 1].Offset : output.Duration;
            var source = metadata[segment.Source.RelativePath];
            var ordered = source.Chapters.OrderBy(chapter => chapter.Position).ToArray();
            var active = ordered.LastOrDefault(chapter => chapter.Position <= segment.Start + CutPlanner.Epsilon);
            var selected = ordered.Where(chapter => chapter.Position > segment.Start + CutPlanner.Epsilon && chapter.Position < segment.End).ToList();
            if (active is not null) selected.Insert(0, active);
            else if (output.IsMerge)
                chapters.Add(new VideoChapter(UniqueUid(0), segment.Offset, source.Title.Length > 0 ? source.Title : segment.Source.DisplayName, "", []));
            foreach (var chapter in selected)
            {
                var position = segment.Offset + (chapter.Position <= segment.Start ? TimeSpan.Zero : chapter.Position - segment.Start);
                if (position < next) chapters.Add(chapter with { Uid = UniqueUid(chapter.Uid), Position = position });
            }
            foreach (var thumbnail in source.Thumbnails.Where(thumbnail => thumbnail.Position >= segment.Start && thumbnail.Position < segment.End))
            {
                var position = segment.Offset + thumbnail.Position - segment.Start;
                if (position < next) thumbnails.Add(thumbnail with { Id = Guid.NewGuid(), Position = position });
            }
        }
        var contents = output.Segments.Select(segment => metadata[segment.Source.RelativePath]).DistinctBy(content => content).ToArray();
        var first = metadata[output.Segments[0].Source.RelativePath];
        var start = output.Segments[0].Start;
        var (recordedAt, recordedAtText) = first.RecordedAt is { } recorded && start > TimeSpan.Zero && RecordedDate.HasTimeOfDay(first.RecordedAtText)
            ? (recorded + start, "")
            : (first.RecordedAt ?? output.Segments[0].Source.RecordedAt, first.RecordedAt is null ? "" : first.RecordedAtText);
        return new VideoMetadata(output.Title,
            string.Join("\n", contents.Select(content => content.Description).Where(description => description.Length > 0)),
            MetadataText.NormalizeTags(contents.SelectMany(content => content.Tags)), recordedAt,
            chapters.OrderBy(chapter => chapter.Position).ToArray(), thumbnails.OrderBy(thumbnail => thumbnail.Position).Take(20).ToArray())
        {
            RecordedAtText = recordedAtText,
        };
    }

    private async Task<EditSource> ProbeAsync(LibraryVideo source, CancellationToken cancellationToken) =>
        await _probe.ProbeAsync(_catalog.GetVideoPath(source.RelativePath), cancellationToken);

    private async Task<T> WithScratchAsync<T>(Func<string, Task<T>> action)
    {
        var scratch = Path.Combine(_catalog.Paths.ScratchDirectory, "edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try { return await action(scratch); }
        finally { ScratchFiles.TryDeleteDirectory(scratch); }
    }

    private static void EnsureFreeSpace(string scratch, EditSource source, TimeSpan length, double factor)
    {
        var bytes = new FileInfo(source.Path).Length * Math.Min(1, length.Ticks / (double)Math.Max(1, source.VideoEnd.Ticks));
        ScratchFiles.EnsureFreeSpace(scratch, (long)(bytes * factor));
    }

    /// <summary>Ranges are checked against the file's probed video length, not the catalog's possibly stale duration.</summary>
    private static void ValidateRange(EditSource source, TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end > source.VideoEnd + CutPlanner.Epsilon || end <= start || end - start < MinimumLength)
            throw new ArgumentOutOfRangeException(nameof(start), "Choose an ordered range inside the video, at least 0.1 seconds long.");
    }

    private static string TitleOrDefault(string title, string fallback) => string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();

    /// <param name="Start">Where the segment begins in the source (actual, after snapping).</param>
    /// <param name="Offset">Where the segment begins in the output.</param>
    private sealed record Segment(LibraryVideo Source, TimeSpan Start, TimeSpan End, TimeSpan Offset);

    private sealed record RenderedOutput(string Title, string StagedPath, IReadOnlyList<Segment> Segments, TimeSpan Duration,
        IReadOnlyList<ExpectedTrack> Tracks, IReadOnlyList<TimeSpan> Boundaries, IReadOnlyList<(TimeSpan Start, TimeSpan End)> ReencodedRanges,
        bool IsMerge);

    /// <summary>Weighted overall progress plus warnings, reported to the caller as they happen.</summary>
    private sealed class EditProgress
    {
        private readonly IProgress<ClipEditProgress>? _sink;
        private readonly WeightedProgress _weighted;
        private string _stage = "";
        private double? _last;

        public EditProgress(IProgress<ClipEditProgress>? sink, double totalWeight)
        {
            _sink = sink;
            _weighted = new WeightedProgress(totalWeight, (stage, fraction) =>
            {
                _last = fraction;
                sink?.Report(new ClipEditProgress(stage, fraction));
            });
        }

        public void Begin(string stage, double weight, bool determinate = true)
        {
            _stage = stage;
            _weighted.Begin(stage, weight, determinate);
        }

        public void Report(double local) => _weighted.Report(local);
        public void Complete(string stage) => _weighted.Complete(stage);
        public void Warn(string warning) => _sink?.Report(new ClipEditProgress(_stage, _last, warning));
    }
}
