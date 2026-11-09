using System.Globalization;
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
internal sealed record AudioTarget(MkvTrack Reference, IReadOnlyList<string> Encoder);

/// <summary>How a merge joins its clips: an mkvmerge append when every track matches, otherwise a composite of
/// raw video pieces (copied or re-encoded to the dominant clip's format) and audio pieces joined on the video's timeline.</summary>
internal sealed record MergePlan(bool Append, VideoTarget? Video, IReadOnlyList<AudioTarget> Audio, IReadOnlyList<MergeClip> Clips, string Note)
{
    public int ReencodedClips => Append ? 0 : Clips.Count(clip => clip.IsReencoded);
    public bool IsLossless => Append || ReencodedClips == 0;
    public ClipMergePlan ToPublic() => new(IsLossless, ReencodedClips, Note);
}

/// <param name="Joins">Output positions where each clip after the first starts.</param>
/// <param name="ReencodedRanges">Output ranges whose video was re-encoded.</param>
internal sealed record MergeOutput(IReadOnlyList<TimeSpan> Joins, TimeSpan Duration, IReadOnlyList<(TimeSpan Start, TimeSpan End)> ReencodedRanges);

internal static class MergePlanner
{
    /// <param name="forceComposite">Skip the append, because mkvmerge already refused it.</param>
    public static MergePlan Plan(IReadOnlyList<EditSource> sources, bool canEncodeHevc, bool forceComposite = false)
    {
        var first = sources[0].Mkv.Tracks;
        if (!forceComposite && sources.All(source => source.Mkv.Tracks.Select(track => track.AppendSignature).SequenceEqual(first.Select(track => track.AppendSignature))))
            return new MergePlan(true, null, [], sources.Select(source => new MergeClip(source, true, [])).ToArray(),
                "Lossless; the clips are joined without re-encoding.");

        var dominant = sources.Select((source, index) => (source, index)).GroupBy(item => VideoKey(item.source))
            .OrderByDescending(group => group.Sum(item => item.source.VideoEnd.Ticks)).ThenBy(group => group.Min(item => item.index))
            .First().Select(item => item.source).ToArray();
        var reference = dominant[0];
        var joinable = VideoEncoding.AnnexB(reference.Video.CodecName) is not null && reference.Video.IsProgressive &&
            reference.IsConstantFrameRate && reference.Mkv.Tracks.Count(track => track.Type == "video") == 1 &&
            (reference.Video.CodecName != "hevc" || canEncodeHevc);
        var codec = joinable ? reference.Video.CodecName : "h264";
        var frameRate = MediaProbe.TryParseRatio(reference.Video.AverageFrameRate, out var rate)
            ? reference.Video.AverageFrameRate : "30/1";
        var frameDuration = joinable ? reference.FrameDuration : TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / (rate > 0 ? rate : 30)));
        var sar = MediaProbe.TryParseRatio(reference.Video.SampleAspectRatio, out _) ? reference.Video.SampleAspectRatio : "1:1";
        var video = new VideoTarget(codec, Math.Max(2, reference.Video.Width / 2 * 2), Math.Max(2, reference.Video.Height / 2 * 2), sar, frameRate,
            VideoEncoding.EncodablePixelFormat(codec, reference.Video.PixelFormat), reference.Video, frameDuration);

        var audioReference = reference.AudioTracks.Count > 0 ? reference : sources.FirstOrDefault(source => source.AudioTracks.Count > 0);
        var audio = new List<AudioTarget>();
        var choices = sources.Select(_ => new List<AudioSource>()).ToArray();
        foreach (var (track, index) in (audioReference?.AudioTracks ?? []).Select((track, index) => (track, index)))
        {
            var matches = sources.Select(source => source.AudioTracks.Count > index && AudioSignature(source.AudioTracks[index]) == AudioSignature(track)).ToArray();
            var encoder = VideoEncoding.AudioEncoder(track);
            var canCopy = matches.All(match => match) || encoder is not null;
            audio.Add(new AudioTarget(track, encoder ?? ["aac", "-b:a", "192k"]));
            for (var clip = 0; clip < sources.Count; clip++)
                choices[clip].Add(canCopy && matches[clip] ? new AudioSource(AudioSourceKind.Copy, index)
                    : sources[clip].AudioTracks.Count > 0 ? new AudioSource(AudioSourceKind.Encode, Math.Min(index, sources[clip].AudioTracks.Count - 1))
                    : new AudioSource(AudioSourceKind.Silence, 0));
        }

        var clips = sources.Select((source, index) => new MergeClip(source, joinable && dominant.Contains(source), choices[index])).ToArray();
        var reencoded = clips.Count(clip => clip.IsReencoded);
        var notes = new List<string>
        {
            reencoded == 0 ? "Lossless; the clips are joined without re-encoding."
                : $"Re-encodes {reencoded} of {clips.Length} clips to match {video.Width}×{video.Height} at {FormatRate(frameRate)} fps; copies the rest.",
        };
        if (clips.Any(clip => clip.Audio.Any(choice => choice.Kind == AudioSourceKind.Silence))) notes.Add("Silent clips get silence.");
        if (sources.Any(source => source.Mkv.Tracks.Any(track => track.Type == "subtitles"))) notes.Add("Subtitles are not carried over.");
        return new MergePlan(false, video, audio, clips, string.Join(' ', notes));
    }

    private static string VideoKey(EditSource source) => string.Join('|', source.Video.CodecName, source.Video.Width, source.Video.Height,
        source.Video.PixelFormat, source.Video.SampleAspectRatio, source.VideoTrack.DefaultDurationNanoseconds, source.Video.FieldOrder);

    private static string AudioSignature(MkvTrack track) => string.Join('|', track.CodecId, track.CodecPrivate,
        track.SamplingFrequency.ToString(CultureInfo.InvariantCulture), track.Channels, track.BitsPerSample);

    private static string FormatRate(string rate) =>
        MediaProbe.TryParseRatio(rate, out var value) ? value.ToString("0.##", CultureInfo.InvariantCulture) : rate;
}

internal sealed class MergeEditor(MediaProbe probe, MediaCommands commands)
{
    private static readonly TimeSpan JoinSearch = TimeSpan.FromSeconds(0.5);

    /// <summary>Appends the clips with mkvmerge. Returns null, leaving no output, when mkvmerge judges the tracks
    /// incompatible after all.</summary>
    public async Task<MergeOutput?> AppendAsync(IReadOnlyList<EditSource> sources, string output, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--no-date", "-o", output };
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
        // mkvmerge starts each appended file after the longest track of the previous one; find the exact keyframe.
        var joins = new List<TimeSpan>();
        var estimate = TimeSpan.Zero;
        foreach (var source in sources.Take(sources.Count - 1))
        {
            estimate += source.Mkv.Duration > TimeSpan.Zero ? source.Mkv.Duration : source.VideoEnd;
            var packets = await probe.ReadPacketsAsync(output, estimate - JoinSearch, JoinSearch * 2, cancellationToken);
            var nearest = packets.Where(packet => packet.IsKey && (packet.Pts - estimate).Duration() <= JoinSearch)
                .OrderBy(packet => (packet.Pts - estimate).Duration()).Select(packet => (TimeSpan?)packet.Pts).FirstOrDefault();
            estimate = nearest ?? estimate;
            joins.Add(estimate);
        }
        var end = await probe.ReadVideoEndAsync(output, cancellationToken);
        return new MergeOutput(joins, end.End, []);
    }

    public async Task<MergeOutput> CompositeAsync(MergePlan plan, string output, string scratch, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var video = plan.Video!;
        var (bitstreamFilter, rawFormat) = VideoEncoding.AnnexB(video.CodecName)!.Value;
        var total = plan.Clips.Sum(clip => clip.Source.VideoEnd.Ticks) * 1.1;
        var done = 0d;
        var pieces = new List<string>();
        var lengths = new List<TimeSpan>();
        var reencoded = new List<(TimeSpan, TimeSpan)>();
        var position = TimeSpan.Zero;
        foreach (var (clip, index) in plan.Clips.Select((clip, index) => (clip, index)))
        {
            var length = clip.Source.VideoEnd;
            void Step(double local) => progress(Math.Clamp((done + length.Ticks * local) / total, 0, 1));
            var piece = Path.Combine(scratch, $"video-{index}.{rawFormat}");
            var frames = clip.CopyVideo
                ? await commands.FfmpegAsync(["-i", clip.Source.Path, "-map", "0:v:0", "-c", "copy", "-bsf:v", bitstreamFilter, "-f", rawFormat, piece],
                    length, Step, cancellationToken)
                : await commands.FfmpegAsync(["-i", clip.Source.Path, "-map", "0:v:0", "-an", "-sn", "-dn",
                    .. VideoEncoding.NormalizingVideoArguments(video), "-f", rawFormat, piece], length, Step, cancellationToken);
            if (frames <= 0) throw new InvalidDataException($"No video frames could be read from {Path.GetFileName(clip.Source.Path)}.");
            var duration = TimeSpan.FromTicks(video.FrameDuration.Ticks * frames);
            if (!clip.CopyVideo) reencoded.Add((position, position + duration));
            pieces.Add(piece);
            lengths.Add(duration);
            position += duration;
            done += length.Ticks;
        }

        string? audio = null;
        if (plan.Audio.Count > 0)
            audio = await JoinAudioAsync(plan, lengths, scratch, cancellationToken);
        progress(1 / 1.1);

        var arguments = new List<string> { "--no-date", "-o", output, "--language", "0:" + plan.Clips[0].Source.VideoTrack.Language,
            "--default-duration", "0:" + (video.FrameDuration.Ticks * 100).ToString(CultureInfo.InvariantCulture) + "ns", "(" };
        arguments.AddRange(pieces);
        arguments.Add(")");
        if (audio is not null)
        {
            foreach (var (target, index) in plan.Audio.Select((target, index) => (target, index)))
            {
                arguments.AddRange(["--language", $"{index}:{target.Reference.Language}"]);
                if (target.Reference.Name.Length > 0) arguments.AddRange(["--track-name", $"{index}:{target.Reference.Name}"]);
            }
            arguments.Add(audio);
        }
        await commands.MkvMergeAsync(arguments, null, cancellationToken);
        var joins = new List<TimeSpan>();
        var offset = TimeSpan.Zero;
        foreach (var length in lengths.Take(lengths.Count - 1)) joins.Add(offset += length);
        return new MergeOutput(joins, position, reencoded);
    }

    /// <summary>Builds one audio file per clip with the target layout, then concatenates them so each clip's audio
    /// starts exactly where its video does.</summary>
    private async Task<string> JoinAudioAsync(MergePlan plan, IReadOnlyList<TimeSpan> lengths, string scratch, CancellationToken cancellationToken)
    {
        var list = new List<string>();
        foreach (var (clip, index) in plan.Clips.Select((clip, index) => (clip, index)))
        {
            var piece = Path.Combine(scratch, $"audio-{index}.mka");
            var arguments = new List<string> { "-i", clip.Source.Path };
            var silenceInput = clip.Audio.Any(choice => choice.Kind == AudioSourceKind.Silence);
            if (silenceInput)
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
            arguments.AddRange(["-vn", "-sn", "-dn", "-f", "matroska", piece]);
            await commands.FfmpegAsync(arguments, lengths[index], null, cancellationToken);
            var seconds = MediaProbe.Seconds(lengths[index]);
            list.Add($"file '{Path.GetFileName(piece)}'");
            list.Add($"outpoint {seconds}");
            list.Add($"duration {seconds}");
        }
        var listPath = Path.Combine(scratch, "audio.txt");
        await File.WriteAllLinesAsync(listPath, list, cancellationToken);
        var joined = Path.Combine(scratch, "audio.mka");
        await commands.FfmpegAsync(["-f", "concat", "-safe", "0", "-i", listPath, "-map", "0:a", "-c", "copy", "-f", "matroska", joined],
            TimeSpan.Zero, null, cancellationToken);
        return joined;
    }

    /// <summary>Binds per-codec options such as <c>-b:a</c> to one output audio stream.</summary>
    private static IEnumerable<string> Retarget(IEnumerable<string> options, int track) =>
        options.Select(option => option.EndsWith(":a", StringComparison.Ordinal) ? $"{option}:{track}" : option);
}
