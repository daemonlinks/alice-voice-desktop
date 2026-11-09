using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// Mirrors Microsoft.Maui.Resizetizer <c>ResizeImageInfo.OutputName</c> and
/// <c>Utils.IsValidResourceFilename</c> (<c>DetectInvalidResourceOutputFilenamesTask</c>).
/// The publish error names the output stem, and <c>Link</c> replaces the source file name.
/// </summary>
public class MauiResourceNameTests
{
    // Utils.rxResourceFilenameValidation in Microsoft.Maui.Resizetizer.
    private static readonly Regex MauiResourceName = new(@"^[a-z]([a-z0-9_]*[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsValidMauiResourceName(string name) => MauiResourceName.IsMatch(name);

    [Theory]
    [InlineData("appicon", true)]
    [InlineData("splash", true)]
    [InlineData("tv_banner", true)]
    [InlineData("appicon_round", true)]
    [InlineData("a", true)]
    [InlineData("alice-voice-master", false)]
    [InlineData("alice-voice", false)]
    [InlineData("AppIcon", false)]
    [InlineData("appicon_", false)]
    [InlineData("_appicon", false)]
    [InlineData("9icon", false)]
    [InlineData("app icon", false)]
    public void ResizetizerNameRuleMatchesThePublishError(string name, bool valid)
        => Assert.Equal(valid, IsValidMauiResourceName(name));

    [Fact]
    public void IconAndSplashOutputNamesAreLegalAndDistinct()
    {
        var projectDir = AppProjectDir();
        var project = XDocument.Load(Path.Combine(projectDir, "AliceVoice.Universal.App.csproj"));
        var images = project.Descendants()
            .Where(element => element.Name.LocalName is "MauiIcon" or "MauiSplashScreen" or "MauiImage" or "MauiFont")
            .Select(element => Output(element, projectDir))
            .ToList();

        Assert.NotEmpty(images);
        Assert.All(images, image =>
        {
            Assert.True(File.Exists(image.SourcePath), image.SourcePath);
            Assert.True(IsValidMauiResourceName(image.OutputName),
                $"{image.Kind} output '{image.OutputName}' from {image.SourcePath} is not a MAUI resource name.");
        });
        Assert.Equal(images.Count, images.Select(image => image.OutputName).Distinct(StringComparer.Ordinal).Count());

        var icon = Assert.Single(images, image => image.Kind == "MauiIcon");
        var splash = Assert.Single(images, image => image.Kind == "MauiSplashScreen");
        Assert.Equal("appicon", icon.OutputName);
        Assert.Equal("splash", splash.OutputName);
        Assert.EndsWith("alice-voice-master.png", icon.SourcePath.Replace('\\', '/'));
        Assert.EndsWith("alice-voice-master.png", splash.SourcePath.Replace('\\', '/'));
        Assert.False(IsValidMauiResourceName(Path.GetFileNameWithoutExtension(icon.SourcePath)));
    }

    [Fact]
    public void AndroidIconBannerAndSplashReferencesUseThoseOutputNames()
    {
        var projectDir = AppProjectDir();
        var android = Path.Combine(projectDir, "Platforms", "Android");
        var resourceNames = Directory.EnumerateFiles(Path.Combine(android, "Resources"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToList();
        Assert.All(resourceNames, name => Assert.True(IsValidMauiResourceName(name), name));
        Assert.Contains("tv_banner", resourceNames);

        var iconOutput = "appicon";
        var text = File.ReadAllText(Path.Combine(android, "AndroidManifest.xml"))
            + File.ReadAllText(Path.Combine(android, "Resources", "drawable", "tv_banner.xml"));
        var refs = Regex.Matches(text, @"@(?:mipmap|drawable)/([A-Za-z0-9_]+)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.Contains(iconOutput, refs);
        Assert.Contains(iconOutput + "_round", refs);
        Assert.Contains("tv_banner", refs);
        Assert.All(refs, name => Assert.True(IsValidMauiResourceName(name), name));
        Assert.DoesNotContain(refs, name => name.Contains('-'));
    }

    private static ImageOutput Output(XElement item, string projectDir)
    {
        var include = item.Attribute("Include")?.Value ?? "";
        var link = item.Attribute("Link")?.Value;
        var foreground = item.Attribute("ForegroundFile")?.Value;
        var isIcon = item.Name.LocalName == "MauiIcon";
        string? filename = include;
        if (isIcon && string.IsNullOrWhiteSpace(foreground))
        {
            foreground = filename;
            filename = null;
        }

        var output = !string.IsNullOrWhiteSpace(link)
            ? Stem(link)
            : !string.IsNullOrWhiteSpace(filename)
                ? Stem(filename)
                : Stem(foreground ?? "");
        var source = Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar)));
        return new ImageOutput(item.Name.LocalName, output, source);
    }

    private static string Stem(string path)
        => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));

    private static string AppProjectDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "universal", "src", "AliceVoice.Universal.App");
            if (File.Exists(Path.Combine(candidate, "AliceVoice.Universal.App.csproj"))) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("App U project was not found above the test output.");
    }

    private sealed record ImageOutput(string Kind, string OutputName, string SourcePath);
}
