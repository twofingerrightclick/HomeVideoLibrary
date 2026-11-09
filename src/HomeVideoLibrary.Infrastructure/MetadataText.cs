using System.Text;
using System.Xml;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>The canonical form of text stored in MKV tags and chapters. Applied before writing and before comparing a
/// write with its read-back, because mkvtoolnix turns CRLF into LF and XML cannot carry most control characters.</summary>
public static class MetadataText
{
    /// <summary>Converts CRLF and CR to LF and removes characters XML 1.0 cannot represent.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
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

    /// <summary>Splits on commas and line breaks (Matroska KEYWORDS are comma-separated), trims, drops empties and
    /// removes case-insensitive duplicates, keeping the first spelling.</summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) => tags
        .SelectMany(tag => Normalize(tag).Split([',', '\n']))
        .Select(tag => tag.Trim())
        .Where(tag => tag.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

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
