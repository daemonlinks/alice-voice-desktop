namespace AliceVoice.Core.Config;

/// <summary>
/// Finds the helper scripts and a Python interpreter that can actually run
/// them, wherever the app is launched from (bin\, a publish folder, or a
/// copied exe). Never returns the Microsoft Store "python" stub.
/// </summary>
public static class RuntimeLocator
{
    /// <summary>
    /// Locate a helper script: explicit dir → &lt;exe dir&gt;\scripts (copied at
    /// build) → walk up from the exe dir looking for a scripts\ folder (repo).
    /// </summary>
    public static string? FindScript(string fileName, string? configuredDir = null, string? baseDir = null,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        baseDir ??= AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(configuredDir))
        {
            var p = Path.Combine(configuredDir, fileName);
            if (fileExists(p)) return Path.GetFullPath(p);
        }

        var dir = new DirectoryInfo(baseDir);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "scripts", fileName);
            if (fileExists(p)) return p;
        }
        return null;
    }

    /// <summary>True for the WindowsApps App Execution Alias stub that opens the Store.</summary>
    public static bool IsStoreStub(string path)
        => path.Contains(Path.DirectorySeparatorChar + "WindowsApps" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Contains("/WindowsApps/", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Ordered list of python.exe candidates (existence not checked).</summary>
    public static IEnumerable<string> PythonCandidates(AliceVoiceOptions options)
    {
        if (!string.IsNullOrEmpty(options.PythonPath)) yield return options.PythonPath!;

        // Hermes venv — the interpreter that has faster-whisper + CUDA ctranslate2.
        yield return Path.Combine(options.HermesHome, "hermes-agent", "venv", "Scripts", "python.exe");

        // Real (non-stub) Python installs on PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim('"'), "python.exe");
    }

    /// <summary>First existing, non-stub candidate, or null.</summary>
    public static string? FindPython(AliceVoiceOptions options, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        foreach (var c in PythonCandidates(options))
        {
            if (IsStoreStub(c)) continue;
            if (fileExists(c)) return c;
        }
        return null;
    }
}
