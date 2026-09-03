using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NetVips;

namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// Builds library collages with libvips, mirroring <c>StripCollageBuilder</c>.
/// </summary>
internal static class VipsCollageBuilder
{
    /// <summary>
    /// The font size Skia draws the library name at before it is scaled down to fit.
    /// </summary>
    private const int LibraryNameFontSize = 112;

    /// <summary>
    /// Alpha of the black scrim drawn over the backdrop so that the library name stays readable.
    /// </summary>
    private const int ScrimAlpha = 0x78;

    /// <summary>
    /// Creates a 2x2 collage.
    /// </summary>
    /// <param name="paths">The paths of the images to use.</param>
    /// <param name="outputPath">Where to write the collage.</param>
    /// <param name="width">The collage width.</param>
    /// <param name="height">The collage height.</param>
    public static void BuildSquareCollage(IReadOnlyList<string> paths, string outputPath, int width, int height)
    {
        var cellWidth = width / 2;
        var cellHeight = height / 2;
        var cursor = new VipsImageCursor(paths);

        var cells = new Image[4];
        try
        {
            // Skia fills the grid column first, so cell order there is top-left, bottom-left,
            // top-right, bottom-right. Arrayjoin lays out row first, hence the permutation.
            var order = new[] { 0, 2, 1, 3 };
            for (var i = 0; i < cells.Length; i++)
            {
                var candidate = cursor.Next();
                cells[order[i]] = candidate is null
                    ? Image.Black(cellWidth, cellHeight, bands: 3).Copy(interpretation: Enums.Interpretation.Srgb)
                    : Resize(candidate.Value.Path, cellWidth, cellHeight);
            }

            using var grid = Image.Arrayjoin(cells, across: 2);

            // An odd width or height leaves the grid a pixel short of the requested size.
            using var padded = grid.Embed(0, 0, width, height, extend: Enums.Extend.Black);
            Write(padded, outputPath);
        }
        finally
        {
            foreach (var cell in cells)
            {
                cell?.Dispose();
            }
        }
    }

    /// <summary>
    /// Creates a thumb collage: one backdrop, dimmed, with the library name across the middle.
    /// </summary>
    /// <param name="paths">The paths of the images to use.</param>
    /// <param name="outputPath">Where to write the collage.</param>
    /// <param name="width">The collage width.</param>
    /// <param name="height">The collage height.</param>
    /// <param name="libraryName">The library name to draw, if any.</param>
    public static void BuildThumbCollage(IReadOnlyList<string> paths, string outputPath, int width, int height, string? libraryName)
    {
        var cursor = new VipsImageCursor(paths);
        var backdrop = cursor.Next();

        if (backdrop is null)
        {
            using var empty = Image.Black(width, height, bands: 3).Copy(interpretation: Enums.Interpretation.Srgb);
            Write(empty, outputPath);
            return;
        }

        // Keep the backdrop's aspect ratio and pin it to the top left, exactly as Skia does; anything
        // the backdrop does not cover stays black.
        var backdropHeight = Math.Abs(width * backdrop.Value.Height / backdrop.Value.Width);
        using var resized = Resize(backdrop.Value.Path, width, backdropHeight);
        using var canvas = resized.Embed(0, 0, width, height, extend: Enums.Extend.Black);

        // Compositing black at alpha 0x78 over a pixel leaves pixel * (1 - 0x78/255), so the scrim is
        // just a linear scale of the colour bands.
        using var dimmed = Scale(canvas, 1.0 - (ScrimAlpha / 255.0));

        if (string.IsNullOrWhiteSpace(libraryName))
        {
            Write(dimmed, outputPath);
            return;
        }

        using var label = RenderLibraryName(libraryName, width);
        if (label is null)
        {
            Write(dimmed, outputPath);
            return;
        }

        using var titled = dimmed.Composite2(
            label,
            Enums.BlendMode.Over,
            x: (width - label.Width) / 2,
            y: (height - label.Height) / 2);

        Write(titled, outputPath);
    }

    /// <summary>
    /// Renders the library name as a white RGBA image, scaled down to fit the collage width.
    /// </summary>
    /// <remarks>
    /// Pango does bidi, shaping and font fallback here. That replaces the whole
    /// <c>StripCollageBuilder.DrawText</c> path in the Skia encoder - the RTL regex, the grapheme
    /// cluster walk and the hand-maintained typeface list - none of which have an equivalent here
    /// because they are not needed.
    /// </remarks>
    private static Image? RenderLibraryName(string libraryName, int width)
    {
        try
        {
            var mask = Image.Text(
                libraryName,
                font: FormattableString.Invariant($"sans-serif Bold {LibraryNameFontSize}"),
                dpi: 72,
                rgba: false);

            // Same two pass fit as Skia: measure, and if the text overruns 95% of the collage, redraw
            // it at the size that lands on 90%.
            if (mask.Width > width * 0.95)
            {
                var fitted = Math.Max(
                    1,
                    (int)(0.9 * width * LibraryNameFontSize / (double)mask.Width));

                mask.Dispose();
                mask = Image.Text(
                    libraryName,
                    font: FormattableString.Invariant($"sans-serif Bold {fitted}"),
                    dpi: 72,
                    rgba: false);
            }

            using (mask)
            {
                using var white = mask.NewFromImage(new double[] { 0xFF, 0xFF, 0xFF });
                return white.Bandjoin(mask).Copy(interpretation: Enums.Interpretation.Srgb);
            }
        }
        catch (VipsException)
        {
            // Text rendering needs Pango and fontconfig; a stripped libvips build has neither.
            return null;
        }
    }

    /// <summary>
    /// Resizes an image on disk to exactly the given size, shrinking on load where possible.
    /// </summary>
    /// <param name="path">The image to resize.</param>
    /// <param name="width">The target width.</param>
    /// <param name="height">The target height.</param>
    /// <returns>The resized image, as opaque sRGB.</returns>
    internal static Image Resize(string path, int width, int height)
    {
        using var source = Source.NewFromFile(path);
        using var thumbnail = Image.ThumbnailSource(
            source,
            width,
            string.Empty,
            height: height,
            size: Enums.Size.Force);

        // Collages composite many images of mixed provenance; normalising to opaque sRGB up front
        // keeps Arrayjoin and Composite2 from having to reconcile band counts later.
        using var opaque = thumbnail.HasAlpha() ? thumbnail.Flatten() : thumbnail.Copy();
        return opaque.Colourspace(Enums.Interpretation.Srgb).Cast(Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// Scales the colour bands of an image, leaving any alpha band untouched.
    /// </summary>
    /// <param name="image">The image to scale.</param>
    /// <param name="factor">The factor to scale the colour bands by.</param>
    /// <returns>The scaled image.</returns>
    internal static Image Scale(Image image, double factor)
    {
        var colorBands = image.HasAlpha() ? image.Bands - 1 : image.Bands;
        var scale = new double[image.Bands];
        for (var i = 0; i < scale.Length; i++)
        {
            scale[i] = i < colorBands ? factor : 1;
        }

        return image.Linear(scale, new double[image.Bands]).Cast(Enums.BandFormat.Uchar);
    }

    /// <summary>
    /// Writes an image, picking the format from the output path's extension as Skia does.
    /// </summary>
    /// <param name="image">The image to write.</param>
    /// <param name="outputPath">Where to write it.</param>
    internal static void Write(Image image, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var extension = Path.GetExtension(outputPath.AsSpan());
        var (suffix, options) = extension switch
        {
            _ when extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                => (".jpg", new VOption { { "Q", 90 }, { "subsample_mode", Enums.ForeignSubsample.On } }),
            _ when extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                => (".webp", new VOption { { "Q", 90 } }),

            // Skia also writes GIF and BMP here, but libvips cannot save either from the prebuilt
            // binaries. Callers ask for PNG in practice, and PNG is the safe fallback for the rest.
            _ => (".png", new VOption()),
        };

        using var target = Target.NewToFile(outputPath);
        image.WriteToTarget(target, suffix, options);
    }
}
