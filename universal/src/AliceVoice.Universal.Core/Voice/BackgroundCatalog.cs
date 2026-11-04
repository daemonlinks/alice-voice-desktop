using Serilog;

namespace AliceVoice.Universal.Voice;

/// <summary>Scene images: every jpg then every png in the backgrounds folder, same order as the WinUI window.</summary>
public sealed class BackgroundCatalog
{
    private readonly List<string> _files;
    private readonly Random _random;

    public BackgroundCatalog(IEnumerable<string> files, Random? random = null)
    {
        _files = files.ToList();
        _random = random ?? Random.Shared;
        if (_files.Count > 0) Index = 0;
    }

    public int Index { get; private set; } = -1;
    public IReadOnlyList<string> Files => _files;
    public string? Current => Index >= 0 && Index < _files.Count ? _files[Index] : null;

    public static BackgroundCatalog Load(string? directory, Random? random = null)
    {
        var files = new List<string>();
        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                files.AddRange(Directory.GetFiles(directory, "*.jpg"));
                files.AddRange(Directory.GetFiles(directory, "*.png"));
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Background load failed"); }
        return new BackgroundCatalog(files, random);
    }

    public string? Step(int delta)
    {
        if (_files.Count == 0) return null;
        Index = ((Index + delta) % _files.Count + _files.Count) % _files.Count;
        return Current;
    }

    /// <summary>Pick a different image. Does nothing when there are fewer than two.</summary>
    public string? RandomOther()
    {
        if (_files.Count < 2) return Current;
        int idx;
        do { idx = _random.Next(_files.Count); } while (idx == Index);
        Index = idx;
        return Current;
    }
}
