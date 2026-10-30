using AliceVoice.Core.Chat;
using AliceVoice.Core.Config;
using AliceVoice.Core.Voice;
using Xunit;

namespace AliceVoice.App.Tests;

public class ConversationHistoryTests
{
    [Fact]
    public void KeepsTurnsInOrder()
    {
        var h = new ConversationHistory();
        h.AddTurn("hi", "hello");
        h.AddTurn("how are you", "fine");
        var s = h.Snapshot();
        Assert.Equal(4, s.Count);
        Assert.Equal(new ChatMessage("user", "hi"), s[0]);
        Assert.Equal(new ChatMessage("assistant", "fine"), s[3]);
    }

    [Fact]
    public void DropsOldestPairsWhenFull()
    {
        var h = new ConversationHistory(maxMessages: 4);
        for (int i = 0; i < 5; i++) h.AddTurn($"u{i}", $"a{i}");
        var s = h.Snapshot();
        Assert.Equal(4, s.Count);
        Assert.Equal("u3", s[0].Content);
        Assert.Equal("user", s[0].Role);
    }

    [Fact]
    public void ClearEmpties()
    {
        var h = new ConversationHistory();
        h.AddTurn("a", "b");
        h.Clear();
        Assert.Empty(h.Snapshot());
    }
}

public class OllamaChunkTests
{
    [Fact]
    public void ParsesContentDelta()
    {
        var t = OllamaChatClient.ParseChunk("{\"message\":{\"role\":\"assistant\",\"content\":\"Hel\"},\"done\":false}", out var done, out var err);
        Assert.Equal("Hel", t);
        Assert.False(done);
        Assert.Null(err);
    }

    [Fact]
    public void DetectsDone()
    {
        OllamaChatClient.ParseChunk("{\"message\":{\"content\":\"\"},\"done\":true}", out var done, out _);
        Assert.True(done);
    }

    [Fact]
    public void SurfacesError()
    {
        OllamaChatClient.ParseChunk("{\"error\":\"model not found\"}", out var done, out var err);
        Assert.True(done);
        Assert.Equal("model not found", err);
    }

    [Fact]
    public void IgnoresGarbage()
    {
        Assert.Equal("", OllamaChatClient.ParseChunk("not json", out var done, out var err));
        Assert.False(done);
        Assert.Null(err);
    }

    [Fact]
    public void TruncateCapsLength()
    {
        Assert.Equal("abc…", OllamaChatClient.Truncate("abcdef", 3));
        Assert.Equal("ab", OllamaChatClient.Truncate("ab", 3));
    }
}

public class ToolCallTests
{
    [Fact]
    public void ParsesPlainToolCall()
    {
        Assert.True(AliceLocalBackend.TryParseToolCall("{\"tool\":\"honcho_search\",\"args\":{\"query\":\"cats\"}}", out var tool, out var args));
        Assert.Equal("honcho_search", tool);
        Assert.Contains("cats", args);
    }

    [Fact]
    public void ParsesFencedToolCall()
    {
        Assert.True(AliceLocalBackend.TryParseToolCall("```json\n{\"tool\":\"search\",\"args\":{\"query\":\"x\"}}\n```", out var tool, out _));
        Assert.Equal("search", tool);
    }

    [Theory]
    [InlineData("Hello Master, how are you?")]
    [InlineData("{\"not\":\"a tool\"}")]
    [InlineData("{broken")]
    public void RejectsNonToolText(string reply)
    {
        Assert.False(AliceLocalBackend.TryParseToolCall(reply, out _, out _));
    }

    [Fact]
    public void BuildToolCallEscapesSafely()
    {
        var json = AliceLocalBackend.BuildToolCall("honcho_save", "{\"content\":\"she said \\\"hi\\\"\\nthen left\"}");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("honcho_save", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal("she said \"hi\"\nthen left", doc.RootElement.GetProperty("args").GetProperty("content").GetString());
    }

    [Fact]
    public void BuildToolCallToleratesBadArgs()
    {
        var json = AliceLocalBackend.BuildToolCall("search", "{oops");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.GetProperty("args").ValueKind);
    }
}

public class ReplyStyleTests
{
    [Theory]
    [InlineData("quick", 160)]
    [InlineData("natural", 320)]
    [InlineData("full", 1024)]
    public void ProfileSetsTokenCap(string profile, int tokens)
        => Assert.Equal(tokens, ReplyStyle.Build("natural", profile).MaxTokens);

    [Fact]
    public void MoodAddsStyleLine()
    {
        Assert.Contains("playful", ReplyStyle.Build("playful", "full").StyleHint);
        Assert.Null(ReplyStyle.Build("natural", "full").StyleHint);
    }
}

public class SoulResolverTests
{
    private static AliceVoiceOptions Opts(string root) => new()
    {
        AliceLocalSoulPath = Path.Combine(root, "alice_local", "SOUL.md")
    };

    [Fact]
    public void ReadsOnlyTheLocalSoul()
    {
        var o = Opts(Path.GetTempPath());
        Assert.Equal(o.ResolvedAliceLocalSoulPath, SoulResolver.ReadPath(o, _ => true));
    }

    [Fact]
    public void MissingLocalSoulGivesNullNeverAnAgentSoul()
    {
        var o = Opts(Path.GetTempPath());
        var agent = Path.Combine(o.HermesHome, "profiles", "alice_voice", "SOUL.md");
        Assert.Null(SoulResolver.ReadPath(o, p => p == agent));
        Assert.Null(SoulResolver.ReadPath(o, _ => false));
    }

    [Fact]
    public void OnlyLocalSoulIsWritable()
    {
        var o = Opts(Path.GetTempPath());
        Assert.True(SoulResolver.IsWritable(o, o.ResolvedAliceLocalSoulPath));
        Assert.False(SoulResolver.IsWritable(o, Path.Combine(o.HermesHome, "profiles", "alice_voice", "SOUL.md")));
        Assert.False(SoulResolver.IsWritable(o, Path.Combine(Path.GetTempPath(), "other.md")));
    }

    [Fact]
    public void LocalSoulInsideHermesProfilesIsRefused()
    {
        var o = new AliceVoiceOptions { AliceLocalSoulPath = Path.Combine(new AliceVoiceOptions().HermesHome, "profiles", "alice_voice", "SOUL.md") };
        Assert.False(SoulResolver.IsWritable(o, o.ResolvedAliceLocalSoulPath));
        Assert.Null(SoulResolver.ReadPath(o, _ => true));
    }

    [Fact]
    public void DetectsAgentSoulPaths()
    {
        var o = new AliceVoiceOptions();
        Assert.True(SoulResolver.IsAgentSoulPath(o, Path.Combine(o.HermesHome, "profiles", "alice_2", "SOUL.md")));
        Assert.False(SoulResolver.IsAgentSoulPath(o, o.ResolvedAliceLocalSoulPath));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), o.ResolvedAliceLocalSoulPath, StringComparison.OrdinalIgnoreCase);
    }
}

public class FleetChatTests
{
    [Fact]
    public void ParsesOpenAiReply()
    {
        var r = HermesApiFleetChat.ParseReply("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hi Master\"}}]}");
        Assert.Equal("Hi Master", r);
    }
}
