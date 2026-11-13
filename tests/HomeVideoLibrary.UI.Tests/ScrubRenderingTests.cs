using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class ScrubRenderingTests
{
    [AvaloniaFact]
    public void ListHoverEnlargesOnlyItsChapterWithoutOpeningTimelinePreview()
    {
        var duration = TimeSpan.FromSeconds(60);
        var (window, bar) = Host(new ScrubBar { Duration = duration, Position = duration, ChapterSegments = [
            new ChapterMark(TimeSpan.Zero, "First") { End = TimeSpan.FromSeconds(30) },
            new ChapterMark(TimeSpan.FromSeconds(30), "Second") { End = duration }] });
        int RedHeight(double fraction)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame()!;
            using var buffer = frame.Lock();
            var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
            Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
            var red = buffer.Format == PixelFormat.Rgba8888 ? 0 : 2;
            var x = (int)At(bar, fraction).X;
            return Enumerable.Range(0, buffer.Size.Height).Count(y =>
            {
                var pixel = y * buffer.RowBytes + x * 4;
                return bytes[pixel + red] > 100 && bytes[pixel + red] > bytes[pixel + 1] * 1.5;
            });
        }
        var first = RedHeight(0.25);
        var second = RedHeight(0.75);
        bar.HighlightedChapterStart = TimeSpan.Zero;
        Assert.True(RedHeight(0.25) > first);
        Assert.Equal(second, RedHeight(0.75));
        Assert.Null(bar.HoverTime);
        bar.HighlightedChapterStart = null;
        Assert.Equal(first, RedHeight(0.25));
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("moment")]
    [InlineData("chapter")]
    public void PlayedTrackRemainsRedWithOrWithoutAnnotations(string kind)
    {
        var duration = TimeSpan.FromSeconds(60);
        var marks = kind switch
        {
            "moment" => new[] { new ChapterMark(TimeSpan.FromSeconds(30), "Moment") },
            "chapter" => [new ChapterMark(TimeSpan.FromSeconds(5), "Chapter") { End = TimeSpan.FromSeconds(40) }],
            _ => Array.Empty<ChapterMark>(),
        };
        var (window, bar) = Host(new ScrubBar { Duration = duration, Position = TimeSpan.FromSeconds(40), ChapterSegments = marks });
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame.");
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var redChannel = buffer.Format == PixelFormat.Rgba8888 ? 0 : 2;
        // Sample the played track away from the thumb and annotations.
        var point = At(bar, 0.2);
        var pixel = (int)point.Y * buffer.RowBytes + (int)point.X * 4;
        Assert.True(bytes[pixel + redChannel] > 100 && bytes[pixel + redChannel] > bytes[pixel + 1] * 1.5,
            $"Played track must be red; pixel R={bytes[pixel + redChannel]}, G={bytes[pixel + 1]} ({kind}).");
        window.Close();
    }
}
