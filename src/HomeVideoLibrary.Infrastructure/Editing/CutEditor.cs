using System.Globalization;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Plans and renders trims and splits of one library MKV with mkvmerge, re-encoding only the frames a
/// frame-accurate cut cannot copy.</summary>
internal sealed class CutEditor(MediaTools tools, MediaProcess process, MediaProbe probe, MediaCommands commands)
{
    private static readonly TimeSpan SearchRadius = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WideSearch = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HalfMillisecond = TimeSpan.FromTicks(5_000);

    public async Task<CutPlan> PlanTrimAsync(EditSource source, TimeSpan start, TimeSpan end, ClipCutMode mode,
        CancellationToken cancellationToken)
    {
        var nearStart = await PacketsAroundAsync(source, start, cancellationToken);
        var nearEnd = end >= source.VideoEnd - CutPlanner.Epsilon ? [] : await PacketsAroundAsync(source, end, cancellationToken);
        string? reason = null;
        if (mode == ClipCutMode.Precise)
        {
            reason = await PreciseIneligibilityAsync(source);
            if (reason is null)
            {
                var precise = CutPlanner.PlanPreciseTrim(nearStart, nearEnd, start, end, source.VideoEnd);
                if (precise.IsLossless) return precise;
                reason = await OpenGopReasonAsync(source, precise, cancellationToken);
                if (reason is null) return precise;
            }
        }
        return CutPlanner.PlanFastTrim(nearStart, nearEnd, start, end, source.VideoEnd, reason);
    }

    public async Task<TimeSpan> PlanSplitAsync(EditSource source, TimeSpan position, CancellationToken cancellationToken) =>
        CutPlanner.PlanSplit(await PacketsAroundAsync(source, position, cancellationToken), position, source.VideoEnd)
            ?? throw new ArgumentOutOfRangeException(nameof(position), "There is no keyframe near that position to split at.");

    public async Task RenderTrimAsync(EditSource source, CutPlan plan, string output, string scratch, Action<double> progress,
        CancellationToken cancellationToken)
    {
        if (plan.IsLossless)
        {
            var arguments = new List<string> { "--no-date", "-o", output };
            if (plan.ActualStart > TimeSpan.Zero || plan.ActualEnd < source.VideoEnd - CutPlanner.Epsilon)
                arguments.AddRange(["--split", "parts:" + SplitTime(plan.ActualStart) + "-" + EndTime(source, plan.ActualEnd)]);
            arguments.AddRange(SourceOptions(source));
            arguments.Add(source.Path);
            await commands.MkvMergeAsync(arguments, progress, cancellationToken);
            return;
        }
        await RenderSmartCutAsync(source, plan, output, scratch, progress, cancellationToken);
    }

    /// <summary>Writes the two parts to <paramref name="first"/> and <paramref name="second"/>.</summary>
    public async Task RenderSplitAsync(EditSource source, TimeSpan position, string first, string second, string scratch,
        Action<double> progress, CancellationToken cancellationToken)
    {
        var pattern = Path.Combine(scratch, "split-%d.mkv");
        await commands.MkvMergeAsync(["--no-date", "-o", pattern, "--split", "timestamps:" + SplitTime(position), .. SourceOptions(source), source.Path],
            progress, cancellationToken);
        var parts = new[] { Path.Combine(scratch, "split-1.mkv"), Path.Combine(scratch, "split-2.mkv") };
        if (!parts.All(File.Exists) || File.Exists(Path.Combine(scratch, "split-3.mkv")))
            throw new InvalidDataException("The video could not be split into exactly two parts.");
        File.Move(parts[0], first);
        File.Move(parts[1], second);
    }

    /// <summary>Global tags, chapters and this app's attachments are rewritten after muxing, so they are left out;
    /// tracks, their tags and other attachments are kept.</summary>
    public static IEnumerable<string> SourceOptions(EditSource source)
    {
        yield return "--no-global-tags";
        yield return "--no-chapters";
        if (source.Mkv.AppAttachmentIds.Count == 0) yield break;
        yield return "--attachments";
        yield return "!" + string.Join(',', source.Mkv.AppAttachmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Re-encodes the frames next to each cut, copies the GOPs between them, and joins the pieces as raw
    /// (Annex-B) video so each piece carries its own parameter sets. Raw video has no timestamps; mkvmerge gets the
    /// source's own presentation times, so variable frame rates and dropped frames stay in sync.</summary>
    private async Task RenderSmartCutAsync(EditSource source, CutPlan plan, string output, string scratch, Action<double> progress,
        CancellationToken cancellationToken)
    {
        var (bitstreamFilter, rawFormat) = VideoEncoding.AnnexB(source.Video.CodecName)!.Value;
        var encode = VideoEncoding.MatchingVideoArguments(source.Video)!;
        // ffprobe stops at the first packet past the interval in decode order, so read a margin for reordered frames.
        var frames = (await probe.ReadPacketsAsync(source.Path, plan.ActualStart, plan.Duration + SearchRadius, cancellationToken))
            .Where(packet => packet.Pts >= plan.ActualStart - CutPlanner.Epsilon && packet.Pts < plan.ActualEnd - CutPlanner.Epsilon)
            .Select(packet => packet.Pts).ToArray();
        var total = (double)plan.Duration.Ticks;
        var done = 0d;
        void Step(TimeSpan length, double local) => progress(Math.Clamp((done + length.Ticks * local) / total * 0.8, 0, 0.8));

        var pieces = new List<string>();
        foreach (var (piece, index) in plan.Pieces.Select((piece, index) => (piece, index)))
        {
            var length = piece.End - piece.Start;
            var expected = frames.Count(pts => pts >= piece.Start - CutPlanner.Epsilon && pts < piece.End - CutPlanner.Epsilon);
            var path = Path.Combine(scratch, $"piece-{index}.{rawFormat}");
            long written;
            if (piece.Kind == PieceKind.Encode)
            {
                written = await commands.FfmpegAsync(["-ss", source.Timeline.Seek(piece.Start - HalfMillisecond), "-i", source.Path,
                    "-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", expected.ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough",
                    .. encode, "-f", rawFormat, path], length, local => Step(length, local), cancellationToken);
            }
            else
            {
                var copy = Path.Combine(scratch, $"piece-{index}.mkv");
                await commands.MkvMergeAsync(["--no-date", "-o", copy,
                    "--split", "parts:" + SplitTime(piece.Start) + "-" + EndTime(source, piece.End),
                    "-A", "-S", "-B", "-M", "-T", "--no-chapters", "--no-global-tags", "-d", source.VideoTrack.Id.ToString(CultureInfo.InvariantCulture), source.Path],
                    local => Step(length, local / 2), cancellationToken);
                written = await commands.FfmpegAsync(["-i", copy, "-map", "0:v:0", "-c", "copy", "-bsf:v", bitstreamFilter, "-f", rawFormat, path],
                    length, local => Step(length, 0.5 + local / 2), cancellationToken);
                File.Delete(copy);
            }
            if (written != expected)
                throw new InvalidDataException($"A cut piece has {written} frames; the source has {expected} there.");
            pieces.Add(path);
            done += length.Ticks;
        }

        var timestamps = Path.Combine(scratch, "timestamps.txt");
        await TimestampFile.WriteAsync(timestamps, frames.Select(pts => pts - plan.ActualStart), cancellationToken);
        var others = Path.Combine(scratch, "other-tracks.mkv");
        var hasOthers = source.Mkv.Tracks.Any(track => track.Type != "video") || source.Mkv.ForeignAttachments.Count > 0;
        if (hasOthers)
            await commands.MkvMergeAsync(["--no-date", "-o", others,
                "--split", "parts:" + MediaProbe.Seconds(plan.ActualStart) + "s-" + MediaProbe.Seconds(plan.ActualEnd) + "s",
                "-D", .. SourceOptions(source), source.Path], null, cancellationToken);

        await commands.MkvMergeAsync(["--no-date", "-o", output, .. source.VideoTrack.HeaderArguments(0), "--timestamps", "0:" + timestamps,
            "(", .. pieces, ")", .. hasOthers ? [others] : Array.Empty<string>()], local => progress(0.8 + local * 0.2), cancellationToken);
        if (source.VideoTrack.DefaultDurationNanoseconds > 0)
            await commands.SetVideoFrameDurationAsync(output, source.FrameDuration, cancellationToken);
    }

    private async Task<IReadOnlyList<VideoPacket>> PacketsAroundAsync(EditSource source, TimeSpan time, CancellationToken cancellationToken)
    {
        var packets = await probe.ReadPacketsAsync(source.Path, time - SearchRadius, SearchRadius * 2, cancellationToken);
        if (CutPlanner.KeyAtOrAfter(packets, time) is null && time + SearchRadius < source.VideoEnd)
            packets = await probe.ReadPacketsAsync(source.Path, time - SearchRadius, WideSearch, cancellationToken);
        if (CutPlanner.KeyAtOrAfter(packets, time) is null && time + WideSearch < source.VideoEnd)
            packets = await probe.ReadPacketsAsync(source.Path, time - SearchRadius, null, cancellationToken);
        if (CutPlanner.KeyAtOrBefore(packets, time) is null && time > SearchRadius)
            packets = [.. await probe.ReadPacketsAsync(source.Path, time - WideSearch, WideSearch, cancellationToken), .. packets];
        return packets.DistinctBy(packet => packet.Pts).OrderBy(packet => packet.Pts).ToArray();
    }

    /// <summary>Why a frame-accurate cut is unsafe for this source, judged from its stream parameters, or null.</summary>
    private async Task<string?> PreciseIneligibilityAsync(EditSource source)
    {
        const string prefix = "Frame-accurate cutting is unavailable for this video";
        if (VideoEncoding.AnnexB(source.Video.CodecName) is null) return $"{prefix} ({source.Video.CodecName} video).";
        if (source.Mkv.Tracks.Count(track => track.Type == "video") != 1) return $"{prefix} (more than one video track).";
        if (!source.Video.IsProgressive) return $"{prefix} (interlaced or unknown field order).";
        if (VideoEncoding.MatchingVideoArguments(source.Video) is null) return $"{prefix} ({source.Video.Profile} {source.Video.PixelFormat}).";
        if (!await VideoEncoding.CanEncodeAsync(tools, process, source.Video.CodecName)) return $"{prefix} (no {source.Video.CodecName} encoder).";
        return null;
    }

    /// <summary>Copied pieces must start on, and end before, IDR frames: an open-GOP keyframe's leading pictures
    /// reference frames that the cut removes.</summary>
    private async Task<string?> OpenGopReasonAsync(EditSource source, CutPlan plan, CancellationToken cancellationToken)
    {
        var lengthSize = NalUnits.LengthSize(source.VideoTrack.CodecPrivate, source.Video.CodecName);
        var keyframes = plan.Pieces.Where(piece => piece.Kind == PieceKind.Copy)
            .SelectMany(piece => new[] { piece.Start, piece.End }).Where(time => time < source.VideoEnd - CutPlanner.Epsilon).Distinct();
        foreach (var keyframe in keyframes)
        {
            var packet = await probe.ReadPacketAsync(source.Path, keyframe, cancellationToken);
            if (packet is not { } read || (read.Pts - keyframe).Duration() > CutPlanner.Epsilon ||
                !NalUnits.StartsWithIdr(read.Data, lengthSize, source.Video.CodecName))
                return "Frame-accurate cutting is unavailable for this video (open-GOP keyframes).";
        }
        return null;
    }

    /// <summary>mkvmerge splits at the first keyframe at or after the given time; one millisecond early selects
    /// exactly the keyframe at <paramref name="keyframe"/> despite timestamp rounding.</summary>
    private static string SplitTime(TimeSpan keyframe) =>
        MediaProbe.Seconds(keyframe > CutPlanner.Epsilon ? keyframe - CutPlanner.Epsilon : TimeSpan.Zero) + "s";

    private static string EndTime(EditSource source, TimeSpan end) =>
        end >= source.VideoEnd - CutPlanner.Epsilon ? "" : SplitTime(end);
}
