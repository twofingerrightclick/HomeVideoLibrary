using System.Text;
using System.Xml;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The canonical form of text stored in MKV tags and chapters. Applied before writing and before comparing a
/// write with its read-back, because mkvtoolnix turns CRLF into LF and XML cannot carry most control characters.</summary>
public static class MetadataText
{
    /// <summary>Converts CRLF and CR to LF and removes characters XML 1.0 cannot represent. Whitespace-only text becomes
    /// empty, since mkvtoolnix drops such values on write.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], character))
            {
                builder.Append(character).Append(text[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>A single-line label: normalized, with line breaks folded to spaces and surrounding whitespace trimmed.</summary>
    public static string NormalizeTitle(string? text) => Normalize(text).Replace('\n', ' ').Trim();

    /// <summary>XML-safe <see cref="TagRules.Canonicalize"/>: Matroska KEYWORDS are comma-separated, so stored and typed
    /// values split the same way.</summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => TagRules.Canonicalize(tags.Select(Normalize));

    /// <summary>What a verified save leaves in the MKV, field for field, and so what the next read returns: normalized text,
    /// canonical tags, chapters in start order and <see cref="VideoMetadata.RecordedAtText"/> as written. Saving verifies
    /// the file against this, so a caller can adopt it as the saved state without reading the file again.</summary>
    public static VideoMetadata AsStored(VideoMetadata metadata)
    {
        var normalized = Normalize(metadata);
        return normalized with
        {
            Chapters = normalized.Chapters.OrderBy(chapter => chapter.Position).ToArray(),
            RecordedAtText = RecordedDate.ToTagText(normalized),
        };
    }

    public static VideoMetadata Normalize(VideoMetadata metadata) => metadata with
    {
        Title = NormalizeTitle(metadata.Title),
        Description = Normalize(metadata.Description),
        Tags = NormalizeTags(metadata.Tags),
        Chapters = metadata.Chapters.Select(chapter => chapter with
        {
            Title = NormalizeTitle(chapter.Title),
            Description = Normalize(chapter.Description),
            Tags = NormalizeTags(chapter.Tags),
        }).ToArray(),
    };
}
