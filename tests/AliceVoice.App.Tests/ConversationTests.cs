using System.Net;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public void ParsesTheInlineNameQuestion()
    {
        const string reply = """
            To access Master's memory about their name, I will use the honcho_search tool with the query "Master's name".

            {"tool": "honcho_search", "args": {"query": "Master's name"}}
            """;
        Assert.True(AliceLocalBackend.TryParseToolCall(reply, out var tool, out var args));
        Assert.Equal("honcho_search", tool);
        Assert.Contains("Master's name", args);
        Assert.False(AliceLocalBackend.IsKnownTool("not_a_tool"));
        Assert.True(AliceLocalBackend.TryParseToolCall("{\"tool\":\"not_a_tool\",\"args\":{}}", out var unknown, out _));
        Assert.Equal("not_a_tool", unknown);
        Assert.Contains("unknown tool", AliceLocalBackend.UnknownToolResult(unknown));
    }

    [Fact]
    public void MalformedToolJsonIsNotACall()
    {
        const string reply = "I will search {\"tool\": \"honcho_search\", \"args\": {";
        Assert.False(AliceLocalBackend.TryParseToolCall(reply, out _, out _));
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
    public async Task InlineToolCallIsRunAndTheJsonIsNotTheReply()
    {
        const string asked = """
            To access Master's memory about their name, I will use the honcho_search tool with the query "Master's name".

            {"tool": "honcho_search", "args": {"query": "Master's name"}}
            """;
        var calls = new List<string>();
        var shown = new List<string>();
        var backend = Backend(new ScriptedOllama(body =>
        {
            if (body.Contains("\"tools\"", StringComparison.Ordinal))
                return Error("dolphin-llama3:8b does not support tools");
            if (calls.Count == 0)
                return Content(asked);
            return Content("Your name is Alex.");
        }));
        backend.RunTool = (tool, args, _) =>
        {
            calls.Add(tool + " " + args);
            return Task.FromResult("{\"notes\":[\"Master is called Alex\"]}");
        };
        backend.EventReceived += e =>
        {
            if (e.Type is "reply_text" or "reply_partial") shown.Add(e.Type + ":" + e.Text);
        };

        var reply = await backend.AskAsync("What is my name?");

        Assert.Equal("Your name is Alex.", reply);
        Assert.Contains(calls, c => c.Contains("honcho_search") && c.Contains("Master's name"));
        Assert.DoesNotContain(shown, line => line.Contains("\"tool\""));
        Assert.Contains(shown, line => line.Contains("Your name is Alex."));
    }

    [Fact]
    public async Task NativeToolCallIsRunWhenTheModelSupportsIt()
    {
        var calls = new List<string>();
        var backend = Backend(new ScriptedOllama(body =>
        {
            if (calls.Count > 0) return Content("Your name is Alex.");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"honcho_search","arguments":{"query":"Master's name"}}}]},"done":false}
                    {"message":{"role":"assistant","content":""},"done":true}
                    """, System.Text.Encoding.UTF8, "application/x-ndjson")
            };
        }));
        backend.RunTool = (tool, args, _) =>
        {
            calls.Add(tool + " " + args);
            return Task.FromResult("{}");
        };
        var shown = new List<string>();
        backend.EventReceived += e => { if (e.Type is "reply_text" or "reply_partial") shown.Add(e.Text ?? ""); };

        var reply = await backend.AskAsync("What is my name?");

        Assert.Equal("Your name is Alex.", reply);
        Assert.Contains(calls, c => c.Contains("honcho_search") && c.Contains("Master's name"));
        Assert.DoesNotContain(shown, text => text.Contains("\"tool\"") || text.Contains("tool_calls"));
    }

    [Fact]
    public async Task UnknownToolDoesNotRunAndMalformedJsonIsShown()
    {
        var ran = false;
        var backend = Backend(new ScriptedOllama(_ => Content("I will search {\"tool\": \"honcho_search\", \"args\": {")));
        backend.RunTool = (_, _, _) => { ran = true; return Task.FromResult("{}"); };
        var reply = await backend.AskAsync("hi");
        Assert.False(ran);
        Assert.Contains("honcho_search", reply);
        Assert.Contains("{", reply);
    }

    [Fact]
    public async Task UnknownToolNameIsReturnedToTheModelWithoutRunning()
    {
        var ran = false;
        var requests = 0;
        var backend = Backend(new ScriptedOllama(body =>
        {
            requests++;
            if (body.Contains("\"tools\"", StringComparison.Ordinal))
                return Error("dolphin-llama3 does not support tools");
            if (!body.Contains("unknown tool", StringComparison.Ordinal))
                return Content("{\"tool\": \"make_coffee\", \"args\": {}}");
            return Content("I can't make coffee.");
        }));
        backend.RunTool = (_, _, _) => { ran = true; return Task.FromResult("ran"); };
        var shown = new List<string>();
        backend.EventReceived += e => { if (e.Type is "reply_text" or "reply_partial") shown.Add(e.Text ?? ""); };

        var reply = await backend.AskAsync("coffee");

        Assert.False(ran);
        Assert.Equal("I can't make coffee.", reply);
        Assert.True(requests >= 3);
        Assert.DoesNotContain(shown, text => text.Contains("make_coffee"));
    }

    private static AliceLocalBackend Backend(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
        return new AliceLocalBackend(new AliceVoiceOptions { OllamaUrl = "http://ollama.test" }, new OllamaChatClient(http));
    }

    private static HttpResponseMessage Error(string message)
        => new(HttpStatusCode.BadRequest) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };

    private static HttpResponseMessage Content(string text)
    {
        var open = JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done = false });
        var close = JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done = true });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(open + "\n" + close + "\n", Encoding.UTF8, "application/x-ndjson")
        };
    }

    private sealed class ScriptedOllama(Func<string, HttpResponseMessage> next) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return next(body);
        }
    }

    [Fact]
    public void ToolInstructionsMatchTheWebContract()
    {
        var text = AliceLocalBackend.ToolDescriptions;
        Assert.Contains("ONLY a JSON object", text);
        Assert.Contains("Remember that", text);
        Assert.Contains("Remember I", text);
        Assert.Contains("What do you know about me?", text);
        Assert.Contains("Forget that", text);
        Assert.Contains("Remove that", text);
        Assert.Contains("Delete that", text);
        Assert.Contains("I don't like that anymore", text);
        Assert.Contains("What time is it?", text);
        Assert.Contains("Search for", text);
        Assert.Contains("honcho_search", text);
        Assert.Contains("honcho_save", text);
        Assert.Contains("honcho_forget", text);
        Assert.Contains("time: {}", text);
        Assert.Contains("workspace hermes", text);
        Assert.Contains("peer alice_local", text);
        Assert.Contains("Do not honcho_save search results", text);
        Assert.DoesNotContain("workspace alice_local", text);
        Assert.True(AliceLocalBackend.IsKnownTool("time"));
    }

    [Theory]
    [InlineData("Remember that I like tea", "honcho_save", "content", "I like tea")]
    [InlineData("remember i take sugar", "honcho_save", "content", "I take sugar")]
    [InlineData("What do you know about me?", "honcho_search", "query", "Master")]
    [InlineData("What time is it?", "time", "", "")]
    [InlineData("Search for rain tomorrow", "search", "query", "rain tomorrow")]
    [InlineData("Please search for tea", "search", "query", "tea")]
    [InlineData("Forget that I like tea", "honcho_forget", "content", "I like tea")]
    [InlineData("Remove that the blue note", "honcho_forget", "content", "the blue note")]
    [InlineData("Delete that cats", "honcho_forget", "query", "cats")]
    public void TriggerPhrasesBecomeTheWebToolCall(string utterance, string tool, string field, string value)
    {
        Assert.True(AliceLocalToolTriggers.TryMatch(utterance, out var name, out var args));
        Assert.Equal(tool, name);
        using var doc = JsonDocument.Parse(args);
        if (field.Length == 0)
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Empty(doc.RootElement.EnumerateObject());
            return;
        }
        Assert.Equal(value, doc.RootElement.GetProperty(field).GetString());
    }

    [Theory]
    [InlineData("I don't like that anymore")]
    [InlineData("What is my name?")]
    [InlineData("I remember that song")]
    [InlineData("Search for")]
    [InlineData("Hello Master")]
    public void ContextPhrasesAreLeftForTheModel(string utterance)
        => Assert.False(AliceLocalToolTriggers.TryMatch(utterance, out _, out _));

    [Fact]
    public void RememberButtonPrefixesAboutMaster()
    {
        Assert.Equal("About Master: likes tea", AliceLocalBackend.RememberContent("  likes tea "));
        Assert.Equal("About Master: likes tea", AliceLocalBackend.RememberContent("About Master: likes tea"));
        Assert.Equal("", AliceLocalBackend.RememberContent("   "));
    }

    [Fact]
    public async Task RememberButtonSavesToTheLocalPeer()
    {
        var backend = Backend(new ScriptedOllama(_ => Content("unused")));
        string? saved = null;
        backend.RunTool = (tool, args, _) =>
        {
            saved = tool + " " + args;
            return Task.FromResult("{\"saved\":true}");
        };
        Assert.True(await backend.RememberAsync("likes tea"));
        Assert.Contains("honcho_save", saved);
        Assert.Contains("About Master: likes tea", saved);
    }

    [Fact]
    public async Task SearchForRunsSearchAndDoesNotSaveTheResult()
    {
        string? body = null;
        var calls = new List<string>();
        var shown = new List<string>();
        var backend = Backend(new ScriptedOllama(text =>
        {
            body = text;
            if (text.Contains("\"tools\"", StringComparison.Ordinal))
                return Error("dolphin-llama3 does not support tools");
            return Content("Rain tomorrow.");
        }));
        backend.RunTool = (tool, args, _) =>
        {
            calls.Add(tool + " " + args);
            return Task.FromResult("{\"results\":[{\"title\":\"Rain\"}]}");
        };
        backend.EventReceived += e =>
        {
            if (e.Type is "reply_text" or "reply_partial") shown.Add(e.Text ?? "");
        };

        var reply = await backend.AskAsync("Search for rain tomorrow");

        Assert.Equal("Rain tomorrow.", reply);
        Assert.Contains(calls, c => c.StartsWith("search", StringComparison.Ordinal) && c.Contains("rain tomorrow"));
        Assert.DoesNotContain(calls, c => c.Contains("honcho_save"));
        Assert.Contains("Do not call honcho_save", body);
        Assert.DoesNotContain(shown, line => line.Contains("\"tool\""));
    }

    [Fact]
    public async Task WhatTimeIsItRunsTheTimeTool()
    {
        var calls = new List<string>();
        var backend = Backend(new ScriptedOllama(text =>
        {
            if (text.Contains("\"tools\"", StringComparison.Ordinal))
                return Error("dolphin-llama3 does not support tools");
            return Content("It is just after noon.");
        }));
        backend.RunTool = (tool, args, _) =>
        {
            calls.Add(tool + " " + args);
            return Task.FromResult("{\"time\":\"12:00\"}");
        };
        var reply = await backend.AskAsync("What time is it?");
        Assert.Equal("It is just after noon.", reply);
        Assert.Equal(new[] { "time {}" }, calls);
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
