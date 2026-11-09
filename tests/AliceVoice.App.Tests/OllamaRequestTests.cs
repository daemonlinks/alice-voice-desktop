using System.Net;
using System.Text;
using System.Text.Json;
using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

public class OllamaRequestTests
{
    [Fact]
    public async Task ChatDoesNotSendKeepAliveAndStillLimitsTheReply()
    {
        var handler = new CaptureHandler("{\"message\":{\"content\":\"hi\"},\"done\":true}\n");
        using var client = new OllamaChatClient(new HttpClient(handler));
        var reply = await client.ChatAsync("http://localhost:11434", "dolphin-llama3:8b",
            [new ChatMessage("user", "hello")], 40, null, CancellationToken.None);

        Assert.Equal("hi", reply);
        using var doc = JsonDocument.Parse(handler.Body);
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
        Assert.Equal(40, doc.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("dolphin-llama3:8b", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ChatWithoutATokenLimitStillOmitsKeepAlive()
    {
        var handler = new CaptureHandler("{\"done\":true}\n");
        using var client = new OllamaChatClient(new HttpClient(handler));
        var reply = await client.ChatAsync("http://localhost:11434", "dolphin-llama3:8b",
            [new ChatMessage("user", "hello")], null, null, CancellationToken.None);
        Assert.Equal("", reply);
        using var doc = JsonDocument.Parse(handler.Body);
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
        Assert.Equal(0, doc.RootElement.GetProperty("options").GetRawText().Trim().Count(c => c == ':'));
    }

    [Fact]
    public async Task NativeToolRequestOmitsKeepAliveAndSendsTheToolSchemas()
    {
        var handler = new CaptureHandler("""
            {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"honcho_search","arguments":{"query":"name"}}}]},"done":true}
            """);
        using var client = new OllamaChatClient(new HttpClient(handler));
        var completion = await client.CompleteAsync("http://localhost:11434", "dolphin-llama3:8b",
            [new ChatMessage("user", "hello")], null, null, nativeTools: true, CancellationToken.None);
        using var doc = JsonDocument.Parse(handler.Body);
        Assert.False(doc.RootElement.TryGetProperty("keep_alive", out _));
        Assert.Equal("honcho_search", doc.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("honcho_search", completion.ToolCalls[0].Name);
        Assert.Contains("name", completion.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public async Task AnUnfinishedStreamIsAnErrorRatherThanABlankSuccess()
    {
        var handler = new CaptureHandler("{\"message\":{\"content\":\"partial\"}}\n");
        using var client = new OllamaChatClient(new HttpClient(handler));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(
            "http://localhost:11434", "dolphin-llama3:8b", [new ChatMessage("user", "hello")], null, null, CancellationToken.None));
        Assert.Contains("ended before a completed reply", ex.Message);
    }

    [Fact]
    public async Task AStuckModelReportsATimeoutInsteadOfACancel()
    {
        var handler = new HoldHandler();
        using var client = new OllamaChatClient(new HttpClient(handler)) { ReplyTimeout = TimeSpan.FromMilliseconds(40) };
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.ChatAsync(
            "http://localhost:11434", "dolphin-llama3:8b", [new ChatMessage("user", "hello")], null, null, CancellationToken.None));
        Assert.Contains("did not finish", ex.Message);
    }

    private sealed class CaptureHandler(string body) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson") };
        }
    }

    private sealed class HoldHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await request.Content!.ReadAsStringAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The timeout should have cancelled this request.");
        }
    }
}
