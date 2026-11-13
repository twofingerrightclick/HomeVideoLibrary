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
