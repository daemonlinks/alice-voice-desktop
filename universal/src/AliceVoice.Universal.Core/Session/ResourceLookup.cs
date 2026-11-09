namespace AliceVoice.Universal.Session;

/// <summary>
/// Resolves a resource key across scopes. A page dictionary does not contain
/// keys that were merged into the application dictionary; callers must pass
/// the page scope first and the application scope after it. Merged children
/// are searched last-wins, and a cycle cannot recurse.
/// A missing key returns false. It does not throw.
/// </summary>
public static class ResourceLookup
{
    public static bool TryFind<T>(IEnumerable<T>? scopes, string key, Func<T, string, object?> readLocal, Func<T, IEnumerable<T>> merged, out object? value)
        where T : class
    {
        value = null;
        if (scopes is null || string.IsNullOrEmpty(key)) return false;
        var seen = new List<T>();
        foreach (var scope in scopes)
        {
            if (Walk(scope, key, readLocal, merged, seen, out value)) return true;
        }
        return false;
    }

    private static bool Walk<T>(T? node, string key, Func<T, string, object?> readLocal, Func<T, IEnumerable<T>> merged, List<T> seen, out object? value)
        where T : class
    {
        value = null;
        if (node is null) return false;
        for (var i = 0; i < seen.Count; i++)
            if (ReferenceEquals(seen[i], node)) return false;
        seen.Add(node);

        var local = readLocal(node, key);
        if (local is not null)
        {
            value = local;
            return true;
        }

        var children = merged(node)?.ToList();
        if (children is null || children.Count == 0) return false;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (Walk(children[i], key, readLocal, merged, seen, out value)) return true;
        }
        return false;
    }
}
