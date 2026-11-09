namespace HomeVideoLibrary.Infrastructure.Editing;

/// <summary>Reads NAL unit types from a Matroska H.264 or HEVC packet (length-prefixed NAL units).</summary>
internal static class NalUnits
{
    /// <summary>The NAL length size declared by an avcC or hvcC CodecPrivate (hex), or 4 when it cannot be read.</summary>
    public static int LengthSize(string codecPrivateHex, string codecName)
    {
        var offset = codecName == "hevc" ? 21 : 4;
        return codecPrivateHex.Length >= (offset + 1) * 2
            ? (Convert.ToByte(codecPrivateHex.Substring(offset * 2, 2), 16) & 3) + 1 : 4;
    }

    /// <summary>True when the first picture slice in <paramref name="packet"/> belongs to an IDR picture, which no later
    /// picture can reference across.</summary>
    public static bool StartsWithIdr(ReadOnlySpan<byte> packet, int lengthSize, string codecName)
    {
        var position = 0;
        while (position + lengthSize < packet.Length)
        {
            var length = 0;
            for (var i = 0; i < lengthSize; i++) length = (length << 8) | packet[position + i];
            var header = packet[position + lengthSize];
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
            if (length <= 0) return false;
            position += lengthSize + length;
        }
        return false;
    }
}
