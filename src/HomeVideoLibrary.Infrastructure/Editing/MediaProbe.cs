using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>A track as <c>mkvmerge -J</c> reports it. Empty strings and zeros mean "not reported".</summary>
internal sealed record MkvTrack(int Id, string Type, string Codec, string CodecId, string CodecPrivate, string PixelDimensions,
    string DisplayDimensions, long DefaultDurationNanoseconds, double SamplingFrequency, int Channels, int BitsPerSample,
    string Language, string Name)
{
    /// <summary>Everything mkvmerge needs to be equal to append this track to another one cleanly.</summary>
    public string AppendSignature => string.Join('|', Type, CodecId, CodecPrivate, PixelDimensions, DisplayDimensions,
        DefaultDurationNanoseconds.ToString(CultureInfo.InvariantCulture), SamplingFrequency.ToString(CultureInfo.InvariantCulture),
        Channels.ToString(CultureInfo.InvariantCulture), BitsPerSample.ToString(CultureInfo.InvariantCulture));

    public ExpectedTrack Expected => new(Type, Codec, Language);
}

internal sealed record MkvAttachment(int Id, string FileName);

internal sealed record MkvInfo(TimeSpan Duration, IReadOnlyList<MkvTrack> Tracks, IReadOnlyList<MkvAttachment> Attachments)
{
    private const string ManifestName = "home-video-library.thumbnails.v1.json";

    /// <summary>Attachments this app owns (thumbnails and their manifest); edits rewrite them, so muxing leaves them out.</summary>
    public IReadOnlyList<int> AppAttachmentIds => Attachments
        .Where(attachment => attachment.FileName == ManifestName ||
            (attachment.FileName.StartsWith("hvl-thumbnail-", StringComparison.Ordinal) && attachment.FileName.EndsWith(".jpg", StringComparison.Ordinal)))
        .Select(attachment => attachment.Id).ToArray();

    public static MkvInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var duration = root.TryGetProperty("container", out var container) && container.TryGetProperty("properties", out var containerProperties) &&
            containerProperties.TryGetProperty("duration", out var nanoseconds) && nanoseconds.TryGetInt64(out var value)
            ? TimeSpan.FromTicks(value / 100) : TimeSpan.Zero;
        var tracks = root.TryGetProperty("tracks", out var trackArray)
            ? trackArray.EnumerateArray().Select(ParseTrack).ToArray() : [];
        var attachments = root.TryGetProperty("attachments", out var attachmentArray)
            ? attachmentArray.EnumerateArray().Select(attachment => new MkvAttachment(
                attachment.GetProperty("id").GetInt32(), String(attachment, "file_name"))).ToArray()
            : [];
        return new MkvInfo(duration, tracks, attachments);
    }

    private static MkvTrack ParseTrack(JsonElement track)
    {
        var properties = track.TryGetProperty("properties", out var element) ? element : default;
        return new MkvTrack(track.GetProperty("id").GetInt32(), String(track, "type"), String(track, "codec"),
            String(properties, "codec_id"), String(properties, "codec_private_data"), String(properties, "pixel_dimensions"),
            String(properties, "display_dimensions"), Int64(properties, "default_duration"),
            properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("audio_sampling_frequency", out var frequency) &&
            frequency.TryGetDouble(out var hertz) ? hertz : 0,
            (int)Int64(properties, "audio_channels"), (int)Int64(properties, "audio_bits_per_sample"),
            String(properties, "language") is { Length: > 0 } language ? language : "und", String(properties, "track_name"));
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static long Int64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
}

/// <summary>The primary video stream as ffprobe reports it.</summary>
internal sealed record VideoStreamInfo(string CodecName, string Profile, int Level, string PixelFormat, string FieldOrder,
    string ColorRange, string ColorSpace, string ColorPrimaries, string ColorTransfer, string SampleAspectRatio,
    string AverageFrameRate, int Width, int Height)
{
    public bool IsProgressive => FieldOrder == "progressive";
}

internal sealed record VideoPacket(TimeSpan Pts, bool IsKey);

internal sealed record VideoEnd(TimeSpan End, TimeSpan FrameDuration);

/// <param name="VideoEnd">Where the primary video's last frame ends; container durations can be longer because of
/// audio or subtitles that run past it.</param>
internal sealed record EditSource(string Path, MkvInfo Mkv, VideoStreamInfo Video, TimeSpan VideoEnd, TimeSpan FrameDuration)
{
    public MkvTrack VideoTrack => Mkv.Tracks.First(track => track.Type == "video");
    public IReadOnlyList<MkvTrack> AudioTracks => Mkv.Tracks.Where(track => track.Type == "audio").ToArray();

    /// <summary>True when every frame lasts <see cref="FrameDuration"/>, which raw-stream joins depend on.</summary>
    public bool IsConstantFrameRate
    {
        get
        {
            var track = VideoTrack;
            if (track.DefaultDurationNanoseconds <= 0 || !MediaProbe.TryParseRatio(Video.AverageFrameRate, out var rate)) return false;
            var expected = 1e9 / rate;
            return Math.Abs(track.DefaultDurationNanoseconds - expected) / expected < 0.002;
        }
    }
}

/// <summary>Reads what editing needs to know about library MKVs. Source probes are cached by path, size and write time.</summary>
internal sealed class MediaProbe(MediaTools tools, MediaProcess process)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), Task<EditSource>> _sources = new();

    public async Task<EditSource> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The video file is missing from the library.", path);
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        // Probes take well under a second; letting a cancelled caller's probe finish keeps the shared cache entry usable.
        var task = _sources.GetOrAdd(key, _ => ProbeUncachedAsync(file.FullName, CancellationToken.None));
        try { return await task.WaitAsync(cancellationToken); }
        catch (Exception) when (task.IsFaulted)
        {
            _sources.TryRemove(new KeyValuePair<(string, long, DateTime), Task<EditSource>>(key, task));
            throw;
        }
    }

    private async Task<EditSource> ProbeUncachedAsync(string path, CancellationToken cancellationToken)
    {
        var identify = await process.RunAsync(tools.MkvMerge, ["-J", path], ProbeTimeout, cancellationToken);
        if (identify.ExitCode > 1)
            throw new InvalidDataException("The video is not a readable MKV. " + MediaErrors.Summarize(identify.StandardOutput));
        var mkv = MkvInfo.Parse(identify.StandardOutput);
        if (!mkv.Tracks.Any(track => track.Type == "video"))
            throw new InvalidDataException("Editing requires a video track.");
        var video = await ReadVideoStreamAsync(path, cancellationToken);
        var end = await ReadVideoEndAsync(process, tools, path, cancellationToken);
        var frameDuration = mkv.Tracks.First(track => track.Type == "video").DefaultDurationNanoseconds is > 0 and var nanoseconds
            ? TimeSpan.FromTicks(nanoseconds / 100) : end.FrameDuration;
        return new EditSource(path, mkv, video, end.End, frameDuration);
    }

    private async Task<VideoStreamInfo> ReadVideoStreamAsync(string path, CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-show_entries",
            "stream=codec_name,profile,level,pix_fmt,field_order,color_range,color_space,color_primaries,color_transfer,sample_aspect_ratio,avg_frame_rate,width,height",
            "-of", "json", path], ProbeTimeout, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("The video is unreadable. " + MediaErrors.Summarize(result.StandardError));
        using var document = JsonDocument.Parse(result.StandardOutput);
        var stream = document.RootElement.TryGetProperty("streams", out var streams) ? streams.EnumerateArray().FirstOrDefault() : default;
        if (stream.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Editing requires a video stream.");
        string Text(string name) => stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        int Number(string name) => stream.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
        return new VideoStreamInfo(Text("codec_name"), Text("profile"), Number("level"), Text("pix_fmt"), Text("field_order"),
            Text("color_range"), Text("color_space"), Text("color_primaries"), Text("color_transfer"), Text("sample_aspect_ratio"),
            Text("avg_frame_rate"), Number("width"), Number("height"));
    }

    /// <summary>Video packets of the first video stream with presentation times in about [<paramref name="from"/>,
    /// <paramref name="from"/> + <paramref name="length"/>], starting at the keyframe at or before <paramref name="from"/>,
    /// sorted by presentation time. A null length reads to the end of the file.</summary>
    public async Task<IReadOnlyList<VideoPacket>> ReadPacketsAsync(string path, TimeSpan from, TimeSpan? length, CancellationToken cancellationToken)
    {
        var start = from < TimeSpan.Zero ? TimeSpan.Zero : from;
        var interval = Seconds(start) + "%" + (length is { } span ? "+" + Seconds(span) : "");
        var packets = new List<VideoPacket>();
        var lines = new LineSplitter(line =>
        {
            var fields = line.Split(',');
            if (fields.Length >= 2 && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                packets.Add(new VideoPacket(TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)), fields[1].StartsWith('K')));
        });
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-read_intervals", interval,
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path], ProbeTimeout, cancellationToken, lines.Append);
        lines.Append("\n");
        if (result.ExitCode != 0) throw new InvalidDataException("The video's frames could not be read. " + MediaErrors.Summarize(result.StandardError));
        return packets.OrderBy(packet => packet.Pts).ToArray();
    }

    public Task<VideoEnd> ReadVideoEndAsync(string path, CancellationToken cancellationToken) =>
        ReadVideoEndAsync(process, tools, path, cancellationToken);

    /// <summary>Where the first video stream's last frame ends, from the packets near the container's end.</summary>
    public static async Task<VideoEnd> ReadVideoEndAsync(MediaProcess process, MediaTools tools, string path, CancellationToken cancellationToken)
    {
        var format = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
            ProbeTimeout, cancellationToken);
        double.TryParse(format.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var containerSeconds);
        var from = Math.Max(0, containerSeconds - 10);
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0",
            "-read_intervals", from.ToString("0.###", CultureInfo.InvariantCulture) + "%", "-show_entries", "packet=pts_time,duration_time",
            "-of", "csv=p=0", path], ProbeTimeout, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("The video's frames could not be read. " + MediaErrors.Summarize(result.StandardError));
        var packets = result.StandardOutput.Split('\n').Select(line => line.Trim().Split(','))
            .Where(fields => fields.Length >= 1 && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(fields => (Pts: double.Parse(fields[0], CultureInfo.InvariantCulture),
                Duration: fields.Length > 1 && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0))
            .OrderBy(packet => packet.Pts).ToArray();
        if (packets.Length == 0)
        {
            if (containerSeconds <= 0) throw new InvalidDataException("The video has no frames.");
            return new VideoEnd(TimeSpan.FromSeconds(containerSeconds), TimeSpan.FromSeconds(1 / 30d));
        }
        var gaps = packets.Zip(packets.Skip(1), (first, second) => second.Pts - first.Pts).Where(gap => gap > 0).ToArray();
        var frame = packets[^1].Duration > 0 ? packets[^1].Duration : gaps.Length > 0 ? gaps.Min() : 1 / 30d;
        return new VideoEnd(TimeSpan.FromSeconds(packets[^1].Pts + frame), TimeSpan.FromSeconds(frame));
    }

    public static bool TryParseRatio(string text, out double value)
    {
        value = 0;
        var parts = text.Split('/', ':');
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) || numerator <= 0 || denominator <= 0)
            return false;
        value = numerator / denominator;
        return true;
    }

    public static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
}
