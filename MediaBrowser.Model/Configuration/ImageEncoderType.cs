namespace MediaBrowser.Model.Configuration;

/// <summary>
/// Enum ImageEncoderType.
/// </summary>
public enum ImageEncoderType
{
    /// <summary>
    /// Encode images with SkiaSharp. The default.
    /// </summary>
    Skia = 0,

    /// <summary>
    /// Encode images with libvips through NetVips. Faster and considerably lighter on memory, and
    /// adds HEIC/AVIF/TIFF input, at the cost of losing BMP and ICO input.
    /// </summary>
    NetVips = 1,
}
