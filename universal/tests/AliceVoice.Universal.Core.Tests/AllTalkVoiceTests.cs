using System.Net;
using System.Text;
using AliceVoice.Universal.Audio;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class AllTalkVoiceTests
{
    [Fact]
    public async Task RemoteClientFetchesTheWavFromTheBaseItCalled()
    {
        var http = new Script("http://127.0.0.1:7851/audio/clip.wav");
        var played = new Sink();
        using var provider = new AllTalkVoiceProvider(played, "http://100.64.237.3:7851", new HttpClient(http));

        await provider.SpeakAsync("hello", " ");

        Assert.Contains(http.Calls, c => c.Method == HttpMethod.Get && c.Url == "http://100.64.237.3:7851/audio/clip.wav");
        Assert.DoesNotContain(http.Calls, c => c.Url.Contains("127.0.0.1", StringComparison.Ordinal));
        Assert.Contains("character_voice_gen=female_07.wav", http.Calls[0].Body);
        Assert.Equal(22050, played.Rate);
        Assert.NotEmpty(played.Played);
    }

    [Fact]
    public async Task RelativeOutputUrlIsFetchedFromTheSameBase()
    {
        var http = new Script("/audio/clip.wav");
        var played = new Sink();
        using var provider = new AllTalkVoiceProvider(played, "http://ahc-the-beast:7851/", new HttpClient(http));

        await provider.SpeakAsync("hello", "");

        Assert.Equal("http://ahc-the-beast:7851/audio/clip.wav", http.Calls[^1].Url);
        Assert.Contains("character_voice_gen=female_07.wav", http.Calls[0].Body);
    }

    [Fact]
    public async Task BeastLocalAllTalkStillDownloadsLoopback()
    {
        var http = new Script("http://127.0.0.1:7851/audio/clip.wav");
        var played = new Sink();
        using var provider = new AllTalkVoiceProvider(played, "http://127.0.0.1:7851", new HttpClient(http));

        await provider.SpeakAsync("hello", "female_06.wav");

        Assert.Equal("http://127.0.0.1:7851/audio/clip.wav", http.Calls[^1].Url);
        Assert.Contains("character_voice_gen=female_06.wav", http.Calls[0].Body);
    }

    private sealed class Sink : IPcmPlayer
    {
        public List<byte[]> Played { get; } = new();
        public int Rate { get; private set; }
        public void EnqueuePcm(byte[] pcmBytes, int sampleRate) { Played.Add(pcmBytes); Rate = sampleRate; }
        public Task WaitForDrainAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Stop() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Script(string outputFileUrl) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.AbsoluteUri, body));
            if (request.Method == HttpMethod.Post)
            {
                var json = "{\"output_file_url\":" + System.Text.Json.JsonSerializer.Serialize(outputFileUrl) + "}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav()) };
        }

        private static byte[] Wav()
        {
            var data = new byte[] { 0, 0, 0, 0 };
            using var ms = new MemoryStream();
            void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
            void I32(int v) { Span<byte> b = stackalloc byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
            void I16(int v) { Span<byte> b = stackalloc byte[2]; System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(b, (short)v); ms.Write(b); }
            W("RIFF"); I32(36 + data.Length); W("WAVE");
            W("fmt "); I32(16); I16(1); I16(1); I32(22050); I32(44100); I16(2); I16(16);
            W("data"); I32(data.Length); ms.Write(data);
            return ms.ToArray();
        }
    }
}
