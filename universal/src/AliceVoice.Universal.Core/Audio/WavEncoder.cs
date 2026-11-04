using System.Buffers.Binary;
using System.Text;

namespace AliceVoice.Universal.Audio;

/// <summary>Wraps mono PCM16 in a minimal RIFF/WAVE header so a phone can play bytes the Beast produced.</summary>
public static class WavEncoder
{
    public static byte[] EncodePcm16Mono(byte[] pcm, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var data = pcm ?? Array.Empty<byte>();
        var bytes = new byte[44 + data.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + data.Length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data.Length);
        data.CopyTo(bytes, 44);
        return bytes;
    }
}
