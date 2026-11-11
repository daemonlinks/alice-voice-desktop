namespace AliceVoice.Universal.Voice;

/// <summary>
/// Uniform / AspectFit placement. The whole image stays inside the view.
/// A wider view pillarboxes (bars on the left and right). A taller view
/// letterboxes (bars on the top and bottom). The image is never cropped.
/// </summary>
public static class BackgroundFit
{
    /// <summary>MAUI <c>Aspect</c> value. Not AspectFill, not Fill.</summary>
    public const string Aspect = "AspectFit";

    public readonly record struct Placement(double Width, double Height, double X, double Y);

    public static Placement Place(double imageWidth, double imageHeight, double viewWidth, double viewHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewWidth <= 0 || viewHeight <= 0)
            return new Placement(0, 0, 0, 0);
        // min, not max: max is UniformToFill and crops one axis.
        var scale = Math.Min(viewWidth / imageWidth, viewHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new Placement(width, height, (viewWidth - width) / 2, (viewHeight - height) / 2);
    }
}
