using System;
using System.Globalization;
using NetVips;

namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// Draws the played-state indicators that <c>SkiaEncoder</c> paints with a canvas.
///
/// libvips has no canvas, so each indicator is built as a small standalone RGBA image and
/// composited over the resized image instead. The geometry and colours are deliberately kept
/// identical to <c>PercentPlayedDrawer</c> and <c>UnplayedCountIndicator</c> so that switching
/// encoders does not visibly change existing posters.
/// </summary>
internal static class VipsIndicatorDrawer
{
    private const int PercentPlayedHeight = 8;
    private const int UnplayedOffsetFromTopRightCorner = 38;
    private const int UnplayedRadius = 20;

    /// <summary>
    /// Draws a percentage-played bar along the bottom edge of the image.
    /// </summary>
    /// <param name="image">The image to draw on.</param>
    /// <param name="percent">The percentage played.</param>
    /// <returns>The image with the indicator composited over it.</returns>
    public static Image DrawPercentPlayed(Image image, double percent)
    {
        var endX = image.Width - 1;
        var endY = image.Height - 1;
        if (endX <= 0 || endY <= PercentPlayedHeight)
        {
            return image;
        }

        var top = endY - PercentPlayedHeight;

        // #99000000 - the unplayed remainder of the bar.
        using var background = SolidColor(endX, PercentPlayedHeight, 0x00, 0x00, 0x00, 0x99);
        var result = image.Composite2(background, Enums.BlendMode.Over, x: 0, y: top);

        var foregroundWidth = Convert.ToInt32(endX * percent / 100);
        if (foregroundWidth <= 0)
        {
            return result;
        }

        // #FF00A4DC - the played portion.
        using var previous = result;
        using var foreground = SolidColor(foregroundWidth, PercentPlayedHeight, 0x00, 0xA4, 0xDC, 0xFF);
        return previous.Composite2(foreground, Enums.BlendMode.Over, x: 0, y: top);
    }

    /// <summary>
    /// Draws an unplayed count badge in the top right corner of the image.
    /// </summary>
    /// <param name="image">The image to draw on.</param>
    /// <param name="count">The number to show in the badge.</param>
    /// <returns>The image with the indicator composited over it.</returns>
    public static Image DrawUnplayedCount(Image image, int count)
    {
        var centreX = image.Width - UnplayedOffsetFromTopRightCorner;
        var centreY = UnplayedOffsetFromTopRightCorner;
        if (centreX - UnplayedRadius < 0 || centreY + UnplayedRadius > image.Height)
        {
            return image;
        }

        // #CC00A4DC circle. One pixel of slack on each side leaves room for the antialiased edge.
        var diameter = (UnplayedRadius * 2) + 2;
        using var badge = Circle(diameter, UnplayedRadius, 0x00, 0xA4, 0xDC, 0xCC);
        var originX = centreX - (diameter / 2);
        var originY = centreY - (diameter / 2);

        using var label = RenderCount(count);
        if (label is null)
        {
            return image.Composite2(badge, Enums.BlendMode.Over, x: originX, y: originY);
        }

        // Centre the glyphs on the badge rather than replicating Skia's hand-tuned per-digit offsets.
        using var withBadge = image.Composite2(badge, Enums.BlendMode.Over, x: originX, y: originY);
        return withBadge.Composite2(
            label,
            Enums.BlendMode.Over,
            x: centreX - (label.Width / 2),
            y: centreY - (label.Height / 2));
    }

    /// <summary>
    /// Builds a uniformly coloured RGBA image.
    /// </summary>
    private static Image SolidColor(int width, int height, double r, double g, double b, double a)
    {
        using var canvas = Image.Black(width, height);
        return canvas.NewFromImage(new[] { r, g, b, a })
            .Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>
    /// Builds an antialiased filled circle as an RGBA image.
    /// </summary>
    private static Image Circle(int size, double radius, double r, double g, double b, double a)
    {
        var centre = (size - 1) / 2.0;

        // Coverage of each pixel, approximated as the signed distance to the circle edge clamped to
        // one pixel. Cast() clips out of range values for us, which is what does the clamping.
        using var xy = Image.Xyz(size, size);
        using var dx = xy[0] - centre;
        using var dy = xy[1] - centre;
        using var squared = (dx * dx) + (dy * dy);
        using var distance = squared.Pow(0.5);
        using var coverage = ((radius - distance + 0.5) * 255).Cast(Enums.BandFormat.Uchar);
        using var alpha = (coverage * (a / 255.0)).Cast(Enums.BandFormat.Uchar);
        using var color = alpha.NewFromImage(new[] { r, g, b });

        return color.Bandjoin(alpha).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    /// <summary>
    /// Renders the count as a white RGBA image, or null when the libvips build has no text support.
    /// </summary>
    private static Image? RenderCount(int count)
    {
        var text = count.ToString(CultureInfo.InvariantCulture);

        // Skia shrinks the glyphs once the count reaches three digits so they still fit the badge.
        var pointSize = text.Length >= 3 ? 18 : 24;

        try
        {
            // Pango does the shaping and font fallback here, so unlike the Skia path there is no
            // hand-maintained typeface list to walk.
            using var mask = Image.Text(text, font: $"sans-serif Bold {pointSize}", dpi: 72);
            using var white = mask.NewFromImage(new double[] { 0xFF, 0xFF, 0xFF });
            return white.Bandjoin(mask).Copy(interpretation: Enums.Interpretation.Srgb);
        }
        catch (VipsException)
        {
            // Text rendering needs Pango and fontconfig; a stripped libvips build has neither.
            return null;
        }
    }
}
