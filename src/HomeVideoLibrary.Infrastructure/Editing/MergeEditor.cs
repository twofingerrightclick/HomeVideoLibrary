using System.Globalization;
using System.Text.Json;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure.Editing;

internal enum AudioSourceKind { Copy, Encode, Silence }

/// <param name="TrackIndex">Index among the clip's audio tracks; unused for silence.</param>
internal sealed record AudioSource(AudioSourceKind Kind, int TrackIndex);

internal sealed record MergeClip(EditSource Source, bool CopyVideo, IReadOnlyList<AudioSource> Audio)
{
    public bool IsReencoded => !CopyVideo || Audio.Any(audio => audio.Kind == AudioSourceKind.Encode);
}

/// <param name="Encoder">ffmpeg encoder and options for clips that cannot copy this track.</param>
internal sealed record AudioTarget(MkvTrack Reference, IReadOnlyList<string> Encoder, string ExpectedCodec);

/// <summary>How a merge joins its clips. mkvmerge's append is tried whenever the track formats agree (it judges codec
/// configuration itself); otherwise, or when it refuses, the composite joins raw video pieces (copied, or re-encoded to
/// the dominant clip's format) with the other tracks laid out on the video's timeline.</summary>
/// <param name="TryAppend">The tracks agree in codec, size and sample format, and no clip's audio runs past its
/// video (which would leave a hole in the picture where the next clip starts).</param>
/// <param name="ExpectAppend">mkvmerge is expected to accept the append: codec configurations are identical too.</param>
/// <param name="Subtitles">Subtitle tracks carried by the composite; empty when the clips' subtitle layouts differ.</param>
internal sealed record MergePlan(bool TryAppend, bool ExpectAppend, VideoTarget Video, bool CanJoinRaw, IReadOnlyList<AudioTarget> Audio,
    IReadOnlyList<MkvTrack> Subtitles, IReadOnlyList<MergeClip> Clips, string CompositeNote)
{
    public const string LosslessNote = "Lossless; the clips are joined without re-encoding.";

    public int ReencodedClips => ExpectAppend ? 0 : Clips.Count(clip => clip.IsReencoded);
    public bool IsLossless => ReencodedClips == 0;
    public string Note => ExpectAppend ? LosslessNote : CompositeNote;
    public ClipMergePlan ToPublic() => new(IsLossless, ReencodedClips, Note)
    {
        Clips = Clips.Select(clip =>
        {
            var length = clip.Source.VideoEnd - clip.Source.Timeline.StartTime;
            if (ExpectAppend || !clip.IsReencoded) return new ClipMergeSourcePlan(false, false, length, null);
            var encodesAudio = clip.Audio.Any(audio => audio.Kind == AudioSourceKind.Encode);
            var reasons = new List<string>();
            if (!clip.CopyVideo) reasons.Add(VideoReason(clip.Source));
            if (encodesAudio) reasons.Add("its sound differs from the other clips' and is converted to match");
            return new ClipMergeSourcePlan(!clip.CopyVideo, encodesAudio, length, string.Join(" and ", reasons));
        }).ToArray(),
    };

    /// <summary>Why <paramref name="source"/>'s picture cannot be copied: the first way it differs from the clips that
    /// set the merged format, or why nothing can be copied.</summary>
    private string VideoReason(EditSource source)
    {
        var target = Video.Colour;
        var video = source.Video;
        if (!CanJoinRaw)
        {
            if (VideoEncoding.AnnexB(target.CodecName) is null) return $"{VideoEncoding.FriendlyCodec(target.CodecName)} video can't be joined without re-encoding";
            if (Clips.Any(clip => clip.Source.Mkv.Tracks.Count(track => track.Type == "video") != 1)) return "a clip has more than one video track";
            return "joining HEVC video needs an HEVC encoder, and this ffmpeg has none";
        }
        if (video.CodecName != target.CodecName)
            return $"it is {VideoEncoding.FriendlyCodec(video.CodecName)} video and the merged video is {VideoEncoding.FriendlyCodec(target.CodecName)}";
        if (video.Width != target.Width || video.Height != target.Height)
            return $"it is {video.Width}×{video.Height} and the merged video is {target.Width}×{target.Height}";
        if (video.SampleAspectRatio != target.SampleAspectRatio)
            return $"its pixel shape ({video.SampleAspectRatio}) differs from the merged video's ({target.SampleAspectRatio})";
        if (video.FieldOrder != target.FieldOrder)
            return $"its interlacing ({video.FieldOrder}) differs from the merged video's ({target.FieldOrder})";
        return $"its colour format ({video.PixelFormat}) differs from the merged video's ({target.PixelFormat})";
    }

    public IReadOnlyList<ExpectedTrack> CompositeTracks =>
    [
        new ExpectedTrack("video", Clips.FirstOrDefault(clip => clip.CopyVideo)?.Source.VideoTrack.Codec
            ?? (Video.CodecName == "hevc" ? "HEVC/H.265/MPEG-H" : "AVC/H.264/MPEG-4p10"), Clips[0].Source.VideoTrack.Language),
        .. Audio.Select(target => new ExpectedTrack("audio", target.ExpectedCodec, target.Reference.Language)),
        .. Subtitles.Select(track => track.Expected),
    ];
}

/// <param name="Joins">Output positions where each clip after the first starts.</param>
/// <param name="ReencodedRanges">Output ranges whose video was re-encoded.</param>
/// <param name="IsAppend">mkvmerge appended the files, keeping every track of the first clip's layout.</param>
internal sealed record MergeOutput(IReadOnlyList<TimeSpan> Joins, TimeSpan Duration, IReadOnlyList<(TimeSpan Start, TimeSpan End)> ReencodedRanges,
    bool IsAppend);

internal static class MergePlanner
{
    /// <summary>Audio may outlast its video by about an AAC frame or two without visibly delaying the next clip.</summary>
    private static readonly TimeSpan AudioOverhang = TimeSpan.FromSeconds(0.25);

    public static MergePlan Plan(IReadOnlyList<EditSource> sources, bool canEncodeHevc)
    {
        static IEnumerable<string> Formats(EditSource source) => source.Mkv.Tracks.Select(track => track.FormatSignature);
        static IEnumerable<string> Configurations(EditSource source) => source.Mkv.Tracks.Select(track => track.AppendSignature);
        var tryAppend = sources.All(source => Formats(source).SequenceEqual(Formats(sources[0]))) &&
            sources.SkipLast(1).All(source => source.Timeline.AudioEnd is not { } audioEnd || audioEnd <= source.VideoEnd + AudioOverhang);
        var expectAppend = tryAppend && sources.All(source => Configurations(source).SequenceEqual(Configurations(sources[0])));

        var dominant = sources.Select((source, index) => (source, index)).GroupBy(item => VideoKey(item.source))
            .OrderByDescending(group => group.Sum(item => (item.source.VideoEnd - item.source.Timeline.StartTime).Ticks))
            .ThenBy(group => group.Min(item => item.index))
            .First().Select(item => item.source).ToArray();
        var reference = dominant[0];
        // A raw join only concatenates bitstreams; field order and frame timing do not matter because every frame keeps
        // its own timestamp.
        var canJoinRaw = VideoEncoding.AnnexB(reference.Video.CodecName) is not null &&
            sources.All(source => source.Mkv.Tracks.Count(track => track.Type == "video") == 1) &&
            (reference.Video.CodecName != "hevc" || canEncodeHevc);
        var codec = canJoinRaw ? reference.Video.CodecName : "h264";
        var frameRate = MediaProbe.TryParseRatio(reference.Video.AverageFrameRate, out var rate) ? reference.Video.AverageFrameRate : "30/1";
        var sar = MediaProbe.TryParseRatio(reference.Video.SampleAspectRatio, out _) ? reference.Video.SampleAspectRatio : "1:1";
        var video = new VideoTarget(codec, Math.Max(2, reference.Video.Width / 2 * 2), Math.Max(2, reference.Video.Height / 2 * 2), sar, frameRate,
            VideoEncoding.EncodablePixelFormat(codec, reference.Video.PixelFormat), reference.Video,
            TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / (rate > 0 ? rate : 30))));

        var audioReference = reference.AudioTracks.Count > 0 ? reference : sources.FirstOrDefault(source => source.AudioTracks.Count > 0);
        var audio = new List<AudioTarget>();
        var choices = sources.Select(_ => new List<AudioSource>()).ToArray();
        foreach (var (track, index) in (audioReference?.AudioTracks ?? []).Select((track, index) => (track, index)))
        {
            var matches = sources.Select(source => source.AudioTracks.Count > index &&
                source.AudioTracks[index].AppendSignature == track.AppendSignature).ToArray();
            var encoder = VideoEncoding.AudioEncoder(track);
            var canCopy = matches.All(match => match) || encoder is not null;
            audio.Add(new AudioTarget(track, encoder ?? ["aac", "-b:a", "192k"], canCopy ? track.Codec : "AAC"));
            for (var clip = 0; clip < sources.Count; clip++)
                choices[clip].Add(canCopy && matches[clip] ? new AudioSource(AudioSourceKind.Copy, index)
                    : sources[clip].AudioTracks.Count > 0 ? new AudioSource(AudioSourceKind.Encode, Math.Min(index, sources[clip].AudioTracks.Count - 1))
                    : new AudioSource(AudioSourceKind.Silence, 0));
        }

        static IEnumerable<string> SubtitleLayout(EditSource source) =>
            source.Mkv.Tracks.Where(track => track.Type == "subtitles").Select(track => track.CodecId);
        var subtitlesAgree = sources.All(source => SubtitleLayout(source).SequenceEqual(SubtitleLayout(sources[0])));
        var subtitles = subtitlesAgree ? sources[0].Mkv.Tracks.Where(track => track.Type == "subtitles").ToArray() : [];

        var clips = sources.Select((source, index) => new MergeClip(source, canJoinRaw && dominant.Contains(source), choices[index])).ToArray();
        var reencoded = clips.Count(clip => clip.IsReencoded);
        var notes = new List<string>
        {
            reencoded == 0 ? MergePlan.LosslessNote
                : $"Re-encodes {reencoded} of {clips.Length} clips to match {video.Width}×{video.Height} at {FormatRate(frameRate)} fps; copies the rest.",
        };
        if (clips.Any(clip => clip.Audio.Any(choice => choice.Kind == AudioSourceKind.Silence))) notes.Add("Silent clips get silence.");
        if (!subtitlesAgree) notes.Add("Subtitles are not carried over because the clips' subtitle tracks differ.");
        return new MergePlan(tryAppend, expectAppend, video, canJoinRaw, audio, subtitles, clips, string.Join(' ', notes));
    }

    private static string VideoKey(EditSource source) => string.Join('|', source.Video.CodecName, source.Video.Width, source.Video.Height,
        source.Video.PixelFormat, source.Video.SampleAspectRatio, source.Video.FieldOrder);

    private static string FormatRate(string rate) =>
        MediaProbe.TryParseRatio(rate, out var value) ? value.ToString("0.##", CultureInfo.InvariantCulture) : rate;
}

internal sealed class MergeEditor(MediaTools tools, MediaProcess process, MediaProbe probe, MediaCommands commands)
{
    /// <summary>Appends the clips with mkvmerge. Returns null, leaving no output, when mkvmerge judges the tracks
    /// incompatible. mkvmerge marks where each appended file starts with a generated chapter; those positions are
    /// the joins (the chapters themselves are replaced when the metadata is written).</summary>
    public async Task<MergeOutput?> AppendAsync(IReadOnlyList<EditSource> sources, string output, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--no-date", "-o", output, "--generate-chapters", "when-appending" };
        foreach (var (source, index) in sources.Select((source, index) => (source, index)))
        {
            if (index > 0) arguments.Add("+");
            arguments.AddRange(CutEditor.SourceOptions(source));
            arguments.Add(source.Path);
        }
        var warnings = await commands.MkvMergeAsync(arguments, progress, cancellationToken);
        if (MediaCommands.RefusesToAppend(warnings))
        {
            ScratchFiles.TryDelete(output);
            return null;
        }
        var chapters = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_chapters", "-of", "json", output], TimeSpan.FromMinutes(1), cancellationToken);
        using var json = JsonDocument.Parse(chapters.StandardOutput);
        var starts = json.RootElement.TryGetProperty("chapters", out var list)
            ? list.EnumerateArray().Select(chapter => TimeSpan.FromSeconds(double.Parse(chapter.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture)))
                .Order().ToArray()
            : [];
        if (starts.Length != sources.Count)
            throw new InvalidDataException($"mkvmerge reported {starts.Length} clip starts for {sources.Count} clips.");
        var timeline = await probe.ReadTimelineAsync(output, cancellationToken);
        return new MergeOutput(starts.Skip(1).ToArray(), timeline.VideoEnd, [], IsAppend: true);
    }

    public async Task<MergeOutput> CompositeAsync(MergePlan plan, string output, string scratch, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var video = plan.Video;
        var (bitstreamFilter, rawFormat) = VideoEncoding.AnnexB(video.CodecName)!.Value;
        var total = plan.Clips.Sum(clip => (clip.Source.VideoEnd - clip.Source.Timeline.StartTime).Ticks) * 1.1;
        var done = 0d;
        var pieces = new List<string>();
        var lengths = new List<TimeSpan>();
        var timestamps = new List<TimeSpan>();
        var reencoded = new List<(TimeSpan, TimeSpan)>();
        var position = TimeSpan.Zero;
        foreach (var (clip, index) in plan.Clips.Select((clip, index) => (clip, index)))
        {
            var source = clip.Source;
            var span = source.VideoEnd - source.Timeline.StartTime;
            void Step(double local) => progress(Math.Clamp((done + span.Ticks * local) / total, 0, 1));
            var piece = Path.Combine(scratch, $"video-{index}.{rawFormat}");
            TimeSpan length;
            if (clip.CopyVideo)
            {
                var frames = (await probe.ReadPacketsAsync(source.Path, source.Timeline.StartTime, null, cancellationToken)).Select(packet => packet.Pts).ToArray();
                var written = await commands.FfmpegAsync(["-i", source.Path, "-map", "0:v:0", "-c", "copy", "-bsf:v", bitstreamFilter, "-f", rawFormat, piece],
                    span, Step, cancellationToken);
                if (written != frames.Length)
                    throw new InvalidDataException($"{Path.GetFileName(source.Path)} has {frames.Length} frames but {written} were copied.");
                timestamps.AddRange(frames.Select(pts => position + pts - source.Timeline.StartTime));
                length = span;
            }
            else
            {
                var written = await commands.FfmpegAsync(["-i", source.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
                    .. VideoEncoding.NormalizingVideoArguments(video), "-f", rawFormat, piece], span, Step, cancellationToken);
                if (written <= 0) throw new InvalidDataException($"No video frames could be read from {Path.GetFileName(source.Path)}.");
                timestamps.AddRange(Enumerable.Range(0, (int)written).Select(frame => position + video.FrameDuration * frame));
                length = video.FrameDuration * written;
                reencoded.Add((position, position + length));
            }
            pieces.Add(piece);
            lengths.Add(length);
            position += length;
            done += span.Ticks;
        }

        var timestampFile = Path.Combine(scratch, "timestamps.txt");
        await TimestampFile.WriteAsync(timestampFile, timestamps, cancellationToken);
        var others = plan.Audio.Count + plan.Subtitles.Count > 0 ? await JoinOtherTracksAsync(plan, lengths, scratch, cancellationToken) : null;
        progress(1 / 1.1);

        var arguments = new List<string> { "--no-date", "-o", output };
        // A copied clip's header (flags, display size, colour) describes the joined video; re-encoded video keeps only the language.
        var copied = plan.Clips.FirstOrDefault(clip => clip.CopyVideo)?.Source;
        arguments.AddRange(copied?.VideoTrack.HeaderArguments(0) ?? ["--language", "0:" + plan.Clips[0].Source.VideoTrack.Language]);
        arguments.AddRange(["--timestamps", "0:" + timestampFile, "(", .. pieces, ")"]);
        if (others is not null)
        {
            var tracks = plan.Audio.Select(target => target.Reference).Concat(plan.Subtitles).ToArray();
            foreach (var (track, index) in tracks.Select((track, index) => (track, index)))
                arguments.AddRange(track.HeaderArguments(index));
            arguments.Add(others);
        }
        arguments.AddRange(ForeignAttachmentInputs(plan.Clips.Select(clip => clip.Source)));
        await commands.MkvMergeAsync(arguments, null, cancellationToken);
        await commands.SetVideoFrameDurationAsync(output, copied?.FrameDuration ?? video.FrameDuration, cancellationToken);
        var joins = new List<TimeSpan>();
        var offset = TimeSpan.Zero;
        foreach (var length in lengths.Take(lengths.Count - 1)) joins.Add(offset += length);
        return new MergeOutput(joins, position, reencoded, IsAppend: false);
    }

    /// <summary>Each clip's foreign attachments, once per file name, added as attachment-only inputs.</summary>
    private static IEnumerable<string> ForeignAttachmentInputs(IEnumerable<EditSource> sources)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var ids = source.Mkv.ForeignAttachments.Where(attachment => names.Add(attachment.FileName)).Select(attachment => attachment.Id).ToArray();
            if (ids.Length == 0) continue;
            foreach (var argument in new[] { "-D", "-A", "-S", "-B", "-T", "--no-chapters", "--no-global-tags", "--attachments",
                         string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture))), source.Path })
                yield return argument;
        }
    }

    /// <summary>Builds one file per clip with the target audio and subtitle layout, then concatenates them so each
    /// clip's tracks start exactly where its video does and stop where its video stops.</summary>
    private async Task<string> JoinOtherTracksAsync(MergePlan plan, IReadOnlyList<TimeSpan> lengths, string scratch, CancellationToken cancellationToken)
    {
        var list = new List<string>();
        foreach (var (clip, index) in plan.Clips.Select((clip, index) => (clip, index)))
        {
            var piece = Path.Combine(scratch, $"tracks-{index}.mka");
            var arguments = new List<string> { "-i", clip.Source.Path };
            if (clip.Audio.Any(choice => choice.Kind == AudioSourceKind.Silence))
                arguments.AddRange(["-f", "lavfi", "-t", MediaProbe.Seconds(lengths[index]), "-i", "anullsrc=r=48000:cl=stereo"]);
            foreach (var (choice, track) in clip.Audio.Select((choice, track) => (choice, track)))
            {
                var target = plan.Audio[track];
                arguments.AddRange(["-map", choice.Kind == AudioSourceKind.Silence ? "1:a:0" : $"0:a:{choice.TrackIndex}"]);
                if (choice.Kind == AudioSourceKind.Copy)
                    arguments.AddRange([$"-c:a:{track}", "copy"]);
                else
                {
                    arguments.AddRange([$"-c:a:{track}", target.Encoder[0], .. Retarget(target.Encoder.Skip(1), track)]);
                    if (target.Reference.SamplingFrequency > 0)
                        arguments.AddRange([$"-ar:a:{track}", target.Reference.SamplingFrequency.ToString(CultureInfo.InvariantCulture)]);
                    if (target.Reference.Channels > 0)
                        arguments.AddRange([$"-ac:a:{track}", target.Reference.Channels.ToString(CultureInfo.InvariantCulture)]);
                }
            }
            for (var subtitle = 0; subtitle < plan.Subtitles.Count; subtitle++)
                arguments.AddRange(["-map", $"0:s:{subtitle}", $"-c:s:{subtitle}", "copy"]);
            arguments.AddRange(["-vn", "-dn", "-f", "matroska", piece]);
            await commands.FfmpegAsync(arguments, lengths[index], null, cancellationToken);
            var seconds = MediaProbe.Seconds(lengths[index]);
            list.Add($"file '{Path.GetFileName(piece)}'");
            list.Add($"outpoint {seconds}");
            list.Add($"duration {seconds}");
        }
        var listPath = Path.Combine(scratch, "tracks.txt");
        await File.WriteAllLinesAsync(listPath, list, cancellationToken);
        var joined = Path.Combine(scratch, "tracks.mka");
        await commands.FfmpegAsync(["-f", "concat", "-safe", "0", "-i", listPath, "-map", "0", "-c", "copy", "-f", "matroska", joined],
            TimeSpan.Zero, null, cancellationToken);
        return joined;
    }

    /// <summary>Binds per-codec options such as <c>-b:a</c> to one output audio stream.</summary>
    private static IEnumerable<string> Retarget(IEnumerable<string> options, int track) =>
        options.Select(option => option.EndsWith(":a", StringComparison.Ordinal) ? $"{option}:{track}" : option);
}
