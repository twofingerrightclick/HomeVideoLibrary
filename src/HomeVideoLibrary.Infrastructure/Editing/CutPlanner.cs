using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure.Editing;

internal enum PieceKind { Copy, Encode }

/// <summary>A run of source frames [Start, End), either stream-copied or re-encoded.</summary>
internal sealed record CutPiece(PieceKind Kind, TimeSpan Start, TimeSpan End);

/// <summary>What a cut will really produce, in source time. Copy pieces always start on a keyframe.</summary>
internal sealed record CutPlan(TimeSpan ActualStart, TimeSpan ActualEnd, IReadOnlyList<CutPiece> Pieces, string Note)
{
    public bool IsLossless => Pieces.All(piece => piece.Kind == PieceKind.Copy);
    public TimeSpan Duration => ActualEnd - ActualStart;
    public TimeSpan EncodedDuration =>
        TimeSpan.FromTicks(Pieces.Where(piece => piece.Kind == PieceKind.Encode).Sum(piece => (piece.End - piece.Start).Ticks));

    /// <summary>Output positions where re-encoded and copied pieces meet, with the source span each one replaces.</summary>
    public IReadOnlyList<EditBoundary> Boundaries(string sourcePath) => Pieces.Skip(1).Select(piece => new EditBoundary(
        piece.Start - ActualStart,
        [new DecodeWindow(sourcePath, piece.Start - MediaValidator.BoundaryRadius, MediaValidator.BoundaryRadius * 2)])).ToArray();

    public ClipCutPlan ToPublic() => new(ActualStart, ActualEnd, IsLossless, Note) { ReencodedDuration = EncodedDuration };
}

/// <summary>Pure cut arithmetic over video packet lists. Times within <see cref="Epsilon"/> are equal, because Matroska
/// stores millisecond timestamps.</summary>
internal static class CutPlanner
{
    public static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(1);

    public static TimeSpan? KeyAtOrBefore(IReadOnlyList<VideoPacket> packets, TimeSpan time) =>
        packets.Where(packet => packet.IsKey && packet.Pts <= time + Epsilon).Select(packet => (TimeSpan?)packet.Pts).LastOrDefault();

    public static TimeSpan? KeyAtOrAfter(IReadOnlyList<VideoPacket> packets, TimeSpan time) =>
        packets.Where(packet => packet.IsKey && packet.Pts >= time - Epsilon).Select(packet => (TimeSpan?)packet.Pts).FirstOrDefault();

    /// <summary>Lossless trim: from the keyframe at or before <paramref name="start"/> to the keyframe at or after
    /// <paramref name="end"/>, so everything requested is kept. A missing next keyframe means the end of the video.</summary>
    public static CutPlan PlanFastTrim(IReadOnlyList<VideoPacket> nearStart, IReadOnlyList<VideoPacket> nearEnd,
        TimeSpan start, TimeSpan end, TimeSpan videoEnd, string? fallbackReason = null)
    {
        var actualStart = KeyAtOrBefore(nearStart, start) ?? TimeSpan.Zero;
        var actualEnd = end >= videoEnd - Epsilon ? videoEnd : KeyAtOrAfter(nearEnd, end) ?? videoEnd;
        var snapped = (actualStart - start).Duration() > Epsilon || (actualEnd - end).Duration() > Epsilon;
        var note = (fallbackReason is null ? "" : fallbackReason + " ") +
            (snapped ? $"Lossless copy; cuts snap to keyframes at {Timecode.Format(actualStart)} and {Timecode.Format(actualEnd)}."
                : "Lossless copy; both cuts fall on keyframes.");
        return new CutPlan(actualStart, actualEnd, [new CutPiece(PieceKind.Copy, actualStart, actualEnd)], note);
    }

    /// <summary>Frame-accurate trim: re-encode from the first frame at or after <paramref name="start"/> up to the next
    /// keyframe, copy whole GOPs, and re-encode from the last keyframe before <paramref name="end"/>. The cut keeps the
    /// frames presented in [start, end), so it ends where the first excluded frame begins.</summary>
    public static CutPlan PlanPreciseTrim(IReadOnlyList<VideoPacket> nearStart, IReadOnlyList<VideoPacket> nearEnd,
        TimeSpan start, TimeSpan end, TimeSpan videoEnd)
    {
        var firstFrame = nearStart.Where(packet => packet.Pts >= start - Epsilon).Select(packet => (TimeSpan?)packet.Pts).FirstOrDefault()
            ?? throw new InvalidDataException("No video frame was found at the trim start.");
        var copyStart = KeyAtOrAfter(nearStart, start) ?? videoEnd;
        TimeSpan copyEnd, actualEnd;
        if (end >= videoEnd - Epsilon)
            copyEnd = actualEnd = videoEnd;
        else
        {
            copyEnd = KeyAtOrBefore(nearEnd, end) ?? TimeSpan.Zero;
            actualEnd = nearEnd.Where(packet => packet.Pts >= end - Epsilon).Select(packet => (TimeSpan?)packet.Pts).FirstOrDefault() ?? videoEnd;
        }
        var pieces = new List<CutPiece>();
        if (copyStart >= copyEnd)
            pieces.Add(new CutPiece(PieceKind.Encode, firstFrame, actualEnd));
        else
        {
            if (firstFrame < copyStart - Epsilon) pieces.Add(new CutPiece(PieceKind.Encode, firstFrame, copyStart));
            pieces.Add(new CutPiece(PieceKind.Copy, copyStart, copyEnd));
            if (copyEnd < actualEnd - Epsilon) pieces.Add(new CutPiece(PieceKind.Encode, copyEnd, actualEnd));
        }
        var plan = new CutPlan(firstFrame, actualEnd, pieces, "");
        var encoded = plan.EncodedDuration;
        return plan with
        {
            Note = encoded == TimeSpan.Zero
                ? "Lossless copy; both cuts fall on keyframes."
                : $"Frame-accurate; re-encodes {encoded.TotalSeconds:0.##} s next to the cuts and copies the rest.",
        };
    }

    /// <summary>The keyframe nearest <paramref name="position"/> that leaves both parts non-empty, or null.</summary>
    public static TimeSpan? PlanSplit(IReadOnlyList<VideoPacket> nearPosition, TimeSpan position, TimeSpan videoEnd) =>
        nearPosition.Where(packet => packet.IsKey && packet.Pts > Epsilon && packet.Pts < videoEnd - Epsilon)
            .OrderBy(packet => (packet.Pts - position).Duration()).Select(packet => (TimeSpan?)packet.Pts).FirstOrDefault();
}
