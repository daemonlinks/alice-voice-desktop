using System.Text.RegularExpressions;
using System.Xml.Linq;
using AliceVoice.Universal.Session;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

public class ResourceLookupTests
{
    private sealed class Node
    {
        public Dictionary<string, object> Own { get; } = new(StringComparer.Ordinal);
        public List<Node> Merged { get; } = new();
    }

    [Fact]
    public void ApplicationMergedStyleIsVisibleWhenThePageDictionaryDoesNotHaveIt()
    {
        var page = new Node();
        var colors = new Node();
        colors.Own["AppBg"] = "color";
        var styles = new Node();
        styles.Own["Chip"] = "chip-style";
        styles.Own["Dim"] = "dim-style";
        var application = new Node { Merged = { colors, styles } };

        Assert.True(ResourceLookup.TryFind(new[] { page, application }, "Chip", Read, static n => n.Merged, out var chip));
        Assert.Equal("chip-style", chip);
        Assert.True(ResourceLookup.TryFind(new[] { page, application }, "Dim", Read, static n => n.Merged, out var dim));
        Assert.Equal("dim-style", dim);
        Assert.True(ResourceLookup.TryFind(new[] { page, application }, "AppBg", Read, static n => n.Merged, out _));
    }

    [Fact]
    public void MissingKeyReturnsFalseAndAPageKeyWins()
    {
        var page = new Node();
        page.Own["Chip"] = "page-chip";
        var application = new Node();
        application.Own["Chip"] = "app-chip";

        Assert.False(ResourceLookup.TryFind(new[] { page, application }, "Nope", Read, static n => n.Merged, out _));
        Assert.True(ResourceLookup.TryFind(new[] { page, application }, "Chip", Read, static n => n.Merged, out var chip));
        Assert.Equal("page-chip", chip);
    }

    [Fact]
    public void LaterMergedDictionaryWinsAndACycleStops()
    {
        var first = new Node();
        first.Own["Chip"] = "first";
        var second = new Node();
        second.Own["Chip"] = "second";
        var application = new Node { Merged = { first, second } };
        Assert.True(ResourceLookup.TryFind(new[] { application }, "Chip", Read, static n => n.Merged, out var chip));
        Assert.Equal("second", chip);

        var left = new Node();
        var right = new Node();
        left.Merged.Add(right);
        right.Merged.Add(left);
        Assert.False(ResourceLookup.TryFind(new[] { left }, "Chip", Read, static n => n.Merged, out _));
    }

    [Fact]
    public void PageXamlAndCodeBehindOnlyUseDefinedResources()
    {
        var appDir = FindAppDirectory();
        var defined = Keys(Path.Combine(appDir, "Resources", "Styles", "Colors.xaml"))
            .Concat(Keys(Path.Combine(appDir, "Resources", "Styles", "Styles.xaml")))
            .ToHashSet(StringComparer.Ordinal);

        var referenced = StaticResources(Path.Combine(appDir, "MainPage.xaml"))
            .Concat(StaticResources(Path.Combine(appDir, "Resources", "Styles", "Styles.xaml")))
            .Concat(CodeStyles(Path.Combine(appDir, "MainPage.xaml.cs")))
            .ToList();

        var missing = referenced.Where(key => !defined.Contains(key)).Distinct().ToList();
        Assert.Empty(missing);

        var code = File.ReadAllText(Path.Combine(appDir, "MainPage.xaml.cs"));
        Assert.DoesNotContain("Resources[", code);
        Assert.Contains("FindStyle(\"Chip\")", code);
        Assert.Contains("FindStyle(\"Dim\")", code);
    }

    private static object? Read(Node node, string key) => node.Own.TryGetValue(key, out var value) ? value : null;

    private static IEnumerable<string> Keys(string path) =>
        XDocument.Load(path).Descendants().Attributes().Where(a => a.Name.LocalName == "Key").Select(a => a.Value);

    private static IEnumerable<string> StaticResources(string path)
    {
        var text = File.ReadAllText(path);
        return Regex.Matches(text, @"StaticResource\s+([A-Za-z0-9_]+)").Select(match => match.Groups[1].Value);
    }

    private static IEnumerable<string> CodeStyles(string path)
    {
        var text = File.ReadAllText(path);
        return Regex.Matches(text, @"FindStyle\(""([^""]+)""\)").Select(match => match.Groups[1].Value);
    }

    private static string FindAppDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "universal", "src", "AliceVoice.Universal.App");
            if (File.Exists(Path.Combine(candidate, "MainPage.xaml"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("AliceVoice.Universal.App was not found above the test output.");
    }
}
