using System.Collections.Concurrent;
using System.Globalization;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Encoder arguments that reproduce a source's video and audio parameters as closely as the encoders allow.</summary>
internal static class VideoEncoding
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Encoders = new();

    private static readonly HashSet<string> X264PixelFormats =
        ["yuv420p", "yuvj420p", "yuv422p", "yuvj422p", "yuv444p", "yuvj444p", "nv12", "nv16", "nv21", "yuv420p10le", "yuv422p10le", "yuv444p10le", "nv20le", "gray", "gray10le"];

    private static readonly HashSet<string> X265PixelFormats =
        ["yuv420p", "yuvj420p", "yuv422p", "yuvj422p", "yuv444p", "yuvj444p", "yuv420p10le", "yuv422p10le", "yuv444p10le", "gray", "gray10le"];

    /// <summary>ffmpeg's encoder list, read once per executable.</summary>
    public static Task<string> ListEncodersAsync(MediaTools tools, MediaProcess process) =>
        Encoders.GetOrAdd(tools.Ffmpeg, _ => new Lazy<Task<string>>(async () =>
            (await process.RunAsync(tools.Ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(30))).StandardOutput)).Value;

    public static async Task<bool> CanEncodeAsync(MediaTools tools, MediaProcess process, string codecName) =>
        (await ListEncodersAsync(tools, process)).Contains(codecName switch { "hevc" => " libx265 ", _ => " libx264 " }, StringComparison.Ordinal);

    /// <summary>The raw (Annex-B) bitstream filter and format for codecs whose pieces can be joined by concatenation.</summary>
    public static (string Filter, string Format)? AnnexB(string codecName) => codecName switch
    {
        "h264" => ("h264_mp4toannexb", "h264"),
        "hevc" => ("hevc_mp4toannexb", "hevc"),
        _ => null,
    };

    /// <summary>libx264 or libx265 arguments matching <paramref name="video"/>'s profile, level, pixel format and colour,
    /// so a re-encoded piece can sit between copied ones. Null when the profile has no encoder equivalent.</summary>
    public static IReadOnlyList<string>? MatchingVideoArguments(VideoStreamInfo video)
    {
        var arguments = new List<string>();
        if (video.CodecName == "h264")
        {
            var profile = video.Profile switch
            {
                "Baseline" or "Constrained Baseline" => "baseline",
                "Main" => "main",
                "High" => "high",
                "High 10" => "high10",
                "High 4:2:2" => "high422",
                "High 4:4:4 Predictive" => "high444",
                _ => null,
            };
            if (profile is null || !X264PixelFormats.Contains(video.PixelFormat)) return null;
            arguments.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "16", "-profile:v", profile]);
            if (video.Level > 0)
                arguments.AddRange(["-level:v", (video.Level / 10d).ToString("0.0", CultureInfo.InvariantCulture)]);
        }
        else if (video.CodecName == "hevc")
        {
            if (!X265PixelFormats.Contains(video.PixelFormat)) return null;
            arguments.AddRange(["-c:v", "libx265", "-preset", "fast", "-crf", "18", "-x265-params", "log-level=error"]);
        }
        else return null;
        arguments.AddRange(["-pix_fmt", video.PixelFormat]);
        arguments.AddRange(ColorArguments(video));
        return arguments;
    }

    /// <summary>libx264 arguments for normalizing a clip to <paramref name="target"/>.</summary>
    public static IReadOnlyList<string> NormalizingVideoArguments(VideoTarget target)
    {
        var arguments = new List<string> { "-vf", target.Filter, "-fps_mode", "cfr" };
        if (target.CodecName == "hevc")
            arguments.AddRange(["-c:v", "libx265", "-preset", "fast", "-crf", "18", "-x265-params", "log-level=error"]);
        else
            arguments.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "18"]);
        arguments.AddRange(["-pix_fmt", target.PixelFormat]);
        arguments.AddRange(ColorArguments(target.Colour));
        return arguments;
    }

    public static string EncodablePixelFormat(string codecName, string pixelFormat) =>
        (codecName == "hevc" ? X265PixelFormats : X264PixelFormats).Contains(pixelFormat) ? pixelFormat : "yuv420p";

    private static IEnumerable<string> ColorArguments(VideoStreamInfo video)
    {
        (string Option, string Value)[] tags =
            [("-color_range", video.ColorRange), ("-colorspace", video.ColorSpace), ("-color_primaries", video.ColorPrimaries), ("-color_trc", video.ColorTransfer)];
        return tags.Where(tag => Known(tag.Value)).SelectMany(tag => new[] { tag.Option, tag.Value });
    }

    private static bool Known(string value) => value.Length > 0 && value != "unknown" && value != "reserved";

    /// <summary>The ffmpeg encoder that writes <paramref name="track"/>'s codec, or null when there is none worth using.</summary>
    public static IReadOnlyList<string>? AudioEncoder(MkvTrack track) => track.CodecId switch
    {
        "A_AAC" or "A_AAC/MPEG4/LC" or "A_AAC/MPEG2/LC" => ["aac", "-b:a", "192k"],
        "A_AC3" => ["ac3", "-b:a", "384k"],
        "A_EAC3" => ["eac3", "-b:a", "384k"],
        "A_FLAC" => ["flac"],
        "A_OPUS" => ["libopus", "-b:a", "160k"],
        "A_VORBIS" => ["libvorbis", "-q:a", "6"],
        "A_MPEG/L3" => ["libmp3lame", "-b:a", "256k"],
        "A_MPEG/L2" => ["mp2", "-b:a", "256k"],
        "A_PCM/INT/LIT" => [track.BitsPerSample switch { 24 => "pcm_s24le", 32 => "pcm_s32le", 8 => "pcm_u8", _ => "pcm_s16le" }],
        "A_PCM/INT/BIG" => [track.BitsPerSample switch { 24 => "pcm_s24be", 32 => "pcm_s32be", _ => "pcm_s16be" }],
        _ => null,
    };
}

/// <summary>The video every clip of a re-encoding merge is brought to.</summary>
internal sealed record VideoTarget(string CodecName, int Width, int Height, string SampleAspectRatio, string FrameRate,
    string PixelFormat, VideoStreamInfo Colour, TimeSpan FrameDuration)
{
    /// <summary>Deinterlaces frames flagged as interlaced, scales square-pixel-aware without upscaling, pads to the
    /// centre, then forces a constant frame rate.</summary>
    public string Filter
    {
        get
        {
            var sar = SampleAspectRatio.Replace(':', '/');
            return "yadif=deint=interlaced," +
                $"scale=w='max(2,trunc(iw*sar/({sar})/2)*2)':h='max(2,trunc(ih/2)*2)',setsar={sar}," +
                $"scale=w='min(iw,{Width})':h='min(ih,{Height})':force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,setsar={sar},fps={FrameRate}";
        }
    }
}
