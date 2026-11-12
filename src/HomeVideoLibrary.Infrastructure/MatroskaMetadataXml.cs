using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

internal static class MatroskaMetadataXml
{
    private static readonly string[] LanguageElements = ["TagLanguage", "TagLanguageIETF", "TagLanguageBCP47"];

    /// <summary>Reads the video-level tags and the primary edition's chapters. Malformed or duplicate chapter atoms are
    /// skipped with a warning; <see cref="Update"/> leaves them in place.</summary>
    public static VideoMetadata Read(XDocument tags, XDocument chapters, ICollection<string>? warnings = null)
    {
        var global = tags.Root?.Elements("Tag").Where(IsVideoLevel).ToArray() ?? [];
        var chapterModels = new List<VideoChapter>();
        var seen = new HashSet<ulong>();
        foreach (var atom in PrimaryEdition(chapters)?.Elements("ChapterAtom") ?? [])
        {
            if (TryGetUid(atom) is not { } uid || !TryParseTime(atom.Element("ChapterTimeStart")?.Value, out var start))
            {
                warnings?.Add("A chapter without a valid ID or start time was skipped.");
                continue;
            }
            if (!seen.Add(uid))
            {
                warnings?.Add($"A duplicate of chapter {uid} was skipped.");
                continue;
            }
            var chapterTags = ChapterTags(tags, uid).ToArray();
            chapterModels.Add(new VideoChapter(uid, start, ChapterTitle(atom),
                PreferredValue(chapterTags, "DESCRIPTION"), Keywords(chapterTags))
            { End = TryParseTime(atom.Element("ChapterTimeEnd")?.Value, out var end) ? end : start });
        }
        var date = PreferredValue(global, "DATE_RECORDED");
        return new VideoMetadata(PreferredValue(global, "TITLE"), PreferredValue(global, "DESCRIPTION"), Keywords(global),
            RecordedDate.TryParse(date, out var recorded) ? recorded : null,
            chapterModels.OrderBy(chapter => chapter.Position).ToArray(), [])
        {
            RecordedAtText = date,
        };
    }

    /// <summary>Applies normalized <paramref name="metadata"/> to the extracted XML, preserving everything the app does not own:
    /// other target levels, language variants, foreign editions, nested and malformed chapter atoms.</summary>
    public static void Update(XDocument tags, XDocument chapters, VideoMetadata metadata,
        IReadOnlyDictionary<string, string>? extraGlobalTags = null)
    {
        if (tags.Root is null) tags.Add(new XElement("Tags"));
        if (chapters.Root is null) chapters.Add(new XElement("Chapters"));
        var global = tags.Root!.Elements("Tag").Where(IsVideoLevel).ToArray();
        var target = global.FirstOrDefault();
        if (target is null)
        {
            target = new XElement("Tag", new XElement("Targets", new XElement("TargetTypeValue", "50"), new XElement("TargetType", "MOVIE")));
            tags.Root.Add(target);
            global = [target];
        }
        SetLocalized(global, target, "TITLE", metadata.Title);
        SetLocalized(global, target, "DESCRIPTION", metadata.Description);
        Replace(global, target, "KEYWORDS", metadata.Tags);
        Replace(global, target, "DATE_RECORDED", [RecordedDate.ToTagText(metadata)]);
        foreach (var (name, value) in extraGlobalTags ?? new Dictionary<string, string>())
            Replace(global, target, name, [value]);
        UpdateChapters(tags, chapters, metadata.Chapters);
        foreach (var empty in tags.Root.Elements("Tag").Where(tag => !tag.Elements("Simple").Any()).ToArray()) empty.Remove();
    }

    /// <summary>Whether a video-level Simple tag named <paramref name="name"/> exists, even with an empty value.</summary>
    public static bool HasGlobal(XDocument tags, string name) =>
        Simples(tags.Root?.Elements("Tag").Where(IsVideoLevel) ?? [], name).Any();

    /// <summary>Appends <paramref name="values"/> as video-level Simple tags, to the first video-level Tag (created when
    /// there is none), leaving every existing element as it is.</summary>
    public static void AddGlobal(XDocument tags, IReadOnlyDictionary<string, string> values)
    {
        if (tags.Root is null) tags.Add(new XElement("Tags"));
        var target = tags.Root!.Elements("Tag").FirstOrDefault(IsVideoLevel);
        if (target is null)
        {
            target = new XElement("Tag", new XElement("Targets", new XElement("TargetTypeValue", "50"), new XElement("TargetType", "MOVIE")));
            tags.Root.Add(target);
        }
        foreach (var (name, value) in values) target.Add(NewSimple(name, value));
    }

    /// <summary>All values of a video-level Simple tag, in document order.</summary>
    public static IReadOnlyList<string> GlobalValues(XDocument tags, string name) =>
        Values(tags.Root?.Elements("Tag").Where(IsVideoLevel) ?? [], name);

    private static void UpdateChapters(XDocument tags, XDocument chapters, IReadOnlyList<VideoChapter> desiredChapters)
    {
        var edition = PrimaryEdition(chapters);
        if (edition is null)
        {
            edition = new XElement("EditionEntry", new XElement("EditionFlagDefault", "1"));
            chapters.Root!.Add(edition);
        }
        if (edition.Element("EditionFlagOrdered")?.Value == "1")
        {
            // Ordered chapters define playback order through mandatory ends; rewriting them would change what plays.
            if (SameChapters(MetadataText.Normalize(Read(tags, chapters)).Chapters, desiredChapters)) return;
            throw new InvalidOperationException("This video uses ordered chapters from another program, which cannot be edited here.");
        }
        var existing = new Dictionary<ulong, XElement>();
        foreach (var atom in edition.Elements("ChapterAtom").ToArray())
        {
            if (TryGetUid(atom) is not { } uid || !TryParseTime(atom.Element("ChapterTimeStart")?.Value, out _)) continue;
            // Read showed only the first atom with this UID; stale duplicates would shadow the edit on the next read.
            if (existing.TryAdd(uid, atom)) continue;
            Trace.TraceWarning($"Removed a duplicate of chapter {uid} (\"{ChapterTitle(atom)}\") that readers ignore.");
            atom.Remove();
        }
        var reservedUids = chapters.Descendants("ChapterUID")
            .Select(element => ulong.TryParse(element.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : 0)
            .ToHashSet();
        if (desiredChapters.FirstOrDefault(chapter => !existing.ContainsKey(chapter.Uid) && reservedUids.Contains(chapter.Uid)) is { } clash)
            throw new InvalidOperationException($"Chapter \"{clash.Title}\" reuses an ID that another edition or nested chapter already has.");
        var desired = desiredChapters.Select(chapter => chapter.Uid).ToHashSet();
        foreach (var (uid, atom) in existing.Where(pair => !desired.Contains(pair.Key)))
        {
            // A parent's removal must not silently discard nested external chapters.
            if (atom.Elements("ChapterAtom").Any())
                throw new InvalidOperationException($"Chapter \"{ChapterTitle(atom)}\" contains nested chapters from another program, so it cannot be removed here.");
            atom.Remove();
            foreach (var tag in ChapterTags(tags, uid).ToArray()) tag.Remove();
        }
        foreach (var chapter in desiredChapters.OrderBy(chapter => chapter.Position))
        {
            if (!existing.TryGetValue(chapter.Uid, out var atom))
                atom = new XElement("ChapterAtom", new XElement("ChapterUID", chapter.Uid));
            atom.SetElementValue("ChapterTimeStart", FormatTime(chapter.Position));
            atom.SetElementValue("ChapterTimeEnd", FormatTime(chapter.End));
            var display = atom.Element("ChapterDisplay");
            if (display is null)
            {
                display = new XElement("ChapterDisplay", new XElement("ChapterLanguage", "und"));
                atom.Add(display);
            }
            display.SetElementValue("ChapterString", chapter.Title);
            if (atom.Parent is not null) atom.Remove();
            edition.Add(atom);
            var chapterTags = ChapterTags(tags, chapter.Uid).ToArray();
            var chapterTarget = chapterTags.FirstOrDefault();
            if (chapterTarget is null)
            {
                chapterTarget = new XElement("Tag", new XElement("Targets", new XElement("TargetTypeValue", "30"),
                    new XElement("TargetType", "CHAPTER"), new XElement("ChapterUID", chapter.Uid)));
                tags.Root!.Add(chapterTarget);
                chapterTags = [chapterTarget];
            }
            SetLocalized(chapterTags, chapterTarget, "DESCRIPTION", chapter.Description);
            Replace(chapterTags, chapterTarget, "KEYWORDS", chapter.Tags);
        }
    }

    internal static bool SameChapters(IReadOnlyList<VideoChapter> left, IReadOnlyList<VideoChapter> right) =>
        left.Count == right.Count && left.OrderBy(chapter => chapter.Uid).Zip(right.OrderBy(chapter => chapter.Uid)).All(pair =>
            pair.First.Uid == pair.Second.Uid && pair.First.Position == pair.Second.Position && pair.First.End == pair.Second.End && pair.First.Title == pair.Second.Title &&
            pair.First.Description == pair.Second.Description && pair.First.Tags.SequenceEqual(pair.Second.Tags));

    private static XElement? PrimaryEdition(XDocument chapters) => chapters.Root?.Elements("EditionEntry")
        .OrderByDescending(edition => edition.Element("EditionFlagDefault")?.Value == "1").FirstOrDefault();

    private static IEnumerable<XElement> ChapterTags(XDocument tags, ulong uid) => (tags.Root?.Elements("Tag") ?? [])
        .Where(tag =>
        {
            var targets = tag.Element("Targets")?.Elements().Where(IsUidTarget).ToArray() ?? [];
            // Multi-target tags also describe foreign tracks/chapters; leave their complete XML alone.
            return targets.Length == 1 && targets[0].Name.LocalName == "ChapterUID" &&
                targets[0].Value == uid.ToString(CultureInfo.InvariantCulture);
        });

    private static bool IsVideoLevel(XElement tag)
    {
        var targets = tag.Element("Targets");
        if (targets?.Elements().Any(IsUidTarget) == true) return false;
        // RFC 9559 defaults an omitted logical level to 50. Other levels, and explicitly
        // named audio/foreign targets, must retain their independent descriptions and titles.
        var level = targets?.Element("TargetTypeValue")?.Value;
        var type = targets?.Element("TargetType")?.Value;
        return (level is null || level == "50") && (type is null || type is "MOVIE" or "EPISODE" or "CONCERT");
    }

    private static bool IsUidTarget(XElement element) => element.Name.LocalName.EndsWith("UID", StringComparison.Ordinal);

    private static ulong? TryGetUid(XElement atom) =>
        ulong.TryParse(atom.Element("ChapterUID")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid != 0 ? uid : null;

    private static string ChapterTitle(XElement atom) => atom.Element("ChapterDisplay")?.Element("ChapterString")?.Value ?? "";

    private static IEnumerable<XElement> Simples(IEnumerable<XElement> tags, string name) =>
        tags.SelectMany(tag => tag.Elements("Simple")).Where(simple => simple.Element("Name")?.Value == name);

    private static bool IsLanguageNeutral(XElement simple) => LanguageElements
        .Select(name => simple.Element(name)?.Value).All(language => language is null or "" or "und");

    private static string StringOf(XElement simple) => simple.Element("String")?.Value ?? "";

    /// <summary>The first language-neutral value, else the first value in any language.</summary>
    private static string PreferredValue(IEnumerable<XElement> tags, string name)
    {
        var candidates = Simples(tags, name).Where(simple => StringOf(simple).Length > 0).ToArray();
        return StringOf(candidates.FirstOrDefault(IsLanguageNeutral) ?? candidates.FirstOrDefault() ?? new XElement("Simple"));
    }

    private static string[] Values(IEnumerable<XElement> tags, string name) =>
        Simples(tags, name).Select(StringOf).Where(value => value.Length > 0).ToArray();

    private static IReadOnlyList<string> Keywords(IEnumerable<XElement> tags) => MetadataText.NormalizeTags(Values(tags, "KEYWORDS"));

    /// <summary>Updates the language-neutral value in place, keeping translations. An unchanged value leaves the XML alone;
    /// clearing removes every variant, since the reader would otherwise surface a translation in its place.</summary>
    private static void SetLocalized(IReadOnlyList<XElement> tags, XElement target, string name, string value)
    {
        if (PreferredValue(tags, name) == value) return;
        var all = Simples(tags, name).ToArray();
        if (value.Length == 0)
        {
            foreach (var simple in all) simple.Remove();
            return;
        }
        var neutral = all.Where(IsLanguageNeutral).ToArray();
        foreach (var duplicate in neutral.Skip(1)) duplicate.Remove();
        if (neutral.FirstOrDefault() is { } first) first.SetElementValue("String", value);
        else target.Add(NewSimple(name, value));
    }

    private static void Replace(IReadOnlyList<XElement> tags, XElement target, string name, IEnumerable<string> values)
    {
        foreach (var simple in Simples(tags, name).ToArray()) simple.Remove();
        foreach (var value in values.Where(value => value.Length > 0)) target.Add(NewSimple(name, value));
    }

    private static XElement NewSimple(string name, string value) => new("Simple", new XElement("Name", name), new XElement("String", value));

    /// <summary>Parses <c>HH:MM:SS.nnnnnnnnn</c> exactly with integer arithmetic; nanoseconds below 100 are truncated.</summary>
    internal static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var parts = value?.Trim().Split(':');
        if (parts is not { Length: 3 }) return false;
        var secondParts = parts[2].Split('.');
        if (secondParts.Length > 2 || !TryDigits(parts[0], 10, out var hours) || !TryDigits(parts[1], 2, out var minutes) ||
            !TryDigits(secondParts[0], 2, out var seconds) || minutes >= 60 || seconds >= 60)
            return false;
        long nanoseconds = 0;
        if (secondParts.Length == 2)
        {
            var fraction = secondParts[1];
            if (fraction.Length is 0 or > 9 || !TryDigits(fraction.PadRight(9, '0'), 9, out nanoseconds)) return false;
        }
        if (hours > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerHour - 1) return false;
        time = TimeSpan.FromTicks(hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute +
            seconds * TimeSpan.TicksPerSecond + nanoseconds / 100);
        return true;
    }

    private static bool TryDigits(string text, int maxLength, out long value)
    {
        value = 0;
        return text.Length > 0 && text.Length <= maxLength && text.All(char.IsAsciiDigit) &&
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    internal static string FormatTime(TimeSpan value) => string.Create(CultureInfo.InvariantCulture,
        $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Ticks % TimeSpan.TicksPerSecond:0000000}00");
}
