using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BlurHashSharp;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using NetVips;
using VipsRuntime = NetVips.NetVips;

namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// Image encoder that uses libvips, through NetVips, to manipulate images.
///
/// A full replacement for <c>SkiaEncoder</c>: resizing, trickplay tiles, dimensions, blurhashes,
/// library collages and the splashscreen. libvips has no canvas API, so the drawn parts are built by
/// compositing instead - see <see cref="VipsCollageBuilder"/> and <see cref="VipsSplashscreenBuilder"/>.
/// </summary>
public class NetVipsEncoder : IImageEncoder
{
    /// <summary>
    /// The metadata field libvips exposes the EXIF orientation tag as.
    /// </summary>
    private const string OrientationField = "orientation";

    /// <summary>
    /// Any larger than 128x128 is too slow and there's no visually discernible difference.
    /// </summary>
    private const int BlurHashSize = 128;

    private static readonly HashSet<string> _supportedInputFormats = BuildSupportedInputFormats();

    private static readonly HashSet<ImageFormat> _supportedOutputFormats =
        new() { ImageFormat.Webp, ImageFormat.Jpg, ImageFormat.Png };

    private readonly ILogger<NetVipsEncoder> _logger;
    private readonly IServerApplicationPaths _appPaths;

    static NetVipsEncoder()
    {
        if (!IsNativeLibAvailable())
        {
            return;
        }

        // libvips caches recent operations so that repeating one is free. Jellyfin almost never asks
        // for the same image at the same size twice in a row, so the cache is pure resident memory
        // here - it is the usual cause of "libvips leaks" reports from long running servers.
        Cache.Max = 0;
        Cache.MaxMem = 0;

        // Refuse the loaders libvips itself marks untrusted, so a hostile file in a library cannot
        // reach a decoder that nobody audits.
        VipsRuntime.BlockUntrusted = true;

        // svgload is one of those, but scrapers routinely save SVG artwork - clearlogos especially -
        // under a .jpg or .png name, and librsvg is the only way to read it. The exposure is narrow:
        // librsvg refuses entity expansion and external entities, loading through a Source leaves it
        // without a base URI so it resolves no external references at all, and every render below
        // goes through thumbnail, which rasterises at the target size rather than at whatever canvas
        // the file declares. Must come after the line above; the global set would re-block it.
        Operation.Block("VipsForeignLoadSvg", false);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NetVipsEncoder"/> class.
    /// </summary>
    /// <param name="logger">The application logger.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    public NetVipsEncoder(ILogger<NetVipsEncoder> logger, IServerConfigurationManager serverConfigurationManager)
    {
        _logger = logger;

        ArgumentNullException.ThrowIfNull(serverConfigurationManager);
        _appPaths = serverConfigurationManager.ApplicationPaths;

        // Every libvips pipeline runs on its own thread pool. Jellyfin already fans encodes out over
        // ParallelImageEncodingLimit workers, so leaving the libvips default in place oversubscribes
        // the box by that factor. Divide the cores across the workers instead.
        var parallelEncodes = serverConfigurationManager.Configuration.ParallelImageEncodingLimit;
        if (parallelEncodes <= 0)
        {
            parallelEncodes = Environment.ProcessorCount;
        }

        VipsRuntime.Concurrency = Math.Max(1, Environment.ProcessorCount / parallelEncodes);

        _logger.LogDebug(
            "libvips {Version} initialized with concurrency {Concurrency}",
            VipsRuntime.Version(0) + "." + VipsRuntime.Version(1) + "." + VipsRuntime.Version(2),
            VipsRuntime.Concurrency);
    }

    /// <inheritdoc/>
    public string Name => "NetVips";

    /// <inheritdoc/>
    public bool SupportsImageCollageCreation => true;

    /// <inheritdoc/>
    public bool SupportsImageEncoding => true;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> SupportedInputFormats => _supportedInputFormats;

    /// <inheritdoc/>
    public IReadOnlyCollection<ImageFormat> SupportedOutputFormats => _supportedOutputFormats;

    /// <summary>
    /// Check if the native lib is available.
    /// </summary>
    /// <returns>True if the native lib is available, otherwise false.</returns>
    public static bool IsNativeLibAvailable() => ModuleInitializer.VipsInitialized;

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException">The path is not valid.</exception>
    public ImageDimensions GetImageSize(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        using var source = Source.NewFromFile(path);
        using var image = Image.NewFromSource(source, access: Enums.Access.Sequential);

        // Header read only - libvips does not touch pixel data until it is asked for. These are the
        // stored dimensions, not the EXIF-oriented ones, which is what SkiaEncoder reports too.
        return new ImageDimensions(image.Width, image.Height);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    public string GetImageBlurHash(int xComp, int yComp, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var extension = Path.GetExtension(path.AsSpan()).TrimStart('.').ToString();
        if (!_supportedInputFormats.Contains(extension))
        {
            _logger.LogDebug("Unable to compute blur hash due to unsupported format: {ImagePath}", path);
            return string.Empty;
        }

        using var source = Source.NewFromFile(path);

        // Shrink on load: for a JPEG this decodes at a reduced DCT scale rather than decoding the
        // full image and throwing most of it away.
        using var thumbnail = Image.ThumbnailSource(
            source,
            BlurHashSize,
            string.Empty,
            height: BlurHashSize,
            size: Enums.Size.Down);

        // Colourspace promotes greyscale to three bands and keeps any alpha as a trailing band.
        using var srgb = thumbnail.Colourspace(Enums.Interpretation.Srgb).Cast(Enums.BandFormat.Uchar);

        // Discard alpha rather than flattening it. vips_thumbnail hands back unpremultiplied pixels,
        // so the colour bands are the image's own colours; compositing them onto black instead would
        // darken the hash by the alpha factor, and SkiaSharp does not do that either.
        using var rgb = srgb.ExtractBand(0, n: 3);
        var pixels = rgb.WriteToMemory<byte>();

        return CoreBlurHashEncoder.Encode(
            xComp,
            yComp,
            rgb.Width,
            rgb.Height,
            pixels,
            rgb.Width * rgb.Bands,
            PixelFormat.RGB888);
    }

    /// <inheritdoc />
    public string EncodeImage(string inputPath, DateTime dateModified, string outputPath, bool autoOrient, ImageOrientation? orientation, int quality, ImageProcessingOptions options, ImageFormat outputFormat)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var inputFormat = Path.GetExtension(inputPath.AsSpan()).TrimStart('.').ToString();
        if (!_supportedInputFormats.Contains(inputFormat))
        {
            _logger.LogDebug("Unable to encode image due to unsupported format: {ImagePath}", inputPath);
            return inputPath;
        }

        if (!_supportedOutputFormats.Contains(outputFormat))
        {
            throw new ArgumentException($"Requested {outputFormat} output, which libvips cannot write", nameof(outputFormat));
        }

        var hasBackgroundColor = !string.IsNullOrWhiteSpace(options.BackgroundColor);
        var hasForegroundColor = !string.IsNullOrWhiteSpace(options.ForegroundLayer);
        var blur = options.Blur ?? 0;
        var hasIndicator = options.UnplayedCount.HasValue || !options.PercentPlayed.Equals(0);

        var (storedSize, storedOrientation) = ReadHeader(inputPath);

        // The file's own EXIF tag wins; the caller supplied orientation is only a fallback for files
        // that have none, which matches how SkiaEncoder treats it.
        var effectiveOrientation = storedOrientation ?? orientation;
        var rotatesAxes = autoOrient && effectiveOrientation is ImageOrientation.LeftTop
            or ImageOrientation.RightTop or ImageOrientation.RightBottom or ImageOrientation.LeftBottom;

        var originalImageSize = rotatesAxes
            ? new ImageDimensions(storedSize.Height, storedSize.Width)
            : storedSize;

        if (options.HasDefaultOptions(inputPath, originalImageSize) && !autoOrient)
        {
            // Just spit out the original file if all the options are default
            return inputPath;
        }

        var newImageSize = ImageHelper.GetNewImageSize(options, originalImageSize);
        var width = newImageSize.Width;
        var height = newImageSize.Height;

        using var source = Source.NewFromFile(inputPath);

        // Note that SkiaEncoder.SharpenInPlace is deliberately not ported. That kernel compensates for
        // Skia's mipmap+linear downscale; libvips reduces with a Lanczos3 kernel that does not need
        // the compensation, and stacking the two produces halos. Output is marginally crisper here.
        var image = Resize(source, width, height, autoOrient, storedOrientation, effectiveOrientation);

        try
        {
            if (blur > 0)
            {
                var blurred = image.Gaussblur(blur);
                image.Dispose();
                image = blurred;
            }

            // Only meaningful where there is transparency to flatten; as with Skia, an opaque image
            // simply covers the background.
            if (hasBackgroundColor && image.HasAlpha())
            {
                var flattened = image.Flatten(ParseColor(options.BackgroundColor));
                image.Dispose();
                image = flattened;
            }

            if (hasForegroundColor)
            {
                if (!double.TryParse(options.ForegroundLayer, CultureInfo.InvariantCulture, out var opacity))
                {
                    opacity = .4;
                }

                var darkened = ApplyForegroundLayer(image, opacity);
                image.Dispose();
                image = darkened;
            }

            if (hasIndicator)
            {
                image = DrawIndicator(image, options);
            }

            var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
            Directory.CreateDirectory(directory);

            var (suffix, saveOptions) = GetSaveOptions(outputFormat, quality);

            // Target rather than WriteToFile: libvips parses "name[option=value]" out of filenames, and
            // library paths routinely contain square brackets.
            using var target = Target.NewToFile(outputPath);
            image.WriteToTarget(target, suffix, saveOptions);
        }
        finally
        {
            image.Dispose();
        }

        return outputPath;
    }

    /// <inheritdoc />
    public int CreateTrickplayTile(ImageCollageOptions options, int quality, int imgWidth, int? imgHeight)
    {
        ArgumentNullException.ThrowIfNull(options);

        var paths = options.InputPaths;
        var tileWidth = options.Width;
        var tileHeight = options.Height;

        if (paths.Count < 1)
        {
            throw new ArgumentException("InputPaths cannot be empty.");
        }

        if (paths.Count > tileWidth * tileHeight)
        {
            throw new ArgumentException($"InputPaths contains more images than would fit on {tileWidth}x{tileHeight} grid.");
        }

        var tileHeightPx = imgHeight;
        var sources = new List<Source>(paths.Count);
        var tiles = new List<Image>(paths.Count);

        try
        {
            foreach (var path in paths)
            {
                var source = Source.NewFromFile(path);
                sources.Add(source);

                // Sequential access lets libvips stream each thumbnail through the join without ever
                // materialising it, so peak memory is a few scanlines rather than the whole grid.
                var tile = Image.NewFromSource(source, access: Enums.Access.Sequential);
                tiles.Add(tile);

                if (tile.Width != imgWidth)
                {
                    throw new InvalidOperationException("Image width does not match provided width.");
                }

                // If no height was provided, use the height of the first image.
                tileHeightPx ??= tile.Height;

                if (tile.Height != tileHeightPx)
                {
                    throw new InvalidOperationException("Image height does not match first image height.");
                }
            }

            using var grid = Image.Arrayjoin(tiles.ToArray(), across: tileWidth);
            using var target = Target.NewToFile(options.OutputPath);
            var (suffix, saveOptions) = GetSaveOptions(ImageFormat.Jpg, quality);
            grid.WriteToTarget(target, suffix, saveOptions);
        }
        finally
        {
            tiles.ForEach(static tile => tile.Dispose());
            sources.ForEach(static source => source.Dispose());
        }

        return tileHeightPx!.Value;
    }

    /// <inheritdoc />
    public void CreateImageCollage(ImageCollageOptions options, string? libraryName)
    {
        ArgumentNullException.ThrowIfNull(options);

        double ratio = (double)options.Width / options.Height;

        if (ratio >= 1.4)
        {
            VipsCollageBuilder.BuildThumbCollage(options.InputPaths, options.OutputPath, options.Width, options.Height, libraryName);
        }
        else
        {
            // TODO: Create Poster collage capability, as with the Skia encoder.
            VipsCollageBuilder.BuildSquareCollage(options.InputPaths, options.OutputPath, options.Width, options.Height);
        }
    }

    /// <inheritdoc />
    public void CreateSplashscreen(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops)
    {
        ArgumentNullException.ThrowIfNull(posters);
        ArgumentNullException.ThrowIfNull(backdrops);

        // Only generate the splash screen if we have at least one poster and at least one backdrop.
        if (posters.Count == 0 || backdrops.Count == 0)
        {
            return;
        }

        var outputPath = Path.Combine(_appPaths.DataPath, "splashscreen.png");

        try
        {
            VipsSplashscreenBuilder.Generate(posters, backdrops, outputPath);
        }
        catch (Exception ex) when (ex is VipsException or ArgumentException)
        {
            // Called unconditionally at the end of a library scan, so a failure here must not take
            // the scan down with it.
            _logger.LogError(ex, "Error generating splashscreen");
        }
    }

    /// <summary>
    /// Resizes the source image to exactly the requested size, shrinking on load where the format
    /// allows it.
    /// </summary>
    private static Image Resize(
        Source source,
        int width,
        int height,
        bool autoOrient,
        ImageOrientation? storedOrientation,
        ImageOrientation? effectiveOrientation)
    {
        // The common case: either no rotation is wanted, or the file carries its own EXIF tag and
        // thumbnail can apply it while it resizes.
        if (!autoOrient || storedOrientation is not null || effectiveOrientation is null or ImageOrientation.TopLeft)
        {
            return Image.ThumbnailSource(
                source,
                width,
                string.Empty,
                height: height,
                size: Enums.Size.Force,
                noRotate: !autoOrient);
        }

        // The file has no EXIF orientation but the caller knows one. Rotate first so the forced
        // target size is applied to the upright image; this path gives up shrink on load.
        using var loaded = Image.NewFromSource(source, access: Enums.Access.Sequential);
        using var upright = ApplyOrientation(loaded, effectiveOrientation.Value);

        return upright.ThumbnailImage(width, height: height, size: Enums.Size.Force, noRotate: true);
    }

    /// <summary>
    /// Applies an EXIF orientation to an image.
    /// </summary>
    private static Image ApplyOrientation(Image image, ImageOrientation orientation)
    {
        switch (orientation)
        {
            case ImageOrientation.TopRight:
                return image.Flip(Enums.Direction.Horizontal);
            case ImageOrientation.BottomRight:
                return image.Rot(Enums.Angle.D180);
            case ImageOrientation.BottomLeft:
                return image.Flip(Enums.Direction.Vertical);
            case ImageOrientation.LeftTop:
                return RotateThenFlip(image, Enums.Angle.D90);
            case ImageOrientation.RightTop:
                return image.Rot(Enums.Angle.D90);
            case ImageOrientation.RightBottom:
                return RotateThenFlip(image, Enums.Angle.D270);
            case ImageOrientation.LeftBottom:
                return image.Rot(Enums.Angle.D270);
            default:
                return image.Copy();
        }
    }

    private static Image RotateThenFlip(Image image, Enums.Angle angle)
    {
        using var rotated = image.Rot(angle);
        return rotated.Flip(Enums.Direction.Horizontal);
    }

    /// <summary>
    /// Darkens the image the way Skia's translucent black foreground layer does.
    /// </summary>
    private static Image ApplyForegroundLayer(Image image, double opacity)
    {
        // Compositing opaque black at alpha (1 - opacity) over a pixel leaves pixel * opacity, so the
        // whole layer collapses to a scale of the colour bands.
        var colorBands = image.HasAlpha() ? image.Bands - 1 : image.Bands;
        var scale = new double[image.Bands];
        for (var i = 0; i < scale.Length; i++)
        {
            scale[i] = i < colorBands ? opacity : 1;
        }

        return image.Linear(scale, new double[image.Bands]).Cast(Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// Reads the dimensions and EXIF orientation without decoding pixel data.
    /// </summary>
    private static (ImageDimensions Size, ImageOrientation? Orientation) ReadHeader(string path)
    {
        using var source = Source.NewFromFile(path);
        using var image = Image.NewFromSource(source, access: Enums.Access.Sequential);

        ImageOrientation? orientation = null;
        if (image.Contains(OrientationField)
            && image.Get(OrientationField) is int tag
            && tag is >= (int)ImageOrientation.TopLeft and <= (int)ImageOrientation.LeftBottom)
        {
            orientation = (ImageOrientation)tag;
        }

        return (new ImageDimensions(image.Width, image.Height), orientation);
    }

    /// <summary>
    /// Converts a CSS style hex colour into the RGB triple libvips wants.
    /// </summary>
    private static double[] ParseColor(string color)
    {
        // #RGB, #RRGGBB and #AARRGGBB, which is what the image API actually sends. Unlike
        // SKColor.Parse this does not accept CSS colour names; alpha is dropped because the value is
        // only ever used as a flatten background.
        var hex = color.AsSpan().TrimStart('#');
        if (hex.Length == 3
            && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortRgb))
        {
            return
            [
                ((shortRgb >> 8) & 0xF) * 0x11,
                ((shortRgb >> 4) & 0xF) * 0x11,
                (shortRgb & 0xF) * 0x11
            ];
        }

        if (hex.Length >= 6
            && int.TryParse(hex[^6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return [(rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF];
        }

        return [0, 0, 0];
    }

    private static (string Suffix, VOption Options) GetSaveOptions(ImageFormat format, int quality)
        => format switch
        {
            // libvips turns chroma subsampling off above Q90, which roughly doubles the size of every
            // cached poster compared to Skia. Force it on so switching encoders does not quietly
            // inflate the image cache.
            ImageFormat.Jpg => (".jpg", new VOption { { "Q", quality }, { "subsample_mode", Enums.ForeignSubsample.On } }),
            ImageFormat.Webp => (".webp", new VOption { { "Q", quality } }),
            _ => (".png", new VOption()),
        };

    /// <summary>
    /// Builds the input format list from the loaders the running libvips actually has, rather than
    /// assuming a build configuration.
    /// </summary>
    private static HashSet<string> BuildSupportedInputFormats()
    {
        var formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!IsNativeLibAvailable())
        {
            return formats;
        }

        void AddIf(string loader, params string[] extensions)
        {
            if (VipsRuntime.TypeFind("VipsOperation", loader) != IntPtr.Zero)
            {
                formats.UnionWith(extensions);
            }
        }

        AddIf("jpegload", "jpeg", "jpg");
        AddIf("pngload", "png");
        AddIf("webpload", "webp");
        AddIf("gifload", "gif");
        AddIf("tiffload", "tif", "tiff");

        // Formats SkiaSharp cannot read at all.
        AddIf("heifload", "heic", "heif", "avif");
        AddIf("jxlload", "jxl");
        AddIf("svgload", "svg");

        // BMP and ICO only exist behind the ImageMagick fallback, which the prebuilt NetVips.Native
        // binaries do not ship. Losing them is the one input regression against SkiaSharp.
        AddIf("magickload", "bmp", "ico");

        return formats;
    }

    /// <summary>
    /// Composites the played-state indicators onto the image.
    /// </summary>
    private Image DrawIndicator(Image image, ImageProcessingOptions options)
    {
        try
        {
            if (options.UnplayedCount.HasValue)
            {
                var drawn = VipsIndicatorDrawer.DrawUnplayedCount(image, options.UnplayedCount.Value);
                image.Dispose();
                image = drawn;
            }

            if (options.PercentPlayed > 0)
            {
                var drawn = VipsIndicatorDrawer.DrawPercentPlayed(image, options.PercentPlayed);
                image.Dispose();
                image = drawn;
            }
        }
        catch (VipsException ex)
        {
            // The overlays are cosmetic, so a failure here must not cost the caller its image.
            _logger.LogError(ex, "Error drawing indicator overlay");
        }

        return image;
    }
}
