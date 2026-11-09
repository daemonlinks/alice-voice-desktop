using AliceVoice.Core.Logging;
using AliceVoice.Universal.Host;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

[CollectionDefinition("CrashDiagnostics", DisableParallelization = true)]
public sealed class CrashDiagnosticsCollection;

[Collection("CrashDiagnostics")]
public sealed class CrashDiagnosticsTests
{
    [Fact]
    public void RedactorLeavesOrdinaryDiagnosticsAndStripsSecrets()
    {
        Assert.Equal("", DiagnosticRedactor.Redact(null));
        Assert.Equal(
            "Object reference not set to an instance of an object.",
            DiagnosticRedactor.Redact("Object reference not set to an instance of an object."));
        Assert.Equal("honcho_search", DiagnosticRedactor.Redact("honcho_search"));

        var mixed = DiagnosticRedactor.Redact(
            "api_key=supersecretvalue Bearer abcdefghijklmnop.token " +
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk " +
            "sk-abcdefghijklmnopqrstuvwxyz {\"args\":{\"query\":\"private memory\"},\"content\":\"secret fact\"} " +
            "[TOOL honcho_search result: the user likes cats]");

        Assert.DoesNotContain("supersecretvalue", mixed);
        Assert.DoesNotContain("abcdefghijklmnop", mixed);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", mixed);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", mixed);
        Assert.DoesNotContain("private memory", mixed);
        Assert.DoesNotContain("secret fact", mixed);
        Assert.DoesNotContain("likes cats", mixed);
        Assert.Contains("api_key=", mixed);
        Assert.Contains("Bearer [redacted]", mixed);
        Assert.Contains("[redacted-jwt]", mixed);
        Assert.Contains("[redacted-key]", mixed);
        Assert.Contains("\"args\":[redacted]", mixed);
        Assert.Contains("[TOOL honcho_search result: [redacted]", mixed);
    }

    [Fact]
    public void FormatIncludesExceptionShapeMetadataAndBreadcrumbs()
    {
        Exception captured;
        try
        {
            throw new InvalidOperationException("outer-msg api_key=sekret", new InvalidDataException("inner-msg"));
        }
        catch (Exception ex)
        {
            captured = ex;
        }

        var aggregate = new AggregateException(
            new InvalidOperationException("agg-one"),
            new IOException("agg-two"));
        var text = CrashDiagnostics.Format(new CrashSnapshot
        {
            Timestamp = new DateTimeOffset(2026, 11, 6, 16, 57, 41, TimeSpan.Zero),
            AppName = "AliceVoiceAppU",
            Version = "0.1.0+112d87e",
            ProcessId = 4242,
            ProcessName = "AliceVoice.Universal.App",
            ProcessPath = @"D:\Apps\AliceVoice.AppU\AliceVoice.Universal.App.exe",
            ThreadId = 7,
            ThreadName = "ui",
            Source = "ui",
            Context = "Microsoft.UI.Xaml.Application.UnhandledException",
            Fatal = true,
            Exception = new AggregateException(captured, aggregate.InnerExceptions[0], aggregate.InnerExceptions[1]),
            RuntimeMessage = "stowed",
            Framework = "test-framework",
            OperatingSystem = "test-os",
            Breadcrumbs =
            [
                new OperationTrail.Entry(new DateTimeOffset(2026, 11, 6, 16, 57, 37, TimeSpan.Zero), 7, "tool.start", "honcho_search")
            ]
        });

        Assert.Contains("timestamp: 2026-11-06T16:57:41.0000000+00:00", text);
        Assert.Contains("app: AliceVoiceAppU", text);
        Assert.Contains("version: 0.1.0+112d87e", text);
        Assert.Contains("process: AliceVoice.Universal.App (pid 4242)", text);
        Assert.Contains(@"processPath: D:\Apps\AliceVoice.AppU\AliceVoice.Universal.App.exe", text);
        Assert.Contains("thread: 7 name=ui", text);
        Assert.Contains("source: ui", text);
        Assert.Contains("fatal: true", text);
        Assert.Contains("context: Microsoft.UI.Xaml.Application.UnhandledException", text);
        Assert.Contains("framework: test-framework", text);
        Assert.Contains("os: test-os", text);
        Assert.Contains("runtimeMessage:", text);
        Assert.Contains("stowed", text);
        Assert.Contains("type: System.AggregateException", text);
        Assert.Contains("type: System.InvalidOperationException", text);
        Assert.Contains("type: System.IO.InvalidDataException", text);
        Assert.Contains("type: System.IO.IOException", text);
        Assert.Contains("message: outer-msg api_key=[redacted]", text);
        Assert.Contains("message: inner-msg", text);
        Assert.Contains("message: agg-one", text);
        Assert.Contains("message: agg-two", text);
        Assert.Contains("hresult: " + captured.HResult, text);
        Assert.Contains("0x" + captured.HResult.ToString("X8"), text);
        Assert.Contains("tostring:", text);
        Assert.Contains("inner[0]:", text);
        Assert.Contains("inner[1]:", text);
        Assert.Contains("tool.start honcho_search", text);
        Assert.DoesNotContain("sekret", text);
        Assert.Contains("stack:", text);
        Assert.Contains(nameof(FormatIncludesExceptionShapeMetadataAndBreadcrumbs), text);
    }

    [Fact]
    public void UiExceptionsStayUnhandledAndContextIsRedacted()
    {
        Assert.False(CrashDiagnostics.DecideUiException().MarkHandled);
        var context = CrashDiagnostics.UiContext("failed token=letmein");
        Assert.StartsWith("Microsoft.UI.Xaml.Application.UnhandledException", context);
        Assert.DoesNotContain("letmein", context);
        Assert.Equal("Microsoft.UI.Xaml.Application.UnhandledException", CrashDiagnostics.UiContext("  "));
    }

    [Fact]
    public void OptionsForPutsTheCrashDirectoryBesideTheLogs()
    {
        var logs = Path.Combine(Path.GetTempPath(), "AliceVoice", "logs");
        var options = CrashDiagnostics.OptionsFor(logs, "0.1.0+test");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "AliceVoice", "crash"), options.CrashDirectory);
        Assert.Equal("0.1.0+test", options.Version);
        Assert.Equal("AliceVoiceAppU", options.AppName);
    }

    [Fact]
    public void ReportPersistsAFlushedFileAndRegistersHandlersOnce()
    {
        using var iso = new Isolated();
        OperationTrail.Leave("tool.start", "honcho_search");
        OperationTrail.Leave("note", "api_key=supersecretvalue");

        var path = CrashDiagnostics.Report("ui", new InvalidOperationException("persist-me"), "after tool");

        Assert.True(CrashDiagnostics.ProcessHandlersRegistered);
        Assert.Equal(1, CrashDiagnostics.HandlerAttachCount);
        iso.InstallAgain();
        Assert.Equal(1, CrashDiagnostics.HandlerAttachCount);

        Assert.NotNull(path);
        Assert.StartsWith(iso.Crash, path);
        var text = File.ReadAllText(path);
        Assert.Contains("type: System.InvalidOperationException", text);
        Assert.Contains("message: persist-me", text);
        Assert.Contains("source: ui", text);
        Assert.Contains("version: 0.1.0+test", text);
        Assert.Contains("pid " + Environment.ProcessId, text);
        Assert.Contains("thread: " + Environment.CurrentManagedThreadId, text);
        Assert.Contains("tool.start honcho_search", text);
        Assert.Contains("context: after tool", text);
        Assert.Contains("fatal: true", text);
        Assert.DoesNotContain("supersecretvalue", text);
        Assert.Equal(1, iso.Flushes);
        Assert.True(iso.LoggedFatal);
        Assert.Contains("persist-me", iso.LoggedText);
    }

    [Fact]
    public void DomainHandlerWritesATerminatingReportAndTaskHandlerStaysNonFatal()
    {
        using var iso = new Isolated();
        var domain = new InvalidOperationException("domain-boom");
        CrashDiagnostics.OnDomainUnhandled(null, new UnhandledExceptionEventArgs(domain, isTerminating: true));
        var crash = Assert.Single(Directory.GetFiles(iso.Crash, "crash-*.txt"));
        var crashText = File.ReadAllText(crash);
        Assert.Contains("AppDomain.UnhandledException terminating=true", crashText);
        Assert.Contains("domain-boom", crashText);
        Assert.Contains("fatal: true", crashText);
        Assert.Equal(1, iso.Flushes);

        var task = new UnobservedTaskExceptionEventArgs(new AggregateException(new IOException("task-boom")));
        CrashDiagnostics.OnUnobservedTask(null, task);
        Assert.True(task.Observed);
        var failure = Assert.Single(Directory.GetFiles(iso.Crash, "failure-*.txt"));
        var failureText = File.ReadAllText(failure);
        Assert.Contains("TaskScheduler.UnobservedTaskException", failureText);
        Assert.Contains("type: System.IO.IOException", failureText);
        Assert.Contains("message: task-boom", failureText);
        Assert.Contains("fatal: false", failureText);
        Assert.Equal(1, iso.Flushes);
        Assert.False(iso.LastFatal);
    }

    [Fact]
    public void SameExceptionIsWrittenOnce()
    {
        using var iso = new Isolated();
        var ex = new InvalidOperationException("once");
        Assert.NotNull(CrashDiagnostics.Report("ui", ex, "first"));
        Assert.Null(CrashDiagnostics.Report("domain", ex, "second"));
        Assert.Single(Directory.GetFiles(iso.Crash, "crash-*.txt"));
    }

    [Fact]
    public void NullExceptionsWithDifferentMessagesAreBothKept()
    {
        using var iso = new Isolated();
        CrashDiagnostics.OnDomainUnhandled(null, new UnhandledExceptionEventArgs("first-blob", isTerminating: true));
        CrashDiagnostics.OnDomainUnhandled(null, new UnhandledExceptionEventArgs("second-blob", isTerminating: true));
        CrashDiagnostics.OnDomainUnhandled(null, new UnhandledExceptionEventArgs("second-blob", isTerminating: true));
        var files = Directory.GetFiles(iso.Crash, "crash-*.txt");
        Assert.Equal(2, files.Length);
        var body = string.Join("\n", files.Select(File.ReadAllText));
        Assert.Contains("first-blob", body);
        Assert.Contains("second-blob", body);
    }

    [Fact]
    public void FallsBackWhenTheCrashDirectoryCannotBeCreated()
    {
        using var iso = new Isolated(crashDirectoryIsFile: true);
        CrashDiagnostics.LoggerWrite = (_, _) => throw new IOException("logger down");
        var path = CrashDiagnostics.Report("startup", new InvalidOperationException("fallback-me"), "logger unavailable");
        Assert.NotNull(path);
        Assert.StartsWith(iso.Fallback, path);
        var text = File.ReadAllText(path);
        Assert.Contains("fallback-me", text);
        Assert.Contains("source: startup", text);
        Assert.DoesNotContain("logger down", text);
    }

    [Fact]
    public void RetentionKeepsTheNewestReports()
    {
        using var iso = new Isolated(retained: 2);
        for (var i = 0; i < 4; i++)
            Assert.NotNull(CrashDiagnostics.Report("ui", new InvalidOperationException("keep-" + i), "n" + i));
        var files = Directory.GetFiles(iso.Crash, "crash-*.txt");
        Assert.Equal(2, files.Length);
        var body = string.Join("\n", files.Select(File.ReadAllText));
        Assert.Contains("keep-3", body);
        Assert.Contains("keep-2", body);
        Assert.DoesNotContain("keep-0", body);
    }

    [Fact]
    public void ReenteredReportDoesNotRecurseAndStillPersistsTheOuterOne()
    {
        using var iso = new Isolated();
        CrashDiagnostics.TestProbe = _ => CrashDiagnostics.Report("startup", new InvalidOperationException("nested-secret"), "reenter");
        var path = CrashDiagnostics.Report("ui", new InvalidOperationException("outer-visible"), "first");
        Assert.NotNull(path);
        Assert.Contains("outer-visible", File.ReadAllText(path));
        Assert.DoesNotContain("nested-secret", string.Join("\n", Directory.GetFiles(iso.Crash, "*.txt").Select(File.ReadAllText)));
        Assert.Single(Directory.GetFiles(iso.Crash, "recursive-*.txt"));
    }

    [Fact]
    public void ProbeFailureStillPersists()
    {
        using var iso = new Isolated();
        CrashDiagnostics.TestProbe = _ => throw new InvalidOperationException("probe blew up");
        var path = CrashDiagnostics.Report("background", new InvalidOperationException("still-here"), "ctx");
        Assert.NotNull(path);
        Assert.Contains("still-here", File.ReadAllText(path!));
        Assert.Contains("source: background", File.ReadAllText(path));
    }

    [Fact]
    public void TrailIsBoundedRedactedAndNeverThrows()
    {
        OperationTrail.Clear();
        try
        {
            OperationTrail.Leave(null!, null);
            OperationTrail.Leave("tool start", "api_key=supersecretvalue\nsecond line");
            var redacted = OperationTrail.Snapshot();
            Assert.Contains(redacted, item => item.Operation == "operation" && item.Detail.Contains("[redacted]") && !item.Detail.Contains("supersecretvalue") && !item.Detail.Contains('\n'));

            for (var i = 0; i < OperationTrail.Capacity + 5; i++)
                OperationTrail.Leave("tool.end", "n" + i);
            var items = OperationTrail.Snapshot();
            Assert.Equal(OperationTrail.Capacity, items.Length);
            Assert.DoesNotContain(items, item => item.Detail.Contains("supersecretvalue") || item.Detail.Contains('\n'));
            Assert.Equal("n" + (OperationTrail.Capacity + 4), items[^1].Detail);
            Assert.All(items, item => Assert.Matches("^[A-Za-z0-9._-]{1,48}$", item.Operation));
        }
        finally
        {
            OperationTrail.Clear();
        }
    }

    private sealed class Isolated : IDisposable
    {
        public string Root { get; }
        public string Crash { get; }
        public string Fallback { get; }
        public int Flushes;
        public bool LoggedFatal;
        public bool LastFatal;
        public string LoggedText = "";

        public Isolated(int retained = 32, bool crashDirectoryIsFile = false)
        {
            CrashDiagnostics.ResetForTests();
            OperationTrail.Clear();
            Root = Path.Combine(Path.GetTempPath(), "av-crash-" + Guid.NewGuid().ToString("N"));
            Crash = Path.Combine(Root, "crash");
            Fallback = Path.Combine(Root, "fallback");
            Directory.CreateDirectory(Root);
            if (crashDirectoryIsFile) File.WriteAllText(Crash, "not a directory");
            CrashDiagnostics.FlushLogger = () => Flushes++;
            CrashDiagnostics.LoggerWrite = (fatal, text) =>
            {
                LastFatal = fatal;
                LoggedFatal = fatal;
                LoggedText = text;
            };
            CrashDiagnostics.Install(new CrashLogOptions
            {
                CrashDirectory = Crash,
                FallbackDirectory = Fallback,
                AppName = "AliceVoiceAppU",
                Version = "0.1.0+test",
                RetainedFiles = retained
            });
        }

        public void InstallAgain() => CrashDiagnostics.Install(new CrashLogOptions
        {
            CrashDirectory = Crash,
            FallbackDirectory = Fallback,
            Version = "0.1.0+test",
            RetainedFiles = 32
        });

        public void Dispose()
        {
            CrashDiagnostics.ResetForTests();
            OperationTrail.Clear();
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { /* temp cleanup is best-effort */ }
        }
    }
}
