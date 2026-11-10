using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>A track as <c>mkvmerge -J</c> reports it. Empty strings and zeros mean "not reported".</summary>
/// <param name="Properties">Every scalar property, as text, for copying the track header onto a re-muxed track.</param>
internal sealed record MkvTrack(int Id, string Type, string Codec, string CodecId, string CodecPrivate, string PixelDimensions,
    long DefaultDurationNanoseconds, double SamplingFrequency, int Channels, int BitsPerSample, string Language,
    IReadOnlyDictionary<string, string> Properties)
{
    private static readonly (string Property, string Option)[] HeaderOptions =
    [
        ("track_name", "--track-name"), ("display_dimensions", "--display-dimensions"), ("cropping", "--cropping"),
        ("stereo_mode", "--stereo-mode"), ("color_matrix_coefficients", "--colour-matrix-coefficients"),
        ("color_bits_per_channel", "--colour-bits-per-channel"), ("chroma_subsample", "--chroma-subsample"),
        ("cb_subsample", "--cb-subsample"), ("chroma_siting", "--chroma-siting"), ("color_range", "--colour-range"),
        ("color_transfer_characteristics", "--colour-transfer-characteristics"), ("color_primaries", "--colour-primaries"),
        ("max_content_light", "--max-content-light"), ("max_frame_light", "--max-frame-light"),
        ("chromaticity_coordinates", "--chromaticity-coordinates"), ("white_color_coordinates", "--white-colour-coordinates"),
        ("max_luminance", "--max-luminance"), ("min_luminance", "--min-luminance"),
    ];

    private static readonly (string Property, string Option)[] Flags =
        [("default_track", "--default-track-flag"), ("forced_track", "--forced-display-flag"), ("enabled_track", "--track-enabled-flag")];

    public string Name => Properties.GetValueOrDefault("track_name", "");
    public int IndexEntries => int.TryParse(Properties.GetValueOrDefault("num_index_entries"), out var entries) ? entries : 0;

    /// <summary>Codec, codec configuration and frame or sample format: what mkvmerge needs to be equal to append
    /// this track to another cleanly. Frame durations may differ; Matroska stores a timestamp per frame.</summary>
    public string AppendSignature => string.Join('|', Type, CodecId, CodecPrivate, PixelDimensions,
        SamplingFrequency.ToString(CultureInfo.InvariantCulture), Channels.ToString(CultureInfo.InvariantCulture),
        BitsPerSample.ToString(CultureInfo.InvariantCulture));

    /// <summary>The part of <see cref="AppendSignature"/> no muxer can reconcile; tracks that differ here are never appended.</summary>
    public string FormatSignature => string.Join('|', Type, CodecId, PixelDimensions,
        SamplingFrequency.ToString(CultureInfo.InvariantCulture), Channels.ToString(CultureInfo.InvariantCulture));

    public ExpectedTrack Expected => new(Type, Codec, Language);

    /// <summary>mkvmerge options that give track <paramref name="trackId"/> of a new input this track's header: language,
    /// name, flags, display size and the Matroska colour description, which raw elementary streams cannot carry.</summary>
    public IEnumerable<string> HeaderArguments(int trackId)
    {
        var id = trackId.ToString(CultureInfo.InvariantCulture);
        yield return "--language";
        yield return $"{id}:{Language}";
        foreach (var (property, option) in Flags)
            if (Properties.TryGetValue(property, out var flag))
            {
                yield return option;
                yield return $"{id}:{(flag == "true" ? 1 : 0)}";
            }
        foreach (var (property, option) in HeaderOptions)
            if (Properties.TryGetValue(property, out var value) && value.Length > 0)
            {
                yield return option;
                yield return $"{id}:{value}";
            }
    }
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

    public IReadOnlyList<MkvAttachment> ForeignAttachments => Attachments.Where(attachment => !AppAttachmentIds.Contains(attachment.Id)).ToArray();

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
                attachment.GetProperty("id").GetInt32(),
                attachment.TryGetProperty("file_name", out var name) ? name.GetString() ?? "" : "")).ToArray()
            : [];
        return new MkvInfo(duration, tracks, attachments);
    }

    private static MkvTrack ParseTrack(JsonElement track)
    {
        var properties = new Dictionary<string, string>();
        if (track.TryGetProperty("properties", out var element) && element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                properties[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => "",
                };
        string Text(string name) => properties.GetValueOrDefault(name, "");
        long Number(string name) => long.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
        return new MkvTrack(track.GetProperty("id").GetInt32(), track.GetProperty("type").GetString() ?? "", track.GetProperty("codec").GetString() ?? "",
            Text("codec_id"), Text("codec_private_data"), Text("pixel_dimensions"), Number("default_duration"),
            double.TryParse(Text("audio_sampling_frequency"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hertz) ? hertz : 0,
            (int)Number("audio_channels"), (int)Number("audio_bits_per_sample"), Text("language") is { Length: > 0 } language ? language : "und",
            properties);
    }
}

/// <summary>The primary video stream as ffprobe reports it.</summary>
internal sealed record VideoStreamInfo(string CodecName, string Profile, int Level, string PixelFormat, string FieldOrder,
    string ColorRange, string ColorSpace, string ColorPrimaries, string ColorTransfer, string SampleAspectRatio,
    string AverageFrameRate, int Width, int Height, int BFrames = 0)
{
    public bool IsProgressive => FieldOrder == "progressive";
}

internal sealed record VideoPacket(TimeSpan Pts, bool IsKey);

/// <summary>A file's timeline. All edit positions are absolute timestamps, as Matroska blocks, chapters, mkvmerge and
/// ffprobe's read intervals use them; ffmpeg's input seek is relative to <see cref="StartTime"/>, so every ffmpeg seek
/// goes through <see cref="Seek"/>.</summary>
/// <param name="ContainerEnd">Where the longest track ends.</param>
/// <param name="VideoEnd">Where the primary video's last frame ends.</param>
/// <param name="AudioEnd">Where the longest audio track ends, or null without audio.</param>
/// <param name="PacketEnd">Where the last packet of any track ends; well short of <paramref name="ContainerEnd"/> means
/// the file was cut off after its header was written.</param>
internal sealed record MediaTimeline(TimeSpan StartTime, TimeSpan ContainerEnd, TimeSpan VideoEnd, TimeSpan FrameDuration, TimeSpan? AudioEnd,
    TimeSpan PacketEnd)
{
    public string Seek(TimeSpan absolute) => MediaProbe.Seconds(absolute > StartTime ? absolute - StartTime : TimeSpan.Zero);

    /// <summary>How long the file claims to be, from its first timestamp. Matroska muxers disagree on the header
    /// duration of a file that starts late (ffmpeg writes the end timestamp, mkvmerge the length), so the larger of the
    /// two readings and the last packet's end is taken: a file cut off after its header was written still claims its
    /// full length.</summary>
    public TimeSpan ClaimedLength
    {
        get
        {
            var headerEnd = ContainerEnd - StartTime;
            return (PacketEnd > headerEnd ? PacketEnd : headerEnd) - StartTime;
        }
    }
}

/// <param name="Timeline">Absolute times; see <see cref="MediaTimeline"/>.</param>
internal sealed record EditSource(string Path, MkvInfo Mkv, VideoStreamInfo Video, MediaTimeline Timeline)
{
    private static readonly TimeSpan MinimumLength = TimeSpan.FromMilliseconds(100);

    public MkvTrack VideoTrack => Mkv.Tracks.First(track => track.Type == "video");
    public IReadOnlyList<MkvTrack> AudioTracks => Mkv.Tracks.Where(track => track.Type == "audio").ToArray();
    public TimeSpan VideoEnd => Timeline.VideoEnd;
    public TimeSpan FrameDuration => VideoTrack.DefaultDurationNanoseconds > 0
        ? TimeSpan.FromTicks(VideoTrack.DefaultDurationNanoseconds / 100) : Timeline.FrameDuration;

    /// <summary>The one rule for editable ranges: positions inside the video, where any end at or past the video's last
    /// frame means "to the end". Catalog and container durations include audio that outlasts the picture, so a
    /// whole-video range typed from them still resolves to the video's end.</summary>
    public (TimeSpan Start, TimeSpan End) ResolveRange(TimeSpan start, TimeSpan end)
    {
        var resolved = end >= VideoEnd - CutPlanner.Epsilon ? VideoEnd : end;
        if (start < TimeSpan.Zero || resolved <= start || resolved - start < MinimumLength)
            throw new ArgumentOutOfRangeException(nameof(start), "Choose an ordered range inside the video, at least 0.1 seconds long.");
        return (start, resolved);
    }
}

/// <summary>Reads what editing needs to know about library MKVs. Source probes are cached per file content.</summary>
internal sealed class MediaProbe(MediaTools tools, MediaProcess process)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EndScan = TimeSpan.FromSeconds(10);
    private const int IdentityChunk = 64 * 1024;
    private const int MaxCachedSources = 64;
    private readonly ConcurrentDictionary<string, Task<EditSource>> _sources = new();

    public async Task<EditSource> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var key = await IdentityAsync(path, cancellationToken);
        // Probes take well under a second; letting a cancelled caller's probe finish keeps the shared cache entry usable.
        if (_sources.Count >= MaxCachedSources) _sources.Clear();
        var task = _sources.GetOrAdd(key, _ => ProbeUncachedAsync(Path.GetFullPath(path), CancellationToken.None));
        try { return await task.WaitAsync(cancellationToken); }
        catch (Exception) when (task.IsFaulted)
        {
            _sources.TryRemove(new KeyValuePair<string, Task<EditSource>>(key, task));
            throw;
        }
    }

    public Task<MediaTimeline> ReadTimelineAsync(string path, CancellationToken cancellationToken) =>
        ReadTimelineAsync(process, tools, path, cancellationToken);

    /// <summary>Path, size, write time and the first and last 64 KiB: write times are coarse on some file systems.</summary>
    private static async Task<string> IdentityAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The video file is missing from the library.", path);
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[IdentityChunk];
        hash.AppendData(buffer, 0, await stream.ReadAtLeastAsync(buffer, IdentityChunk, throwOnEndOfStream: false, cancellationToken));
        stream.Seek(Math.Max(0, stream.Length - IdentityChunk), SeekOrigin.Begin);
        hash.AppendData(buffer, 0, await stream.ReadAtLeastAsync(buffer, IdentityChunk, throwOnEndOfStream: false, cancellationToken));
        return string.Join('|', file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, Convert.ToHexString(hash.GetHashAndReset()));
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
        return new EditSource(path, mkv, video, await ReadTimelineAsync(path, cancellationToken));
    }

    private async Task<VideoStreamInfo> ReadVideoStreamAsync(string path, CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-show_entries",
            "stream=codec_name,profile,level,pix_fmt,field_order,color_range,color_space,color_primaries,color_transfer,sample_aspect_ratio,avg_frame_rate,width,height,has_b_frames",
            "-of", "json", path], ProbeTimeout, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("The video is unreadable. " + MediaErrors.Summarize(result.StandardError));
        using var document = JsonDocument.Parse(result.StandardOutput);
        var stream = document.RootElement.TryGetProperty("streams", out var streams) ? streams.EnumerateArray().FirstOrDefault() : default;
        if (stream.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Editing requires a video stream.");
        string Text(string name) => stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        int Number(string name) => stream.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
        return new VideoStreamInfo(Text("codec_name"), Text("profile"), Number("level"), Text("pix_fmt"), Text("field_order"),
            Text("color_range"), Text("color_space"), Text("color_primaries"), Text("color_transfer"), Text("sample_aspect_ratio"),
            Text("avg_frame_rate"), Number("width"), Number("height"), Number("has_b_frames"));
    }

    /// <summary>Video packets of the first video stream with absolute presentation times in about [<paramref name="from"/>,
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
            if (fields.Length >= 2 && TryParseSeconds(fields[0], out var pts))
                packets.Add(new VideoPacket(pts, fields[1].StartsWith('K')));
        });
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-read_intervals", interval,
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path], ProbeTimeout, cancellationToken, lines.Append);
        lines.Append("\n");
        if (result.ExitCode != 0) throw new InvalidDataException("The video's frames could not be read. " + MediaErrors.Summarize(result.StandardError));
        return packets.OrderBy(packet => packet.Pts).ToArray();
    }

    /// <summary>The bytes of the first video packet at or after the keyframe at or before <paramref name="at"/>, read
    /// with ffprobe's absolute read interval, or null when there is none.</summary>
    public async Task<(TimeSpan Pts, byte[] Data)?> ReadPacketAsync(string path, TimeSpan at, CancellationToken cancellationToken)
    {
        var result = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-select_streams", "V:0", "-read_intervals", Seconds(at) + "%+#1",
            "-show_entries", "packet=pts_time,data", "-show_data", "-of", "json", path], ProbeTimeout, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException("The video's frames could not be read. " + MediaErrors.Summarize(result.StandardError));
        using var json = JsonDocument.Parse(result.StandardOutput);
        if (!json.RootElement.TryGetProperty("packets", out var packets) || packets.GetArrayLength() == 0) return null;
        var packet = packets[0];
        if (!packet.TryGetProperty("pts_time", out var ptsText) || !TryParseSeconds(ptsText.GetString(), out var pts)) return null;
        // ffprobe's hexdump: "offset: hhhh hhhh ... ascii", 16 bytes per line.
        var hex = string.Concat((packet.TryGetProperty("data", out var data) ? data.GetString() ?? "" : "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.IndexOf(": ", StringComparison.Ordinal) is var colon and >= 0 && line.Length > colon + 2
                ? line.Substring(colon + 2, Math.Min(39, line.Length - colon - 2)).Replace(" ", "", StringComparison.Ordinal) : ""));
        return (pts, Convert.FromHexString(hex));
    }

    /// <summary>Reads start time and per-track ends from the packets in the last ten seconds (every packet when the
    /// container has no duration).</summary>
    public static async Task<MediaTimeline> ReadTimelineAsync(MediaProcess process, MediaTools tools, string path, CancellationToken cancellationToken)
    {
        var info = await process.RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries",
            "format=start_time,duration:stream=index,codec_type:stream_disposition=attached_pic", "-of", "json", path], ProbeTimeout, cancellationToken);
        if (info.ExitCode != 0) throw new InvalidDataException("The video is unreadable. " + MediaErrors.Summarize(info.StandardError));
        TimeSpan start = TimeSpan.Zero, duration = TimeSpan.Zero;
        var types = new Dictionary<int, string>();
        using (var json = JsonDocument.Parse(info.StandardOutput))
        {
            var root = json.RootElement;
            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("start_time", out var value) && TryParseSeconds(value.GetString(), out var parsed)) start = parsed;
                if (format.TryGetProperty("duration", out value) && TryParseSeconds(value.GetString(), out parsed)) duration = parsed;
            }
            if (root.TryGetProperty("streams", out var streams))
                foreach (var stream in streams.EnumerateArray())
                    if (!(stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var attached) && attached.GetInt32() == 1))
                        types[stream.GetProperty("index").GetInt32()] = stream.TryGetProperty("codec_type", out var type) ? type.GetString() ?? "" : "";
        }
        var videoIndex = types.Where(pair => pair.Value == "video").Select(pair => (int?)pair.Key).Min()
            ?? throw new InvalidDataException("The file has no video stream.");
        var ends = new Dictionary<int, TimeSpan>();
        var videoPts = new List<TimeSpan>();
        var lastVideoDuration = TimeSpan.Zero;
        var lines = new LineSplitter(line =>
        {
            var fields = line.Split(',');
            if (fields.Length < 3 || !int.TryParse(fields[0], out var index) || !TryParseSeconds(fields[1], out var pts)) return;
            TryParseSeconds(fields[2], out var packetDuration);
            ends[index] = ends.TryGetValue(index, out var end) && end > pts + packetDuration ? end : pts + packetDuration;
            if (index != videoIndex) return;
            videoPts.Add(pts);
            if (videoPts.Count > 512) videoPts.RemoveRange(0, 256);
            if (pts >= videoPts.Max()) lastVideoDuration = packetDuration;
        });
        var arguments = new List<string> { "-v", "error" };
        if (duration > EndScan) arguments.AddRange(["-read_intervals", Seconds(start + duration - EndScan) + "%"]);
        arguments.AddRange(["-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "csv=p=0", path]);
        var result = await process.RunAsync(tools.Ffprobe, arguments, ProbeTimeout, cancellationToken, lines.Append);
        lines.Append("\n");
        if (result.ExitCode != 0) throw new InvalidDataException("The video's frames could not be read. " + MediaErrors.Summarize(result.StandardError));
        if (videoPts.Count == 0) throw new InvalidDataException("The video's last frames are missing; the file may be truncated.");
        var sorted = videoPts.Order().ToArray();
        var gaps = sorted.Zip(sorted.Skip(1), (first, second) => second - first).Where(gap => gap > TimeSpan.Zero).ToArray();
        var frame = lastVideoDuration > TimeSpan.Zero ? lastVideoDuration : gaps.Length > 0 ? gaps.Min() : TimeSpan.FromSeconds(1 / 30d);
        var videoEnd = sorted[^1] + frame;
        var audioEnds = ends.Where(pair => types.GetValueOrDefault(pair.Key) == "audio").Select(pair => (TimeSpan?)pair.Value).Max();
        var containerEnd = duration > TimeSpan.Zero ? start + duration : ends.Values.Append(videoEnd).Max();
        var finalVideoEnd = ends[videoIndex] > videoEnd ? ends[videoIndex] : videoEnd;
        return new MediaTimeline(start, containerEnd, finalVideoEnd, frame, audioEnds, ends.Values.Append(finalVideoEnd).Max());
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

    private static bool TryParseSeconds(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds)) return false;
        value = TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
        return true;
    }
}
