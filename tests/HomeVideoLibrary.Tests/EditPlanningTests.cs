using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure.Editing;
using Xunit;

namespace HomeVideoLibrary.Tests;

/// <summary>The pure rules behind edit plans: the kept range, what a cut re-encodes and why a merge re-encodes a clip.</summary>
public sealed class EditPlanningTests
{
    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    [Fact]
    public void AnOutPointPastTheEndKeepsToTheEnd() =>
        Assert.Equal((TimeSpan.FromSeconds(2), Ten), ClipRange.Validate(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), Ten));

    [Theory]
    [InlineData(10, 12, "The in point is at or past the end of the video (0:00:10.000).")]
    [InlineData(11, 12, "The in point is at or past the end of the video (0:00:10.000).")]
    [InlineData(5, 4, "The out point must come after the in point.")]
    [InlineData(5, 5.05, "Keep at least 0.1 seconds between the in and out points.")]
    public void RangesThatCannotBeKeptSayWhyInPlainWords(double start, double end, string problem)
    {
        var exception = Assert.Throws<ClipRangeException>(() => ClipRange.Validate(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), Ten));
        Assert.Equal(problem, exception.Message);
        Assert.Equal(problem, ClipRange.Problem(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), Ten));
    }

    [Fact]
    public void APreciseCutWithNoKeyframeInsideRendersAllOfIt()
    {
        // Keyframes every 2 s; 2.4..3.6 falls inside one group of pictures.
        var packets = Enumerable.Range(0, 100).Select(frame => new VideoPacket(TimeSpan.FromMilliseconds(frame * 100), frame % 20 == 0)).ToArray();

        var plan = CutPlanner.PlanPreciseTrim(packets, packets, TimeSpan.FromSeconds(2.4), TimeSpan.FromSeconds(3.6), Ten).ToPublic();

        Assert.Equal(TimeSpan.FromSeconds(1.2), plan.ReencodedDuration);
        Assert.Equal(plan.ActualEnd - plan.ActualStart, plan.ReencodedDuration);
        Assert.True(plan.RendersVideo);
    }

    [Fact]
    public void ASmartCutAtBothEndsDoesNotRender()
    {
        var packets = Enumerable.Range(0, 100).Select(frame => new VideoPacket(TimeSpan.FromMilliseconds(frame * 100), frame % 20 == 0)).ToArray();

        var plan = CutPlanner.PlanPreciseTrim(packets, packets, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6.5), Ten).ToPublic();

        Assert.Equal(TimeSpan.FromSeconds(1), plan.ReencodedDuration); // 1.5..2.0 and 6.0..6.5.
        Assert.False(plan.RendersVideo);
    }

    [Theory]
    [InlineData("codec", true, "it is HEVC video and the merged video is H.264")]
    [InlineData("profile", true, "it uses the Main profile and the merged video uses High")]
    [InlineData("size", true, "it is 720×480 and the merged video is 1920×1080")]
    [InlineData("pixel shape", true, "its pixel shape (8:9) differs from the merged video's (1:1)")]
    [InlineData("interlacing", true, "its interlacing (tt) differs from the merged video's (progressive)")]
    [InlineData("colour", true, "its colour format (yuv422p) differs from the merged video's (yuv420p)")]
    [InlineData("sound", false, "its sound differs from the other clips' and is converted to match")]
    [InlineData("no raw join", true, "MPEG-2 video can't be joined without re-encoding")]
    [InlineData("two video tracks", true, "a clip has more than one video track")]
    [InlineData("no HEVC encoder", true, "joining HEVC video needs an HEVC encoder, and this ffmpeg has none")]
    public void AMergeSaysWhyEachClipIsReencoded(string difference, bool rendersVideo, string reason)
    {
        // The longer first clip sets the merged format; the second differs from it in one way.
        var dominant = Clip(codec: difference switch { "no raw join" => "mpeg2video", "no HEVC encoder" => "hevc", _ => "h264" }, seconds: 10);
        var other = difference switch
        {
            "codec" => Clip(codec: "hevc"),
            "profile" => Clip(profile: "Main"),
            "pixel shape" => Clip(sar: "8:9"),
            "interlacing" => Clip(fieldOrder: "tt"),
            "colour" => Clip(pixelFormat: "yuv422p"),
            "sound" => Clip(audioCodecId: "A_AC3"),
            "no raw join" => Clip(codec: "mpeg2video", width: 720, height: 480),
            "two video tracks" => Clip(videoTracks: 2),
            "no HEVC encoder" => Clip(codec: "hevc", width: 720, height: 480),
            _ => Clip(width: 720, height: 480),
        };

        var plan = MergePlanner.Plan([dominant, other], canEncodeHevc: false).ToPublic();

        Assert.False(plan.IsLossless);
        Assert.Equal(reason, plan.Clips[1].Reason);
        Assert.Equal(rendersVideo, plan.RendersVideo);
        Assert.Equal(rendersVideo, plan.Clips[1].ReencodesVideo);
    }

    /// <summary>A clip with one audio track whose codec configuration is unique, so mkvmerge would not be expected to
    /// append it. Tests make the first clip longer, so it sets the merged format.</summary>
    private static EditSource Clip(string codec = "h264", string profile = "High", int width = 1920, int height = 1080, string sar = "1:1",
        string fieldOrder = "progressive", string pixelFormat = "yuv420p", string audioCodecId = "A_AAC", int videoTracks = 1, double seconds = 4)
    {
        var length = TimeSpan.FromSeconds(seconds);
        var codecId = codec switch { "h264" => "V_MPEG4/ISO/AVC", "hevc" => "V_MPEGH/ISO/HEVC", _ => "V_MPEG2" };
        var tracks = Enumerable.Range(0, videoTracks)
            .Select(index => new MkvTrack(index, "video", codec, codecId, Guid.NewGuid().ToString("N"), $"{width}x{height}", 40_000_000, 0, 0, 0,
                "und", new Dictionary<string, string>()))
            .Append(new MkvTrack(videoTracks, "audio", "audio", audioCodecId, "", "", 0, 48_000, 2, 0, "eng", new Dictionary<string, string>()))
            .ToArray();
        return new EditSource("/library/clip.mkv", new MkvInfo(length, tracks, []),
            new VideoStreamInfo(codec, profile, 40, pixelFormat, fieldOrder, "tv", "bt709", "bt709", "bt709", sar, "25/1", width, height),
            new MediaTimeline(TimeSpan.Zero, length, length, TimeSpan.FromMilliseconds(40), length, length));
    }
}
