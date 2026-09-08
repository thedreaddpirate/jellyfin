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
    /// adds HEIC/AVIF/TIFF input. Nothing is lost by choosing it: libvips reads neither BMP, ICO nor
    /// any raw format, so the server keeps Skia alongside it and hands those files over.
    /// </summary>
    NetVips = 1,
}
