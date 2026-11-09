using System.Globalization;
using System.Xml.Linq;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

internal static class MatroskaMetadataXml
{
    private static readonly string[] OwnedTags = ["TITLE", "DESCRIPTION", "KEYWORDS", "DATE_RECORDED"];

    public static VideoMetadata Read(XDocument tags, XDocument chapters)
    {
        var global = tags.Root?.Elements("Tag").Where(IsGlobal).ToArray() ?? [];
        var edition = PrimaryEdition(chapters);
        var chapterModels = edition?.Elements("ChapterAtom").Select(atom =>
        {
            var uid = ulong.Parse(atom.Element("ChapterUID")!.Value, CultureInfo.InvariantCulture);
            var chapterTags = tags.Root?.Elements("Tag").Where(tag => tag.Element("Targets")?.Element("ChapterUID")?.Value == uid.ToString(CultureInfo.InvariantCulture)).ToArray() ?? [];
            return new VideoChapter(uid, ParseTime(atom.Element("ChapterTimeStart")!.Value),
                atom.Element("ChapterDisplay")?.Element("ChapterString")?.Value ?? "",
                Value(chapterTags, "DESCRIPTION"), Values(chapterTags, "KEYWORDS"));
        }).OrderBy(chapter => chapter.Position).ToArray() ?? [];
        var date = Value(global, "DATE_RECORDED");
        return new VideoMetadata(Value(global, "TITLE"), Value(global, "DESCRIPTION"), Values(global, "KEYWORDS"),
            DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var recorded) ? recorded : null,
            chapterModels, []);
    }

    public static void Update(XDocument tags, XDocument chapters, VideoMetadata metadata)
    {
        if (tags.Root is null) tags.Add(new XElement("Tags"));
        if (chapters.Root is null) chapters.Add(new XElement("Chapters"));
        var global = tags.Root!.Elements("Tag").Where(IsGlobal).ToArray();
        foreach (var tag in global) RemoveOwned(tag, OwnedTags);
        var target = global.FirstOrDefault();
        if (target is null)
        {
            target = new XElement("Tag", new XElement("Targets"));
            tags.Root.Add(target);
        }
        Add(target, "TITLE", metadata.Title);
        Add(target, "DESCRIPTION", metadata.Description);
        foreach (var keyword in metadata.Tags) Add(target, "KEYWORDS", keyword);
        if (metadata.RecordedAt is { } recorded) Add(target, "DATE_RECORDED", recorded.ToString("O"));

        var edition = PrimaryEdition(chapters);
        if (edition is null)
        {
            edition = new XElement("EditionEntry", new XElement("EditionFlagDefault", "1"));
            chapters.Root!.Add(edition);
        }
        var existing = edition.Elements("ChapterAtom").ToDictionary(atom => ulong.Parse(atom.Element("ChapterUID")!.Value, CultureInfo.InvariantCulture));
        var reservedUids = chapters.Descendants("ChapterUID").Select(element => ulong.Parse(element.Value, CultureInfo.InvariantCulture)).ToHashSet();
        if (metadata.Chapters.Any(chapter => !existing.ContainsKey(chapter.Uid) && reservedUids.Contains(chapter.Uid)))
            throw new InvalidOperationException("A chapter UID is already used by another edition or nested chapter.");
        var desired = metadata.Chapters.Select(chapter => chapter.Uid).ToHashSet();
        foreach (var removed in existing.Where(pair => !desired.Contains(pair.Key)))
        {
            // A parent's removal must not silently discard nested external chapters.
            if (removed.Value.Elements("ChapterAtom").Any())
                throw new InvalidOperationException("This chapter contains nested external chapters. Preserve it when editing this file.");
            removed.Value.Remove();
            foreach (var tag in ChapterTags(tags, removed.Key).ToArray()) tag.Remove();
        }
        foreach (var chapter in metadata.Chapters.OrderBy(chapter => chapter.Position))
        {
            if (!existing.TryGetValue(chapter.Uid, out var atom))
                atom = new XElement("ChapterAtom", new XElement("ChapterUID", chapter.Uid));
            atom.SetElementValue("ChapterTimeStart", FormatTime(chapter.Position));
            // Existing ChapterTimeEnd must remain consistent with an edited start.
            if (atom.Element("ChapterTimeEnd") is { } end && ParseTime(end.Value) < chapter.Position)
                atom.Element("ChapterTimeEnd")!.Remove();
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
            foreach (var tag in chapterTags) RemoveOwned(tag, ["DESCRIPTION", "KEYWORDS"]);
            var chapterTarget = chapterTags.FirstOrDefault();
            if (chapterTarget is null)
            {
                chapterTarget = new XElement("Tag", new XElement("Targets", new XElement("ChapterUID", chapter.Uid)));
                tags.Root.Add(chapterTarget);
            }
            Add(chapterTarget, "DESCRIPTION", chapter.Description);
            foreach (var keyword in chapter.Tags) Add(chapterTarget, "KEYWORDS", keyword);
        }
        foreach (var empty in tags.Root.Elements("Tag").Where(tag => !tag.Elements("Simple").Any()).ToArray()) empty.Remove();
    }

    private static XElement? PrimaryEdition(XDocument chapters) => chapters.Root?.Elements("EditionEntry")
        .OrderByDescending(edition => edition.Element("EditionFlagDefault")?.Value == "1").FirstOrDefault();
    private static IEnumerable<XElement> ChapterTags(XDocument tags, ulong uid) => tags.Root!.Elements("Tag")
        .Where(tag => tag.Element("Targets")?.Element("ChapterUID")?.Value == uid.ToString(CultureInfo.InvariantCulture));
    private static bool IsGlobal(XElement tag) => tag.Element("Targets")?.Elements()
        .Any(element => element.Name.LocalName.EndsWith("UID", StringComparison.Ordinal)) != true;
    private static string Value(IEnumerable<XElement> tags, string name) => Values(tags, name).FirstOrDefault() ?? "";
    private static string[] Values(IEnumerable<XElement> tags, string name) => tags.SelectMany(tag => tag.Elements("Simple"))
        .Where(simple => simple.Element("Name")?.Value == name).Select(simple => simple.Element("String")?.Value ?? "").Where(value => value.Length > 0).ToArray();
    private static void RemoveOwned(XElement tag, string[] names)
    {
        foreach (var simple in tag.Elements("Simple").Where(simple => names.Contains(simple.Element("Name")?.Value)).ToArray()) simple.Remove();
    }
    private static void Add(XElement tag, string name, string value)
    {
        if (value.Length > 0) tag.Add(new XElement("Simple", new XElement("Name", name), new XElement("String", value)));
    }
    private static TimeSpan ParseTime(string value)
    {
        var parts = value.Split(':');
        return TimeSpan.FromHours(double.Parse(parts[0], CultureInfo.InvariantCulture)) +
            TimeSpan.FromMinutes(double.Parse(parts[1], CultureInfo.InvariantCulture)) +
            TimeSpan.FromSeconds(double.Parse(parts[2], CultureInfo.InvariantCulture));
    }
    private static string FormatTime(TimeSpan value) => string.Create(CultureInfo.InvariantCulture,
        $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Ticks % TimeSpan.TicksPerSecond:0000000}00");
}
