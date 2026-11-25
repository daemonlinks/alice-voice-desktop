using AliceVoice.Universal.Media;
using AliceVoice.Universal.Tools;
using Xunit;

namespace AliceVoice.Universal.Core.Tests;

/// <summary>
/// App U's own tools: what the local brain may call, what it is told back, and that every other tool
/// is left alone for the Python bridge. The answers are pinned to the web page's shape, because the
/// point of this host is that local-me behaves the same in both products.
/// </summary>
public class AppToolHostTests
{
    private static (AppToolHost Host, ImageDisplay Images, string Root) Make()
    {
        var root = Directory.CreateTempSubdirectory("av-tool-").FullName;
        File.WriteAllBytes(Path.Combine(root, "beach-morning.png"), MediaTests.Png());
        var settings = new MediaSettings { Roots = new[] { root } };
        var images = new ImageDisplay(settings, new MediaShare(settings), null, _ => { });
        return (new AppToolHost(images), images, root);
    }

    [Fact]
    public void OnlyTheDisplayTrioIsOwned()
    {
        Assert.True(AppToolHost.Owns("gui_show_image"));
        Assert.True(AppToolHost.Owns("gui_image_clear"));
        Assert.True(AppToolHost.Owns("gui_list_pictures"));
        Assert.False(AppToolHost.Owns("honcho_search"));
        Assert.False(AppToolHost.Owns("gui_mood"));
    }

    [Fact]
    public async Task AnotherToolIsLeftForThePythonBridge()
    {
        var (host, images, root) = Make();
        using (images)
        {
            // Null, not an error: the caller must fall through to alice_local_tools.py unchanged.
            Assert.Null(await host.RunAsync("honcho_search", "{\"query\":\"x\"}"));
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ListAnswersInTheWebPagesShapeAndNeverHandsOverAPath()
    {
        var (host, images, root) = Make();
        using (images)
        {
            var reply = await host.RunAsync("gui_list_pictures", "{\"n\":5}");
            Assert.NotNull(reply);
            using var doc = System.Text.Json.JsonDocument.Parse(reply!);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
            var names = doc.RootElement.GetProperty("names").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(new[] { "beach-morning" }, names);
            Assert.Contains("gui_show_image", doc.RootElement.GetProperty("how").GetString());
            Assert.DoesNotContain(root, reply);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task AMissingNameIsRefusedInTheWebPagesWordsWithItsHint()
    {
        var (host, images, root) = Make();
        using (images)
        {
            var reply = await host.RunAsync("gui_show_image", "{\"file\":\"not-a-picture\"}");
            using var doc = System.Text.Json.JsonDocument.Parse(reply!);
            Assert.False(doc.RootElement.TryGetProperty("ok", out _));
            Assert.Equal("not a file", doc.RootElement.GetProperty("error").GetString());
            Assert.Contains("beach-morning", doc.RootElement.GetProperty("hint").GetString());
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ANamedPictureIsAcceptedAndThenActuallyPainted()
    {
        var (host, images, root) = Make();
        using (images)
        {
            var reply = await host.RunAsync("gui_show_image", "{\"file\":\"beach-morning\",\"mode\":\"stage\",\"caption\":\"hello\"}");
            using (var doc = System.Text.Json.JsonDocument.Parse(reply!))
                Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
            await images.WhenIdle();
            Assert.NotNull(images.State.Current);
        }
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task ANamedVideoIsRefusedUntilTheRendererExists()
    {
        var root = Directory.CreateTempSubdirectory("av-tool-v-").FullName;
        File.WriteAllBytes(Path.Combine(root, "clip.mp4"), new byte[] { 1, 2, 3 });
        var settings = new MediaSettings { Roots = new[] { root } };
        using var images = new ImageDisplay(settings, new MediaShare(settings), null, _ => { });
        var host = new AppToolHost(images);

        var reply = await host.RunAsync("gui_show_image", "{\"file\":\"clip\"}");
        using var doc = System.Text.Json.JsonDocument.Parse(reply!);
        // Pinned on purpose: when MediaKind.Video joins AllowedKinds this test is what changes.
        Assert.Equal("unsupported type", doc.RootElement.GetProperty("error").GetString());
        Directory.Delete(root, true);
    }
}
