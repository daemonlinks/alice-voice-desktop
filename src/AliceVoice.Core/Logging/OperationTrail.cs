namespace AliceVoice.Core.Logging;

/// <summary>
/// Bounded in-memory trail of recent operations. Crash reports copy it.
/// Callers pass an operation name and a short non-sensitive detail (tool name,
/// phase, character count). The trail redacts anyway and never throws.
/// </summary>
public static class OperationTrail
{
    public const int Capacity = 40;
    public const int MaxDetailLength = 180;

    private static readonly Queue<Entry> Items = new();
    private static readonly object Gate = new();

    public readonly record struct Entry(DateTimeOffset Timestamp, int ThreadId, string Operation, string Detail);

    public static void Leave(string operation, string? detail = null)
    {
        try
        {
            var entry = new Entry(DateTimeOffset.Now, Environment.CurrentManagedThreadId, SanitizeOperation(operation), DiagnosticRedactor.OneLine(detail, MaxDetailLength));
            lock (Gate)
            {
                Items.Enqueue(entry);
                while (Items.Count > Capacity) Items.Dequeue();
            }
        }
        catch
        {
            // A breadcrumb must not take down the operation it describes.
        }
    }

    public static Entry[] Snapshot()
    {
        lock (Gate) return Items.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) Items.Clear();
    }

    private static string SanitizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation)) return "operation";
        var trimmed = operation.Trim();
        if (trimmed.Length > 48) trimmed = trimmed[..48];
        foreach (var c in trimmed)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-')) return "operation";
        }
        return trimmed;
    }
}
