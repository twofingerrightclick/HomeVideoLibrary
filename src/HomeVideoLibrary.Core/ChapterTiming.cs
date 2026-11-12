namespace HomeVideoLibrary.Core;

/// <summary>Explicit annotation timing. Moments never constrain chapter ranges.</summary>
public static class ChapterTiming
{
    public static string? Conflict(IReadOnlyList<VideoChapter> chapters, TimeSpan duration)
    {
        if (chapters.Any(c => c.Position < TimeSpan.Zero || c.Position >= duration || c.End < c.Position || c.End > duration))
            return "Chapter and moment times must fall inside the video, with end at or after start.";
        var ranges = chapters.Where(c => !c.IsMoment).OrderBy(c => c.Position).ToArray();
        for (var i = 1; i < ranges.Length; i++)
            if (ranges[i - 1].End > ranges[i].Position)
                return "Chapters overlap. Adjust their start or end times before saving.";
        return null;
    }

    /// <summary>Applies one authored change atomically; only its immediate range neighbors may change.</summary>
    public static IReadOnlyList<VideoChapter> Apply(IReadOnlyList<VideoChapter> chapters, VideoChapter proposed, TimeSpan duration)
    {
        if (proposed.Position < TimeSpan.Zero || proposed.Position >= duration || proposed.End < proposed.Position)
            throw new ArgumentException("Start must be before the video end, and end must be at or after start.");
        var others = chapters.Where(c => c.Uid != proposed.Uid).ToList();
        if (others.Any(c => c.IsMoment == proposed.IsMoment && c.Position == proposed.Position))
            throw new ArgumentException(proposed.IsMoment ? "A moment already exists at this time." : "A chapter already starts at this time.");
        if (!proposed.IsMoment)
        {
            var next = others.Where(c => !c.IsMoment && c.Position > proposed.Position).OrderBy(c => c.Position).FirstOrDefault();
            proposed = proposed with { End = TimeSpan.FromTicks(Math.Min(proposed.End.Ticks, Math.Min(duration.Ticks, next?.Position.Ticks ?? duration.Ticks))) };
            var previous = others.Where(c => !c.IsMoment && c.Position < proposed.Position).OrderBy(c => c.Position).LastOrDefault();
            if (previous is not null && previous.End > proposed.Position)
                others[others.IndexOf(previous)] = previous with { End = proposed.Position };
        }
        others.Add(proposed);
        return others.OrderBy(c => c.Position).ThenBy(c => c.Uid).ToArray();
    }
}
