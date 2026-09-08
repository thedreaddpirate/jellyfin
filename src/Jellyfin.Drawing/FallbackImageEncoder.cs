using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;

namespace Jellyfin.Drawing;

/// <summary>
/// Pairs a primary encoder with a secondary one that covers the input formats the primary cannot
/// read, so a server is not limited to the intersection of the two image libraries it already ships.
/// </summary>
public class FallbackImageEncoder : IImageEncoder
{
    private readonly IImageEncoder _primary;
    private readonly IImageEncoder _secondary;
    private readonly HashSet<string> _supportedInputFormats;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _primaryInputFormats;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _secondaryInputFormats;

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackImageEncoder"/> class.
    /// </summary>
    /// <param name="primary">The encoder to use wherever it can read the input.</param>
    /// <param name="secondary">The encoder to fall back to for the rest.</param>
    public FallbackImageEncoder(IImageEncoder primary, IImageEncoder secondary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);

        _primary = primary;
        _secondary = secondary;

        // Held as span lookups so routing an image does not have to allocate its extension.
        _primaryInputFormats = new HashSet<string>(primary.SupportedInputFormats, StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();
        _secondaryInputFormats = new HashSet<string>(secondary.SupportedInputFormats, StringComparer.OrdinalIgnoreCase)
            .GetAlternateLookup<ReadOnlySpan<char>>();

        _supportedInputFormats = new HashSet<string>(primary.SupportedInputFormats, StringComparer.OrdinalIgnoreCase);
        _supportedInputFormats.UnionWith(secondary.SupportedInputFormats);
    }

    /// <inheritdoc/>
    public string Name => _primary.Name + "+" + _secondary.Name;

    /// <inheritdoc/>
    public bool SupportsImageCollageCreation => _primary.SupportsImageCollageCreation;

    /// <inheritdoc/>
    public bool SupportsImageEncoding => _primary.SupportsImageEncoding;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> SupportedInputFormats => _supportedInputFormats;

    /// <summary>
    /// Gets the output formats of the primary encoder alone. The secondary only ever reads here, so
    /// offering a format it can write but the primary cannot would break every image that does not
    /// happen to fall through to it.
    /// </summary>
    public IReadOnlyCollection<ImageFormat> SupportedOutputFormats => _primary.SupportedOutputFormats;

    /// <inheritdoc/>
    public ImageDimensions GetImageSize(string path)
        => EncoderFor(path).GetImageSize(path);

    /// <inheritdoc/>
    public string GetImageBlurHash(int xComp, int yComp, string path)
        => EncoderFor(path).GetImageBlurHash(xComp, yComp, path);

    /// <inheritdoc/>
    public string EncodeImage(string inputPath, DateTime dateModified, string outputPath, bool autoOrient, ImageOrientation? orientation, int quality, ImageProcessingOptions options, ImageFormat outputFormat)
        => EncoderFor(inputPath).EncodeImage(inputPath, dateModified, outputPath, autoOrient, orientation, quality, options, outputFormat);

    /// <inheritdoc/>
    public void CreateImageCollage(ImageCollageOptions options, string? libraryName)
        => _primary.CreateImageCollage(options, libraryName);

    /// <inheritdoc/>
    public void CreateSplashscreen(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops)
        => _primary.CreateSplashscreen(posters, backdrops);

    /// <inheritdoc/>
    public int CreateTrickplayTile(ImageCollageOptions options, int quality, int imgWidth, int? imgHeight)
        => _primary.CreateTrickplayTile(options, quality, imgWidth, imgHeight);

    private IImageEncoder EncoderFor(string path)
    {
        var extension = Path.GetExtension(path.AsSpan()).TrimStart('.');
        if (_primaryInputFormats.Contains(extension))
        {
            return _primary;
        }

        return _secondaryInputFormats.Contains(extension) ? _secondary : _primary;
    }
}
