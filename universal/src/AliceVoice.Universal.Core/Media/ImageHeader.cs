using System.Buffers.Binary;

namespace AliceVoice.Universal.Media;

/// <summary>
/// Reads width x height from the first bytes of a PNG, JPEG, GIF or WebP. A buffer
/// that is none of these is not shown: the overlay never paints bytes it cannot name.
/// </summary>
public static class ImageHeader
{
    public static bool TryRead(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(b.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(b.Slice(20, 4));
            return width > 0 && height > 0;
        }
        if (b.Length >= 10 && b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F')
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(8, 2));
            return width > 0 && height > 0;
        }
        if (b.Length >= 30 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F'
            && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P')
            return TryWebP(b, out width, out height);
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            return TryJpeg(b, out width, out height);
        return false;
    }

    private static bool TryWebP(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        var chunk = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4));
        switch (chunk)
        {
            case "VP8X":
                width = 1 + (b[24] | b[25] << 8 | b[26] << 16);
                height = 1 + (b[27] | b[28] << 8 | b[29] << 16);
                break;
            case "VP8 ":
                width = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(26, 2)) & 0x3FFF;
                height = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(28, 2)) & 0x3FFF;
                break;
            case "VP8L":
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(21, 4));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                break;
        }
        return width > 0 && height > 0;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            var len = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            var sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (sof)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2));
                return width > 0 && height > 0;
            }
            if (len < 2) return false;
            i += 2 + len;
        }
        return false;
    }
}
