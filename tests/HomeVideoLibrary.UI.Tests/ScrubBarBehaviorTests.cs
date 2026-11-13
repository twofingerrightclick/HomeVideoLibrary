using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class ScrubBarBehaviorTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMinutes(40);

    [AvaloniaFact]
    public void Loading_a_hover_frame_keeps_the_preview_size_and_hand_cursor()
    {
        Border? tooltip = null;
        Popup? popup = null;
        var scrub = new ScrubBar { Duration = Length };
        scrub.TemplateApplied += (_, e) =>
        {
            tooltip = e.NameScope.Find<Border>("PART_Tooltip");
            popup = e.NameScope.Find<Popup>("PART_Popup");
        };
        var (window, bar) = Host(scrub);
        try
        {
            Assert.NotNull(tooltip);
            Assert.NotNull(popup);
            var frame = Assert.IsType<Border>(Assert.IsType<StackPanel>(tooltip.Child).Children[0]);
            tooltip.Measure(Size.Infinity);
            var emptySize = tooltip.DesiredSize;
            Assert.True(frame.IsVisible);

            using var image = new RenderTargetBitmap(new PixelSize(32, 18));
            bar.HoverPreview = image;
            tooltip.Measure(Size.Infinity);
            Assert.Equal(emptySize, tooltip.DesiredSize);
            bar.HoverPreview = null;
            tooltip.Measure(Size.Infinity);
            Assert.Equal(emptySize, tooltip.DesiredSize);
            Assert.Equal("Hand", tooltip.Cursor?.ToString());
            Assert.Equal("Hand", popup.Cursor?.ToString());
            Assert.False(tooltip.IsHitTestVisible);
            Assert.False(popup.IsHitTestVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(0, -6, false)]
    [InlineData(0, 6, false)]
    [InlineData(3, 3, false)]
    [InlineData(-3, 3, false)]
    [InlineData(3, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(2, -2, true)]
    [InlineData(0, 3, true)]
    public void MomentHoverMatchesDotGeometryAndRetainsPointerHeightOnRefresh(double dx, double dy, bool inside)
    {
        var moment = TimeSpan.FromMinutes(20);
        var (window, bar) = Host(new ScrubBar { Duration = Length, ChapterSegments = [
            new ChapterMark(TimeSpan.Zero, "Range") { End = Length }, new ChapterMark(moment, "Moment")] });
        var center = At(bar, 0.5);
        window.MouseMove(center + new Avalonia.Vector(dx, dy));
        Assert.Equal(inside ? "Moment" : "Range", bar.HoverChapterTitle);

        bar.ChapterSegments = [new ChapterMark(TimeSpan.Zero, "Updated range") { End = Length },
            new ChapterMark(moment, "Updated moment")];
        Assert.Equal(inside ? "Updated moment" : "Updated range", bar.HoverChapterTitle);
        window.Close();
    }

    [AvaloniaFact]
    public void MomentHoverUsesItsExactTimeAndTitleInsideARange()
    {
        var moment = TimeSpan.FromMinutes(20);
        var (window, bar) = Host(new ScrubBar { Duration = Length, ChapterSegments = [
            new ChapterMark(TimeSpan.Zero, "Range") { End = Length }, new ChapterMark(moment, "Birthday")] });
        window.MouseMove(At(bar, 0.5));
        Assert.Equal(moment, bar.HoverTime);
        Assert.Equal("Birthday", bar.HoverChapterTitle);
    }

    [AvaloniaFact]
    public void A_drag_requests_exactly_one_seek_on_release_and_never_writes_Position()
    {
        var command = new RecordingCommand();
        var (window, bar) = Host(new ScrubBar { Duration = Length, Position = TimeSpan.FromMinutes(5), SeekCommand = command });
        var events = new List<TimeSpan>();
        bar.SeekRequested += (_, e) => events.Add(e.Time);

        window.MouseDown(At(bar, 0.25), MouseButton.Left);
        window.MouseMove(At(bar, 0.75));
        Assert.Empty(command.Calls);
        Assert.InRange(bar.ScrubPosition!.Value.TotalMinutes, 29, 31);

        window.MouseUp(At(bar, 0.75), MouseButton.Left);

        var sought = Assert.IsType<TimeSpan>(Assert.Single(command.Calls));
        Assert.Equal([sought], events);
        Assert.InRange(sought.TotalMinutes, 29, 31);
        Assert.Equal(TimeSpan.FromMinutes(5), bar.Position);
        Assert.Null(bar.ScrubPosition);
    }

    [AvaloniaFact]
    public void Keyboard_seeks_accumulate_from_the_pending_request_without_writing_Position()
    {
        var command = new RecordingCommand();
        var (window, bar) = Host(new ScrubBar { Duration = Length, Position = TimeSpan.FromMinutes(5), SeekCommand = command });
        bar.Focus();

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);

        Assert.Equal([(object)TimeSpan.FromSeconds(305), TimeSpan.FromSeconds(310)], command.Calls);
        Assert.Equal(TimeSpan.FromMinutes(5), bar.Position);

        bar.Position = TimeSpan.FromSeconds(310);
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
        Assert.Equal(TimeSpan.FromSeconds(309), command.Calls[^1]);
    }

    [AvaloniaFact]
    public void Escape_cancels_a_drag_without_seeking()
    {
        var command = new RecordingCommand();
        var (window, bar) = Host(new ScrubBar { Duration = Length, SeekCommand = command });

        window.MouseDown(At(bar, 0.5), MouseButton.Left);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.LeftMouseButton);

        Assert.False(bar.IsScrubbing);
        Assert.Empty(command.Calls);
    }

    [AvaloniaFact]
    public void Assigning_a_new_chapter_snapshot_updates_what_hover_shows()
    {
        var (window, bar) = Host(new ScrubBar { Duration = Length, ChapterSegments = [new ChapterMark(TimeSpan.Zero, "Before") { End = Length }] });
        window.MouseMove(At(bar, 0.6));
        Assert.Equal("Before", bar.HoverChapterTitle);

        bar.ChapterSegments = [new ChapterMark(TimeSpan.Zero, "Before") { End = TimeSpan.FromMinutes(20) }, new ChapterMark(TimeSpan.FromMinutes(20), "After") { End = Length }];
        window.MouseMove(At(bar, 0.61));

        Assert.Equal("After", bar.HoverChapterTitle);
    }

    [AvaloniaFact]
    public void Hovering_a_chapter_without_a_title_shows_no_title()
    {
        var (window, bar) = Host(new ScrubBar
        {
            Duration = Length,
            ChapterSegments = [new NullTitledMark(TimeSpan.Zero), new NullTitledMark(TimeSpan.FromMinutes(20))],
        });

        window.MouseMove(At(bar, 0.6));

        Assert.NotNull(bar.HoverTime);
        Assert.Null(bar.HoverChapterTitle);
    }

    private sealed class NullTitledMark(TimeSpan start) : IChapterMark
    {
        public TimeSpan Start { get; } = start;

        public string Title => null!;
    }
}
