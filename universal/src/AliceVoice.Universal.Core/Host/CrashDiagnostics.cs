using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using AliceVoice.Core.Logging;
using Serilog;
using Serilog.Events;

namespace AliceVoice.Universal.Host;

/// <summary>
/// Where a diagnostic file should be written. Directories are created on use.
/// A null crash directory falls back to <c>%LOCALAPPDATA%\AliceVoice\crash</c>.
/// </summary>
public sealed record CrashLogOptions
{
    public string? CrashDirectory { get; init; }
    public string? FallbackDirectory { get; init; }
    public string AppName { get; init; } = "AliceVoiceAppU";
    public string? Version { get; init; }
    public int RetainedFiles { get; init; } = 32;

    public static CrashLogOptions Default { get; } = new();
}

/// <summary>Inputs for <see cref="CrashDiagnostics.Format"/>. Tests build this directly.</summary>
public sealed record CrashSnapshot
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string AppName { get; init; }
    public required string Version { get; init; }
    public required int ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public string? ProcessPath { get; init; }
    public required int ThreadId { get; init; }
    public string? ThreadName { get; init; }
    public required string Source { get; init; }
    public string? Context { get; init; }
    public bool Fatal { get; init; }
    public Exception? Exception { get; init; }
    public string? RuntimeMessage { get; init; }
    public string? Framework { get; init; }
    public string? OperatingSystem { get; init; }
    public IReadOnlyList<OperationTrail.Entry> Breadcrumbs { get; init; } = Array.Empty<OperationTrail.Entry>();
}

/// <summary>
/// Decision for a UI-framework unhandled exception. <see cref="MarkHandled"/> is
/// always false: the report is written, then the process is still allowed to terminate.
/// </summary>
public readonly record struct UiExceptionDecision(bool MarkHandled);

/// <summary>
/// Process-wide crash and failure reports for App U. Writes a file with
/// <see cref="FileOptions.WriteThrough"/> before touching Serilog, and uses a
/// second directory when the main crash directory cannot be created. Handlers
/// are registered once. The same exception is not written twice.
/// </summary>
public static class CrashDiagnostics
{
    public const string UiSource = "ui";
    public const string DomainSource = "domain";
    public const string TaskSource = "task";

    private static readonly object Gate = new();
    private static readonly Exception?[] RecentExceptions = new Exception?[8];
    private static int _recentCursor;
    private static string? _lastFingerprint;
    private static long _lastFingerprintTicks;
    private static int _registered;
    private static int _depth;
    private static int _sequence;
    private static int _recursiveMarkers;
    private static CrashLogOptions _options = CrashLogOptions.Default;

    /// <summary>Test seam. The bool is true for a fatal report. Production leaves this null and uses Serilog.</summary>
    internal static Action<bool, string>? LoggerWrite { get; set; }

    /// <summary>Test seam. Production leaves this null and calls <see cref="Log.CloseAndFlush"/>.</summary>
    internal static Action? FlushLogger { get; set; }

    /// <summary>Invoked with the formatted report while a report is in progress. Used to prove the recursion guard.</summary>
    internal static Action<string>? TestProbe { get; set; }

    public static bool ProcessHandlersRegistered => Volatile.Read(ref _registered) == 1;

    /// <summary>How many times the process handlers were actually attached. Stays at 1 across repeated <see cref="Install"/> calls.</summary>
    internal static int HandlerAttachCount { get; private set; }

    /// <summary>Fatal UI exceptions stay unhandled so the platform still terminates the process.</summary>
    public static UiExceptionDecision DecideUiException() => new(false);

    public static string DefaultCrashDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AliceVoice", "crash");

    public static string DefaultFallbackDirectory =>
        Path.Combine(Path.GetTempPath(), "AliceVoice", "crash");

    /// <summary>Crash directory beside a log directory (<c>...\logs</c> -&gt; <c>...\crash</c>).</summary>
    public static CrashLogOptions OptionsFor(string logDirectory, string? version = null) => new()
    {
        CrashDirectory = SiblingCrashDirectory(logDirectory),
        Version = version,
        AppName = "AliceVoiceAppU"
    };

    public static void Install(CrashLogOptions? options = null)
    {
        lock (Gate)
        {
            if (options is not null) _options = options;
            if (Interlocked.Exchange(ref _registered, 1) == 1) return;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
            HandlerAttachCount++;
        }
        try { Directory.CreateDirectory(ResolvedCrashDirectory()); }
        catch { /* the write path has its own fallback */ }
    }

    public static string? Report(string source, Exception? exception, string? context = null, bool fatal = true)
        => Report(source, exception, context, fatal, runtimeMessage: null);

    public static string UiContext(string? runtimeMessage)
    {
        var message = DiagnosticRedactor.OneLine(runtimeMessage, 300);
        return string.IsNullOrEmpty(message)
            ? "Microsoft.UI.Xaml.Application.UnhandledException"
            : "Microsoft.UI.Xaml.Application.UnhandledException message=" + message;
    }

    public static string Format(CrashSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine(snapshot.Fatal ? "Alice Voice crash report" : "Alice Voice failure report");
        builder.Append("timestamp: ").AppendLine(snapshot.Timestamp.ToString("O"));
        builder.Append("app: ").AppendLine(snapshot.AppName);
        builder.Append("version: ").AppendLine(string.IsNullOrWhiteSpace(snapshot.Version) ? "unknown" : snapshot.Version);
        builder.Append("process: ").Append(string.IsNullOrWhiteSpace(snapshot.ProcessName) ? "unknown" : snapshot.ProcessName);
        builder.Append(" (pid ").Append(snapshot.ProcessId).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(snapshot.ProcessPath))
            builder.Append("processPath: ").AppendLine(DiagnosticRedactor.Redact(snapshot.ProcessPath));
        builder.Append("thread: ").Append(snapshot.ThreadId);
        builder.Append(" name=").AppendLine(string.IsNullOrWhiteSpace(snapshot.ThreadName) ? "(unnamed)" : DiagnosticRedactor.OneLine(snapshot.ThreadName, 80));
        builder.Append("source: ").AppendLine(SanitizeSource(snapshot.Source));
        builder.Append("fatal: ").AppendLine(snapshot.Fatal ? "true" : "false");
        builder.Append("context: ").AppendLine(string.IsNullOrWhiteSpace(snapshot.Context) ? "(none)" : DiagnosticRedactor.OneLine(snapshot.Context, 500));
        if (!string.IsNullOrWhiteSpace(snapshot.Framework))
            builder.Append("framework: ").AppendLine(DiagnosticRedactor.OneLine(snapshot.Framework, 200));
        if (!string.IsNullOrWhiteSpace(snapshot.OperatingSystem))
            builder.Append("os: ").AppendLine(DiagnosticRedactor.OneLine(snapshot.OperatingSystem, 200));
        if (!string.IsNullOrWhiteSpace(snapshot.RuntimeMessage))
        {
            builder.AppendLine("runtimeMessage:");
            builder.AppendLine(Indent(DiagnosticRedactor.Redact(snapshot.RuntimeMessage), "  "));
        }

        builder.AppendLine("exception:");
        if (snapshot.Exception is null)
            builder.AppendLine("  (none)");
        else
            WriteException(builder, snapshot.Exception, "  ", new HashSet<Exception>(ExceptionReferenceComparer.Instance));

        builder.AppendLine("breadcrumbs:");
        if (snapshot.Breadcrumbs.Count == 0)
            builder.AppendLine("  (none)");
        foreach (var crumb in snapshot.Breadcrumbs)
        {
            builder.Append("  ").Append(crumb.Timestamp.ToString("O"));
            builder.Append(" t").Append(crumb.ThreadId);
            builder.Append(' ').Append(crumb.Operation);
            if (!string.IsNullOrEmpty(crumb.Detail)) builder.Append(' ').Append(crumb.Detail);
            builder.AppendLine();
        }
        return builder.ToString();
    }

    internal static void OnDomainUnhandled(object? sender, UnhandledExceptionEventArgs args)
    {
        var context = args.IsTerminating ? "AppDomain.UnhandledException terminating=true" : "AppDomain.UnhandledException terminating=false";
        Report(DomainSource, args.ExceptionObject as Exception, context, fatal: true, runtimeMessage: args.ExceptionObject is not Exception ? args.ExceptionObject?.ToString() : null);
    }

    internal static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        // Unobserved task exceptions do not tear the process down. Marking them
        // observed keeps that behavior (the previous handler did the same) and
        // still stores the exception. This is not the fatal UI path.
        Report(TaskSource, args.Exception, "TaskScheduler.UnobservedTaskException", fatal: false);
        args.SetObserved();
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            _registered = 0;
            HandlerAttachCount = 0;
            _depth = 0;
            _sequence = 0;
            _recursiveMarkers = 0;
            _lastFingerprint = null;
            _lastFingerprintTicks = 0;
            _recentCursor = 0;
            Array.Clear(RecentExceptions);
            _options = CrashLogOptions.Default;
            LoggerWrite = null;
            FlushLogger = null;
            TestProbe = null;
        }
    }

    private static string? Report(string source, Exception? exception, string? context, bool fatal, string? runtimeMessage)
    {
        var depth = Interlocked.Increment(ref _depth);
        try
        {
            if (depth > 1)
            {
                if (depth == 2) WriteRecursiveMarker();
                return null;
            }
            if (IsDuplicate(exception, context, runtimeMessage)) return null;

            var snapshot = BuildSnapshot(source, exception, context, fatal, runtimeMessage);
            var text = Format(snapshot);
            try { TestProbe?.Invoke(text); }
            catch { /* the probe is a test seam; a failure there must not drop the report */ }

            var path = Persist(text, fatal, source);
            WriteToLogger(fatal ? LogEventLevel.Fatal : LogEventLevel.Error, text, path, source);
            if (fatal) TryFlushLogger();
            return path;
        }
        catch
        {
            WriteRecursiveMarker();
            return null;
        }
        finally
        {
            Interlocked.Decrement(ref _depth);
        }
    }

    private static CrashSnapshot BuildSnapshot(string source, Exception? exception, string? context, bool fatal, string? runtimeMessage)
    {
        CrashLogOptions options;
        lock (Gate) options = _options;
        string processName = "";
        try { processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName; }
        catch { /* process identity is optional */ }

        return new CrashSnapshot
        {
            Timestamp = DateTimeOffset.Now,
            AppName = string.IsNullOrWhiteSpace(options.AppName) ? "AliceVoiceAppU" : options.AppName,
            Version = ResolveVersion(options.Version),
            ProcessId = Environment.ProcessId,
            ProcessName = processName,
            ProcessPath = Environment.ProcessPath,
            ThreadId = Environment.CurrentManagedThreadId,
            ThreadName = Thread.CurrentThread.Name,
            Source = source,
            Context = context,
            Fatal = fatal,
            Exception = exception,
            RuntimeMessage = runtimeMessage,
            Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Breadcrumbs = OperationTrail.Snapshot()
        };
    }

    private static string ResolveVersion(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashDiagnostics).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational)) return informational;
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsDuplicate(Exception? exception, string? context, string? runtimeMessage)
    {
        // Source is intentionally not part of the fingerprint: the WinUI hook and
        // AppDomain hook can both observe one failure, and the second write is noise.
        // A null exception is identified by the runtime message instead, so two
        // different UI failures without an Exception object are both kept.
        var fingerprint = exception is null
            ? "null|" + context + "|" + runtimeMessage
            : exception.GetType().FullName + "|" + exception.Message + "|" + exception.StackTrace;
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (exception is not null)
            {
                foreach (var seen in RecentExceptions)
                {
                    if (ReferenceEquals(seen, exception)) return true;
                }
                RecentExceptions[_recentCursor++ % RecentExceptions.Length] = exception;
            }
            if (fingerprint == _lastFingerprint && now - _lastFingerprintTicks < 2000) return true;
            _lastFingerprint = fingerprint;
            _lastFingerprintTicks = now;
            return false;
        }
    }

    private static string? Persist(string text, bool fatal, string source)
    {
        var name = FileName(fatal, source);
        if (TryWrite(ResolvedCrashDirectory(), name, text, out var path))
        {
            Prune(ResolvedCrashDirectory());
            return path;
        }
        if (TryWrite(ResolvedFallbackDirectory(), name, text, out path))
        {
            Prune(ResolvedFallbackDirectory());
            return path;
        }
        return null;
    }

    private static void WriteToLogger(LogEventLevel level, string report, string? path, string source)
    {
        try
        {
            // The report is a property value, not a message template, so braces in
            // exception text are not parsed as holes.
            var line = "Diagnostic " + SanitizeSource(source) + " written to " + (path ?? "(not written)") + Environment.NewLine + report;
            if (LoggerWrite is { } hook)
            {
                hook(level == LogEventLevel.Fatal, line);
                return;
            }
            Log.Write(level, "{Report}", line);
        }
        catch
        {
            // The crash file is the record when Serilog is disposed or throwing.
        }
    }

    private static void TryFlushLogger()
    {
        try
        {
            if (FlushLogger is { } hook)
            {
                hook();
                return;
            }
            Log.CloseAndFlush();
        }
        catch
        {
            // Already persisted independently.
        }
    }

    private static void WriteRecursiveMarker()
    {
        if (Interlocked.Increment(ref _recursiveMarkers) != 1) return;
        var name = "recursive-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + ".txt";
        const string text = "Alice Voice crash handler re-entered; suppressed the nested report.\n";
        if (!TryWrite(ResolvedCrashDirectory(), name, text, out _))
            TryWrite(ResolvedFallbackDirectory(), name, text, out _);
    }

    private static bool TryWrite(string directory, string fileName, string text, out string path)
    {
        path = "";
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, fileName);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(text);
            writer.Flush();
            stream.Flush(true);
            return true;
        }
        catch
        {
            path = "";
            return false;
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            int keep;
            lock (Gate) keep = Math.Max(1, _options.RetainedFiles);
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*.txt")
                .Where(file => IsDiagnosticFile(file.Name))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
            foreach (var file in files)
            {
                try { file.Delete(); } catch { /* a locked old file is not worth failing the report */ }
            }
        }
        catch { /* retention is best-effort */ }
    }

    private static bool IsDiagnosticFile(string name) =>
        name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("failure-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("recursive-", StringComparison.OrdinalIgnoreCase);

    private static string FileName(bool fatal, string source)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var kind = fatal ? "crash" : "failure";
        return $"{kind}-{DateTime.Now:yyyyMMdd-HHmmssfff}-{SanitizeSource(source)}-p{Environment.ProcessId}-n{seq}.txt";
    }

    private static string ResolvedCrashDirectory()
    {
        lock (Gate)
            return string.IsNullOrWhiteSpace(_options.CrashDirectory) ? DefaultCrashDirectory : _options.CrashDirectory!;
    }

    private static string ResolvedFallbackDirectory()
    {
        lock (Gate)
            return string.IsNullOrWhiteSpace(_options.FallbackDirectory) ? DefaultFallbackDirectory : _options.FallbackDirectory!;
    }

    private static string SiblingCrashDirectory(string logDirectory)
    {
        var root = Path.GetDirectoryName(logDirectory);
        return string.IsNullOrEmpty(root) ? DefaultCrashDirectory : Path.Combine(root, "crash");
    }

    internal static string SanitizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "unknown";
        var trimmed = source.Trim().ToLowerInvariant();
        if (trimmed.Length > 24) trimmed = trimmed[..24];
        foreach (var c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_')) return "unknown";
        }
        return trimmed;
    }

    private static void WriteException(StringBuilder builder, Exception exception, string indent, HashSet<Exception> seen)
    {
        if (!seen.Add(exception))
        {
            builder.Append(indent).AppendLine("(cycle)");
            return;
        }

        builder.Append(indent).Append("type: ").AppendLine(exception.GetType().FullName);
        builder.Append(indent).Append("message: ").AppendLine(DiagnosticRedactor.Redact(exception.Message));
        builder.Append(indent).Append("hresult: ").Append(exception.HResult).Append(" (0x").Append(exception.HResult.ToString("X8")).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(exception.Source))
            builder.Append(indent).Append("exceptionSource: ").AppendLine(DiagnosticRedactor.OneLine(exception.Source, 200));
        builder.Append(indent).AppendLine("stack:");
        if (string.IsNullOrWhiteSpace(exception.StackTrace))
            builder.Append(indent).AppendLine("  (none)");
        else
            builder.AppendLine(Indent(DiagnosticRedactor.Redact(exception.StackTrace), indent + "  "));
        builder.Append(indent).AppendLine("tostring:");
        builder.AppendLine(Indent(DiagnosticRedactor.Redact(exception.ToString()), indent + "  "));

        if (exception is AggregateException aggregate)
        {
            for (var i = 0; i < aggregate.InnerExceptions.Count; i++)
            {
                builder.Append(indent).Append("inner[").Append(i).AppendLine("]:");
                WriteException(builder, aggregate.InnerExceptions[i], indent + "  ", seen);
            }
            return;
        }

        if (exception.InnerException is { } inner)
        {
            builder.Append(indent).AppendLine("inner:");
            WriteException(builder, inner, indent + "  ", seen);
        }
    }

    private sealed class ExceptionReferenceComparer : IEqualityComparer<Exception>
    {
        public static readonly ExceptionReferenceComparer Instance = new();
        public bool Equals(Exception? x, Exception? y) => ReferenceEquals(x, y);
        public int GetHashCode(Exception obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static string Indent(string text, string indent)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(indent);
            builder.AppendLine(line);
        }
        return builder.ToString().TrimEnd('\r', '\n');
    }
}
