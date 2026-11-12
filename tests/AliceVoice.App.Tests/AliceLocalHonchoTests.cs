using System.Diagnostics;
using System.Text.Json;
using AliceVoice.Core.Config;
using Xunit;

namespace AliceVoice.App.Tests;

public class AliceLocalHonchoTests
{
    [Fact]
    public void ScriptPinsTheSharedHermesWorkspace()
    {
        var script = ScriptPath();
        var text = File.ReadAllText(script);
        Assert.Contains("WORKSPACE = \"hermes\"", text);
        Assert.Contains("PEER = \"AHC\"", text);
        Assert.Contains("AI_PEER = \"alice_local\"", text);
        Assert.DoesNotContain("get(\"HONCHO_WORKSPACE\", \"alice_local\")", text);
        Assert.DoesNotContain("sk-", text);
    }

    [Fact]
    public void SearchPlansTheHermesPeerNotTheStrayWorkspace()
    {
        using var doc = Plan("""{"tool":"honcho_search","args":{"query":"Master"}}""");
        var url = doc.RootElement.GetProperty("url").GetString()!;
        Assert.Contains("/v3/workspaces/hermes/conclusions/query", url);
        Assert.DoesNotContain("/workspaces/alice_local/", url);
        Assert.Equal("hermes", doc.RootElement.GetProperty("workspace").GetString());
        Assert.Equal("AHC", doc.RootElement.GetProperty("peer").GetString());
        Assert.Equal("alice_local", doc.RootElement.GetProperty("ai_peer").GetString());
        var filters = doc.RootElement.GetProperty("body").GetProperty("filters");
        Assert.Equal("alice_local", filters.GetProperty("observer").GetString());
        Assert.Equal("AHC", filters.GetProperty("observed").GetString());
    }

    [Fact]
    public void StaleWorkspaceEnvCannotSelectTheStrayRoot()
    {
        using var doc = Plan(
            """{"tool":"honcho_save","args":{"content":"likes tea"}}""",
            new Dictionary<string, string>
            {
                ["HONCHO_WORKSPACE"] = "alice_local",
                ["HONCHO_AI_PEER"] = "alice_voice",
                ["HONCHO_PEER"] = "someone",
                ["HONCHO_BASE"] = "http://127.0.0.1:9"
            });
        var url = doc.RootElement.GetProperty("url").GetString()!;
        Assert.StartsWith("http://127.0.0.1:9/v3/workspaces/hermes/conclusions", url);
        Assert.DoesNotContain("/workspaces/alice_local", url);
        Assert.DoesNotContain("alice_voice", doc.RootElement.ToString());
        var row = doc.RootElement.GetProperty("body").GetProperty("conclusions")[0];
        Assert.Equal("alice_local", row.GetProperty("observer_id").GetString());
        Assert.Equal("AHC", row.GetProperty("observed_id").GetString());
        Assert.Equal("likes tea", row.GetProperty("content").GetString());
    }

    [Fact]
    public void ForgetAcceptsContentWithoutAQuery()
    {
        using var doc = Plan("""{"tool":"honcho_forget","args":{"content":"I like tea"}}""");
        Assert.Equal("I like tea", doc.RootElement.GetProperty("query").GetString());
        Assert.Equal("I like tea", doc.RootElement.GetProperty("content").GetString());
        Assert.Contains("/workspaces/hermes/", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("error").ValueKind);
    }

    [Fact]
    public void TimeToolDoesNotCallHoncho()
    {
        using var planned = Plan("""{"tool":"time","args":{}}""");
        Assert.False(planned.RootElement.GetProperty("honcho").GetBoolean());
        Assert.Equal("hermes", planned.RootElement.GetProperty("workspace").GetString());

        using var ran = JsonDocument.Parse(Run("""{"tool":"time","args":{}}"""));
        Assert.False(ran.RootElement.TryGetProperty("error", out _));
        Assert.False(string.IsNullOrWhiteSpace(ran.RootElement.GetProperty("time").GetString()));
        Assert.Contains("T", ran.RootElement.GetProperty("iso").GetString());
    }

    [Fact]
    public void SearchWithoutAKeyDoesNotLeaveTheMachine()
    {
        using var doc = JsonDocument.Parse(Run("""{"tool":"search","args":{"query":"tea"}}"""));
        Assert.Equal("no TAVILY_API_KEY configured", doc.RootElement.GetProperty("error").GetString());
    }

    private static JsonDocument Plan(string stdin, IDictionary<string, string>? env = null)
        => JsonDocument.Parse(Run(stdin, env, plan: true));

    private static string Run(string stdin, IDictionary<string, string>? env = null, bool plan = false)
    {
        var psi = new ProcessStartInfo(Python())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(ScriptPath());
        if (plan) psi.ArgumentList.Add("--plan");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var key in new[] { "HONCHO_WORKSPACE", "HONCHO_PEER", "HONCHO_AI_PEER", "HONCHO_API_KEY", "HONCHO_BASE", "TAVILY_API_KEY", "ALICEVOICE_ENV" })
            psi.Environment.Remove(key);
        if (env is not null)
            foreach (var (key, value) in env) psi.Environment[key] = value;

        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(stdin);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(15000));
        Assert.True(proc.ExitCode == 0, stderr + stdout);
        return stdout;
    }

    private static string ScriptPath()
    {
        var path = RuntimeLocator.FindScript("alice_local_tools.py", baseDir: AppContext.BaseDirectory);
        Assert.NotNull(path);
        return path!;
    }

    private static string Python()
    {
        // The app's own resolver knows the Hermes venv (the interpreter that has
        // faster-whisper) and REFUSES the Windows Store stub. Hardcoded POSIX paths
        // made all five tests in this file fail on Windows with "Python was not found"
        // (fixed 2026-11-12; the stub is what a bare `python` resolves to).
        if (RuntimeLocator.FindPython(new AliceVoiceOptions()) is { } found) return found;
        foreach (var candidate in new[] { "/usr/bin/python3", "/usr/bin/python" })
            if (File.Exists(candidate)) return candidate;
        return "python3";
    }
}
