using SloppyAnimatsu.Rendering;
using SkiaSharp;

namespace SloppyAnimatsu.Viewer;

/// <summary>Decodes texture image files (PNG, JPEG, ...) with SkiaSharp, which Avalonia already ships.</summary>
internal static class ImageDecoding
{
    public static DecodedImage? Decode(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec == null) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        return new DecodedImage(info.Width, info.Height, bitmap.Bytes);
    }
}
