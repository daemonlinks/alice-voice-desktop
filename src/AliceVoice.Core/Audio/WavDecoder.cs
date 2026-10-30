using System.Buffers.Binary;
using System.Text;

namespace AliceVoice.Core.Audio;

/// <summary>Decoded audio: mono Int16 little-endian PCM bytes at <see cref="SampleRate"/>.</summary>
public sealed record DecodedWav(byte[] Pcm16Mono, int SampleRate)
{
    public double Seconds => Pcm16Mono.Length / 2.0 / SampleRate;
}

/// <summary>
/// Minimal RIFF/WAVE decoder shared by the AllTalk and Piper providers.
/// Reads the real sample rate and channel count from the header (instead of
/// assuming one), handles PCM 16/24/32-bit and IEEE float 32-bit, skips
/// unknown and padded chunks, and down-mixes to mono.
/// </summary>
public static class WavDecoder
{
    public static DecodedWav? Decode(byte[] wav)
    {
        if (wav.Length < 12) return null;
        var span = wav.AsSpan();
        if (Encoding.ASCII.GetString(span[..4]) != "RIFF" || Encoding.ASCII.GetString(span.Slice(8, 4)) != "WAVE") return null;

        int pos = 12;
        int fmt = 0, channels = 0, rate = 0, bits = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(span.Slice(pos, 4));
            var size = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
            pos += 8;
            if (size < 0) return null;
            var avail = Math.Min(size, wav.Length - pos);
            if (id == "fmt " && avail >= 16)
            {
                fmt = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 2, 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 14, 2));
                // WAVE_FORMAT_EXTENSIBLE: real format code is the first 2 bytes of the sub-format GUID.
                if (fmt == 0xFFFE && avail >= 26) fmt = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(pos + 24, 2));
            }
            else if (id == "data")
            {
                if (channels <= 0 || rate <= 0 || bits <= 0) return null;
                return new DecodedWav(ToPcm16Mono(span.Slice(pos, avail), fmt, bits, channels), rate);
            }
            pos += size + (size & 1); // chunks are word-aligned
        }
        return null;
    }

    private static byte[] ToPcm16Mono(ReadOnlySpan<byte> data, int fmt, int bits, int channels)
    {
        int bytesPer = bits / 8;
        if (bytesPer <= 0) return Array.Empty<byte>();
        int frames = data.Length / (bytesPer * channels);
        var output = new byte[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
                sum += Sample(data.Slice((f * channels + c) * bytesPer, bytesPer), fmt, bits);
            var s = (short)Math.Clamp(Math.Round(sum / channels * 32767.0), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(f * 2, 2), s);
        }
        return output;
    }

    /// <summary>One sample as -1..1.</summary>
    private static double Sample(ReadOnlySpan<byte> b, int fmt, int bits)
    {
        if (fmt == 3 && bits == 32) return Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(b), -1f, 1f);
        if (fmt == 3 && bits == 64) return Math.Clamp(BinaryPrimitives.ReadDoubleLittleEndian(b), -1, 1);
        return bits switch
        {
            8 => (b[0] - 128) / 128.0,
            16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768.0,
            24 => ((b[0] | (b[1] << 8) | ((sbyte)b[2] << 16))) / 8388608.0,
            32 => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648.0,
            _ => 0
        };
    }
}
