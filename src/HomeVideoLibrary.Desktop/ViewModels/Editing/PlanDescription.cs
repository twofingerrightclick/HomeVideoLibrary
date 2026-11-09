using System.Globalization;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>A plan as the page shows it.</summary>
/// <param name="Summary">One line under the controls.</param>
/// <param name="Details">The info popover: what will happen and why, in plain words.</param>
/// <param name="Renders">Whether the edit really re-encodes (the button reads RENDER). A copy, or a precise cut that
/// re-encodes only the frames next to its cuts, saves (SAVE).</param>
public sealed record PlanDescription(string Summary, string Details, bool Renders)
{
    public static PlanDescription Message(string text) => new(text, text, false);

    public static PlanDescription Trim(ClipCutPlan plan)
    {
        var kept = plan.ActualEnd - plan.ActualStart;
        var range = $"{Timecode.Format(plan.ActualStart)} – {Timecode.Format(plan.ActualEnd)}";
        // Nothing is copied when no keyframe falls inside the range; that is a real re-encode, however short.
        var renders = plan.ReencodedDuration > TimeSpan.Zero && plan.ReencodedDuration >= kept - TimeSpan.FromMilliseconds(1);
        var summary = $"Keeps {range} · {(plan.IsLossless ? "lossless" : renders ? "re-encoded" : "precise")}. {plan.Note}";
        var details = plan.IsLossless
            ? $"Saves {range} ({Length(kept)}) as a new video by copying the picture and sound exactly. Nothing is re-encoded, so it is quick and loses no quality. {plan.Note}"
            : renders
                ? $"Saves {range} ({Length(kept)}). No keyframe falls inside this range, so all of it is re-encoded: {Length(kept)} of video to render."
                : $"Saves {range} ({Length(kept)}). The {Length(plan.ReencodedDuration)} next to the cuts is re-encoded so the clip starts and ends on the exact frames; the other {Length(kept - plan.ReencodedDuration)} is copied exactly. Quick.";
        return new PlanDescription(summary, details, renders);
    }

    public static PlanDescription Split(ClipCutPlan plan)
    {
        var at = Timecode.Format(plan.ActualStart);
        return new PlanDescription($"Part 2 starts at {at} · lossless. {plan.Note}",
            $"Saves two new videos: part 1 up to {at} and part 2 from {at} to the end. Both are copied exactly; nothing is re-encoded. {plan.Note}",
            Renders: !plan.IsLossless);
    }

    /// <param name="order">The sources in merge order, for naming the clips.</param>
    public static PlanDescription Merge(ClipMergePlan plan, IReadOnlyList<LibraryVideo> order)
    {
        if (plan.IsLossless)
            return new PlanDescription(plan.Note, $"Joins the clips by copying their picture and sound exactly. Nothing is re-encoded, so it is quick and loses no quality. {plan.Note}", false);
        var lines = plan.Clips.Select((clip, index) => (clip, index)).Where(item => item.clip.Reason is not null)
            .Select(item => $"Clip {item.index + 1} ({order[item.index].Label}) is re-encoded because {item.clip.Reason}.").ToList();
        var video = plan.Clips.Where(clip => clip.ReencodesVideo).Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Duration);
        lines.Add(video > TimeSpan.Zero
            ? $"That is {Length(video)} of video to render; everything else is copied exactly."
            : "Only sound is converted; every picture is copied exactly.");
        lines.Add(plan.Note);
        return new PlanDescription(plan.Note, string.Join("\n\n", lines), true);
    }

    /// <summary>A length as people say it: "1.3 s", "6 min 2 s", "1 h 4 min".</summary>
    private static string Length(TimeSpan length) => length.TotalSeconds < 60
        ? string.Create(CultureInfo.InvariantCulture, $"{length.TotalSeconds:0.#} s")
        : length.TotalHours < 1 ? $"{(int)length.TotalMinutes} min {length.Seconds} s" : $"{(int)length.TotalHours} h {length.Minutes} min";
}
