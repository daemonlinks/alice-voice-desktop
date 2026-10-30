using System.Buffers.Binary;
using System.Text;
using AliceVoice.Core.Audio;
using Xunit;

namespace AliceVoice.App.Tests;

public class WavDecoderTests
{
    private static byte[] Wav(int fmt, int channels, int rate, int bits, byte[] data, bool extraChunk = false)
    {
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
        void I16(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(b, (short)v); ms.Write(b); }
        W("RIFF"); I32(0); W("WAVE");
        if (extraChunk) { W("LIST"); I32(3); ms.Write(new byte[] { 1, 2, 3, 0 }); } // odd size + pad byte
        W("fmt "); I32(16); I16(fmt); I16(channels); I32(rate); I32(rate * channels * bits / 8); I16(channels * bits / 8); I16(bits);
        W("data"); I32(data.Length); ms.Write(data);
        return ms.ToArray();
    }

    private static short S(byte[] pcm, int i) => BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));

    [Fact]
    public void Pcm16MonoPassesThroughWithRealRate()
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), -1000);
        var d = WavDecoder.Decode(Wav(1, 1, 22050, 16, data))!;
        Assert.Equal(22050, d.SampleRate);
        Assert.Equal(4, d.Pcm16Mono.Length / 2);
        Assert.InRange(S(d.Pcm16Mono, 0), 998, 1000);
        Assert.InRange(S(d.Pcm16Mono, 1), -1000, -998);
    }

    [Fact]
    public void Float32IsConvertedAndClamped()
    {
        var data = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), -2f);
        var d = WavDecoder.Decode(Wav(3, 1, 24000, 32, data))!;
        Assert.Equal(24000, d.SampleRate);
        Assert.InRange(S(d.Pcm16Mono, 0), 16380, 16386);
        Assert.Equal(short.MaxValue, S(d.Pcm16Mono, 1));
        Assert.Equal(-32767, S(d.Pcm16Mono, 2));
    }

    [Fact]
    public void StereoIsDownmixedAndOddChunksSkipped()
    {
        var data = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0), 2000);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), 0);
        var d = WavDecoder.Decode(Wav(1, 2, 44100, 16, data, extraChunk: true))!;
        Assert.Equal(44100, d.SampleRate);
        Assert.Equal(1, d.Pcm16Mono.Length / 2);
        Assert.InRange(S(d.Pcm16Mono, 0), 998, 1001);
    }

    [Fact]
    public void GarbageReturnsNull()
    {
        Assert.Null(WavDecoder.Decode(Encoding.ASCII.GetBytes("not a wav file at all")));
        Assert.Null(WavDecoder.Decode(Array.Empty<byte>()));
    }
}

public class TextChunkerTests
{
    [Fact]
    public void ShortTextIsOneChunk() => Assert.Equal(new[] { "Hello there." }, TextChunker.Chunk("  Hello there. ", 900));

    [Fact]
    public void SplitsAtSentenceEndsWithinLimit()
    {
        var text = string.Join(" ", Enumerable.Repeat("This is a sentence of words.", 60));
        var chunks = TextChunker.Chunk(text, 200).ToList();
        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.All(chunks, c => Assert.EndsWith(".", c));
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
    }

    [Fact]
    public void NeverCutsWordsWhenSpacesExist()
    {
        var text = string.Join(" ", Enumerable.Repeat("alpha", 100));
        var chunks = TextChunker.Chunk(text, 50).ToList();
        Assert.All(chunks, c => Assert.All(c.Split(' '), w => Assert.Equal("alpha", w)));
    }

    [Fact]
    public void HardCutsUnbrokenText()
    {
        var chunks = TextChunker.Chunk(new string('x', 250), 100).ToList();
        Assert.Equal(new[] { 100, 100, 50 }, chunks.Select(c => c.Length));
    }
}
