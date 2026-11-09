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
    private const int MaxThumbnails = 20;
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
        _merges = new MergeEditor(tools, _process, _probe, commands);
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
        var range = probe.ResolveRange(start, end);
        return (await _cuts.PlanTrimAsync(probe, range.Start, range.End, mode, cancellationToken)).ToPublic();
    }

    public async Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        var split = await PlanSplitAsync(probe, position, cancellationToken);
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
        var range = probe.ResolveRange(start, end);
        var name = TitleOrDefault(title, source.DisplayName + " · trim");
        var tracks = probe.Mkv.Tracks.Select(track => track.Expected).ToArray();
        return await ExecuteAsync([source], progress, async (scratch, report) =>
        {
            async Task<RenderedOutput> RenderAsync(CutPlan plan)
            {
                var staged = Path.Combine(scratch, plan.IsLossless ? "trim.mkv" : "trim-precise.mkv");
                await _cuts.RenderTrimAsync(probe, plan, staged, scratch, local => report.Report(local), cancellationToken);
                return new RenderedOutput(name, staged, [new Segment(source, probe.Timeline.StartTime, plan.ActualStart, plan.ActualEnd, TimeSpan.Zero)],
                    plan.Duration, tracks, plan.Boundaries(probe.Path), [], IsMerge: false);
            }
            async Task<RenderedOutput> RenderFastAsync() =>
                await RenderAsync(await _cuts.PlanTrimAsync(probe, range.Start, range.End, ClipCutMode.Fast, cancellationToken));

            var plan = await _cuts.PlanTrimAsync(probe, range.Start, range.End, mode, cancellationToken);
            EnsureFreeSpace(scratch, probe, plan.Duration, plan.IsLossless ? 1.1 : 2.2);
            report.Begin(plan.IsLossless ? "Copying the trimmed range" : "Cutting at the exact frames", RenderWeight);
            if (plan.IsLossless) return [await RenderAsync(plan)];
            try { return [await RenderAsync(plan) with { Fallback = RenderFastAsync }]; }
            catch (InvalidDataException exception)
            {
                Trace.TraceWarning($"Frame-accurate trim of {probe.Path} failed; using keyframes. {exception}");
                report.Warn(RenderedOutput.FallbackWarning);
                return [await RenderFastAsync()];
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(source, cancellationToken);
        var split = await PlanSplitAsync(probe, position, cancellationToken);
        var name = TitleOrDefault(title, source.DisplayName);
        return await ExecuteAsync([source], progress, async (scratch, report) =>
        {
            EnsureFreeSpace(scratch, probe, probe.VideoEnd, 1.1);
            var first = Path.Combine(scratch, "part-1.mkv");
            var second = Path.Combine(scratch, "part-2.mkv");
            report.Begin("Splitting at a keyframe", RenderWeight);
            await _cuts.RenderSplitAsync(probe, split, first, second, scratch, local => report.Report(local), cancellationToken);
            var tracks = probe.Mkv.Tracks.Select(track => track.Expected).ToArray();
            var origin = probe.Timeline.StartTime;
            return [new RenderedOutput(name + " · part 1", first, [new Segment(source, origin, origin, split, TimeSpan.Zero)], split - origin, tracks, [], [], false),
                new RenderedOutput(name + " · part 2", second, [new Segment(source, origin, split, probe.VideoEnd, TimeSpan.Zero)], probe.VideoEnd - split, tracks, [], [], false)];
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
        IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var plan = await PlanMergeCoreAsync(sources, cancellationToken);
        var name = TitleOrDefault(title, "Merged home video");
        return await ExecuteAsync(sources, progress, async (scratch, report) =>
        {
            var staged = Path.Combine(scratch, "merge.mkv");
            var clips = plan.Clips.Select(clip => clip.Source).ToArray();
            ScratchFiles.EnsureFreeSpace(scratch, (long)(clips.Sum(clip => new FileInfo(clip.Path).Length) * (plan.ExpectAppend ? 1.1 : 2.2)));
            MergeOutput? output = null;
            if (plan.TryAppend)
            {
                report.Begin("Joining the clips without re-encoding", RenderWeight);
                output = await _merges.AppendAsync(clips, staged, local => report.Report(local), cancellationToken);
                if (output is null) Trace.TraceWarning("mkvmerge refused to append the merge sources; joining them as raw streams instead.");
            }
            if (output is null)
            {
                report.Begin(plan.Clips.Any(clip => clip.IsReencoded) ? "Re-encoding clips that do not match" : "Joining the clips", RenderWeight);
                output = await _merges.CompositeAsync(plan, staged, scratch, local => report.Report(local), cancellationToken);
                if (plan.CompositeNote != MergePlan.LosslessNote) report.Warn(plan.CompositeNote);
            }
            var offsets = new[] { TimeSpan.Zero }.Concat(output.Joins).ToArray();
            var segments = clips.Select((clip, index) =>
                new Segment(sources[index], clip.Timeline.StartTime, clip.Timeline.StartTime, clip.VideoEnd, offsets[index])).ToArray();
            var radius = MediaValidator.BoundaryRadius;
            var boundaries = output.Joins.Select((join, index) => new EditBoundary(join,
                [new DecodeWindow(clips[index].Path, clips[index].VideoEnd - radius, radius),
                    new DecodeWindow(clips[index + 1].Path, clips[index + 1].Timeline.StartTime, radius)])).ToArray();
            var tracks = output.IsAppend ? clips[0].Mkv.Tracks.Select(track => track.Expected).ToArray() : plan.CompositeTracks;
            var planned = output.IsAppend ? offsets[^1] + clips[^1].VideoEnd - clips[^1].Timeline.StartTime : output.Duration;
            return [new RenderedOutput(name, staged, segments, planned, tracks, boundaries, output.ReencodedRanges, IsMerge: true)];
        }, cancellationToken);
    }

    private async Task<TimeSpan> PlanSplitAsync(EditSource probe, TimeSpan position, CancellationToken cancellationToken)
    {
        probe.ResolveRange(TimeSpan.Zero, position);
        probe.ResolveRange(position, probe.VideoEnd);
        return await _cuts.PlanSplitAsync(probe, position, cancellationToken);
    }

    private async Task<MergePlan> PlanMergeCoreAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken)
    {
        if (sources.Count < 2) throw new ArgumentException("Select at least two videos to merge.", nameof(sources));
        var probes = new List<EditSource>();
        foreach (var source in sources) probes.Add(await ProbeAsync(source, cancellationToken));
        return MergePlanner.Plan(probes, await VideoEncoding.CanEncodeAsync(_tools, _process, "hevc"));
    }

    /// <summary>Renders into a private scratch directory, validates each output once (re-rendering a smart cut that
    /// fails as a keyframe trim), writes its metadata, then publishes every output in one catalog transaction, or none.</summary>
    private async Task<IReadOnlyList<LibraryVideo>> ExecuteAsync(IReadOnlyList<LibraryVideo> sources, IProgress<ClipEditProgress>? progress,
        Func<string, EditProgress, Task<IReadOnlyList<RenderedOutput>>> render, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scratch = Path.Combine(_catalog.Paths.ScratchDirectory, "edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var report = new EditProgress(progress, MetadataReadWeight + RenderWeight + FinishWeight + PublishWeight);
        try
        {
            report.Begin("Reading descriptions, chapters and thumbnails", MetadataReadWeight, determinate: false);
            var metadata = new Dictionary<string, VideoMetadata>();
            foreach (var source in sources.DistinctBy(source => source.RelativePath))
                metadata[source.RelativePath] = await ReadSourceMetadataAsync(source, report, cancellationToken);

            var prepared = new List<(LibraryVideo Video, string Staged)>();
            var outputs = await render(scratch, report);
            foreach (var rendered in outputs)
            {
                report.Begin("Checking the new video", FinishWeight / outputs.Count * 2 / 3, determinate: false);
                var output = await ValidateAsync(rendered, report, cancellationToken);
                report.Begin("Writing descriptions, chapters and thumbnails", FinishWeight / outputs.Count / 3, determinate: false);
                var content = MapMetadata(output, metadata);
                await _editor.WriteStagedAsync(output.StagedPath, content, cancellationToken: cancellationToken);
                var id = Guid.NewGuid();
                var timeline = await _probe.ReadTimelineAsync(output.StagedPath, cancellationToken);
                prepared.Add((new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), output.Title, timeline.VideoEnd - timeline.StartTime,
                    DateTimeOffset.UtcNow, content.RecordedAt ?? output.Segments[0].Source.RecordedAt)
                {
                    Title = content.Title,
                    Description = content.Description,
                }, output.StagedPath));
            }

            report.Begin("Publishing the new videos", PublishWeight, determinate: false);
            await LibraryPublisher.PublishAsync(_catalog, prepared, cancellationToken);
            report.Complete("Saved new videos; originals unchanged");
            return prepared.Select(item => item.Video).ToArray();
        }
        finally { ScratchFiles.TryDeleteDirectory(scratch); }
    }

    private async Task<RenderedOutput> ValidateAsync(RenderedOutput output, EditProgress report, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> warnings;
        try { warnings = await _validator.ValidateStructureAsync(output.StagedPath, output.Tracks, output.Duration, output.Boundaries, cancellationToken); }
        catch (InvalidDataException exception) when (output.Fallback is { } fallback)
        {
            Trace.TraceWarning($"{output.StagedPath} failed validation; rendering it with keyframe cuts instead. {exception}");
            ScratchFiles.TryDelete(output.StagedPath);
            output = await fallback();
            report.Warn(RenderedOutput.FallbackWarning);
            warnings = await _validator.ValidateStructureAsync(output.StagedPath, output.Tracks, output.Duration, output.Boundaries, cancellationToken);
        }
        foreach (var warning in warnings) report.Warn(warning);
        foreach (var (start, end) in output.ReencodedRanges)
        {
            var decoded = await _validator.ValidateDecodedRangeAsync(new DecodeWindow(output.StagedPath, start, end - start),
                DecodeTolerance.ForEdit(TimeSpan.FromSeconds(0.1)), cancellationToken);
            if (decoded.ErrorCount > 0) report.Warn($"{decoded.ErrorCount} decoder error(s) in a re-encoded part.");
        }
        return output;
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
    /// (keyframe-snapped) ranges. Each merged clip that lacks a chapter at its start gets one named after it, and
    /// thumbnails are taken from every clip in turn. The recorded date is written only when the first source states
    /// one (a reader's fallback such as the muxing date is not a recording date), and moves with a trim's start only
    /// when it includes a time of day.</summary>
    private static VideoMetadata MapMetadata(RenderedOutput output, IReadOnlyDictionary<string, VideoMetadata> metadata)
    {
        var chapters = new List<VideoChapter>();
        var thumbnailsBySegment = new List<Queue<VideoThumbnail>>();
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
                var position = segment.ToOutput(chapter.Position);
                if (position < next) chapters.Add(chapter with { Uid = UniqueUid(chapter.Uid), Position = position });
            }
            thumbnailsBySegment.Add(new Queue<VideoThumbnail>(source.Thumbnails.OrderBy(thumbnail => thumbnail.Position)
                .Where(thumbnail => thumbnail.Position >= segment.Start && thumbnail.Position < segment.End && segment.ToOutput(thumbnail.Position) < next)
                .Select(thumbnail => thumbnail with { Id = Guid.NewGuid(), Position = segment.ToOutput(thumbnail.Position) })));
        }
        var thumbnails = new List<VideoThumbnail>();
        while (thumbnails.Count < MaxThumbnails && thumbnailsBySegment.Any(queue => queue.Count > 0))
            foreach (var queue in thumbnailsBySegment.Where(queue => queue.Count > 0))
                if (thumbnails.Count < MaxThumbnails) thumbnails.Add(queue.Dequeue());

        var contents = output.Segments.Select(segment => metadata[segment.Source.RelativePath]).DistinctBy(content => content).ToArray();
        var firstSegment = output.Segments[0];
        var first = metadata[firstSegment.Source.RelativePath];
        var shift = firstSegment.Start - firstSegment.Origin;
        DateTimeOffset? recordedAt = null;
        var recordedAtText = "";
        if (first.RecordedAtText.Trim().Length > 0 && first.RecordedAt is { } recorded)
            (recordedAt, recordedAtText) = shift > TimeSpan.Zero && RecordedDate.HasTimeOfDay(first.RecordedAtText)
                ? (recorded + shift, "") : (recorded, first.RecordedAtText);
        return new VideoMetadata(output.Title,
            string.Join("\n", contents.Select(content => content.Description).Where(description => description.Length > 0)),
            MetadataText.NormalizeTags(contents.SelectMany(content => content.Tags)), recordedAt,
            chapters.OrderBy(chapter => chapter.Position).ToArray(), thumbnails.OrderBy(thumbnail => thumbnail.Position).ToArray())
        {
            RecordedAtText = recordedAtText,
        };
    }

    private async Task<EditSource> ProbeAsync(LibraryVideo source, CancellationToken cancellationToken) =>
        await _probe.ProbeAsync(_catalog.GetVideoPath(source.RelativePath), cancellationToken);

    private static void EnsureFreeSpace(string scratch, EditSource source, TimeSpan length, double factor)
    {
        var bytes = new FileInfo(source.Path).Length * Math.Min(1, length.Ticks / (double)Math.Max(1, source.VideoEnd.Ticks));
        ScratchFiles.EnsureFreeSpace(scratch, (long)(bytes * factor));
    }

    private static string TitleOrDefault(string title, string fallback) => string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();

    /// <summary>A source range [Start, End) placed at <paramref name="Offset"/> in the output. Times are absolute source
    /// timestamps; <paramref name="Origin"/> is where the source's timeline starts.</summary>
    private sealed record Segment(LibraryVideo Source, TimeSpan Origin, TimeSpan Start, TimeSpan End, TimeSpan Offset)
    {
        public TimeSpan ToOutput(TimeSpan sourceTime) => Offset + (sourceTime <= Start ? TimeSpan.Zero : sourceTime - Start);
    }

    /// <param name="Duration">The planned length of the output's video.</param>
    /// <param name="Fallback">Renders the same output with keyframe cuts when this one fails validation.</param>
    private sealed record RenderedOutput(string Title, string StagedPath, IReadOnlyList<Segment> Segments, TimeSpan Duration,
        IReadOnlyList<ExpectedTrack> Tracks, IReadOnlyList<EditBoundary> Boundaries, IReadOnlyList<(TimeSpan Start, TimeSpan End)> ReencodedRanges,
        bool IsMerge)
    {
        public const string FallbackWarning = "A frame-accurate cut was not possible; the cuts snap to keyframes instead.";
        public Func<Task<RenderedOutput>>? Fallback { get; init; }
    }

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
