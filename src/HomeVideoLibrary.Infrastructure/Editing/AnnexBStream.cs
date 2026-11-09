namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Reads NAL unit types from an Annex-B (start-code delimited) H.264 or HEVC stream.</summary>
internal static class AnnexBStream
{
    /// <summary>True when the first picture slice in <paramref name="data"/> belongs to an IDR picture, which no later
    /// picture can reference across.</summary>
    public static bool StartsWithIdr(ReadOnlySpan<byte> data, string codecName)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1) continue;
            var header = data[i + 3];
            if (codecName == "hevc")
            {
                var type = (header >> 1) & 0x3F;
                if (type <= 31) return type is 19 or 20;
            }
            else
            {
                var type = header & 0x1F;
                if (type is >= 1 and <= 5) return type == 5;
            }
            i += 2;
        }
        return false;
    }
}
