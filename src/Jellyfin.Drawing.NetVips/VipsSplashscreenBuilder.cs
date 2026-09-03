using System;
using System.Collections.Generic;
using NetVips;

namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// Builds the splashscreen with libvips, mirroring <c>SplashscreenBuilder</c>.
/// </summary>
internal static class VipsSplashscreenBuilder
{
    private const int FinalWidth = 1920;
    private const int FinalHeight = 1080;

    // The generated collage is larger than the final resolution so the perspective transform has
    // pixels to pull from at the near edge.
    private const int WallWidth = FinalWidth * 3;
    private const int WallHeight = FinalHeight * 2;
    private const int Rows = 6;
    private const int Spacing = 20;

    /// <summary>
    /// The perspective transform Skia applies to the wall, as the rows of a 3x3 homography.
    ///
    /// Lifted verbatim from <c>SplashscreenBuilder.Transform3D</c> so both encoders produce the same
    /// image. Skia maps source to destination; libvips <c>mapim</c> wants the opposite, so this gets
    /// inverted before use.
    /// </summary>
    private static readonly double[] _forwardTransform =
    [
        0.324108899, -0.244337708, 42.0407715,
        0.0377609022, 0.563934922, -198.104706,
        -9.08959337E-05, 6.85242048E-05, 0.988209724
    ];

    /// <summary>
    /// Generates the splashscreen.
    /// </summary>
    /// <param name="posters">The poster paths.</param>
    /// <param name="backdrops">The landscape paths.</param>
    /// <param name="outputPath">Where to write the splashscreen.</param>
    public static void Generate(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops, string outputPath)
    {
        using var wall = GenerateCollage(posters, backdrops);
        using var transformed = Transform3D(wall);

        VipsCollageBuilder.Write(transformed, outputPath);
    }

    /// <summary>
    /// Lays posters and backdrops out in staggered rows.
    /// </summary>
    private static Image GenerateCollage(IReadOnlyList<string> posters, IReadOnlyList<string> backdrops)
    {
        var posterCursor = new VipsImageCursor(posters);
        var backdropCursor = new VipsImageCursor(backdrops);
        var posterHeight = WallHeight / Rows;

        var rows = new Image[Rows];
        try
        {
            for (var i = 0; i < Rows; i++)
            {
                var imageCounter = Random.Shared.Next(0, 5);
                var currentWidthPos = i * 75;

                // Each row is built on its own and only then dropped onto the wall. Inserting all
                // ~150 images straight into the wall would leave every output pixel walking a
                // ~150 deep pipeline; this keeps it to the row depth plus one.
                var row = Image.Black(WallWidth, posterHeight, bands: 3)
                    .Copy(interpretation: Enums.Interpretation.Srgb);

                while (currentWidthPos < WallWidth)
                {
                    var candidate = imageCounter is 0 or 2 or 3
                        ? posterCursor.Next()
                        : backdropCursor.Next();

                    if (candidate is null)
                    {
                        row.Dispose();
                        throw new ArgumentException("Not enough valid pictures provided to create a splashscreen!");
                    }

                    var imageWidth = Math.Abs(posterHeight * candidate.Value.Width / candidate.Value.Height);

                    using (var image = VipsCollageBuilder.Resize(candidate.Value.Path, imageWidth, posterHeight))
                    {
                        var previous = row;
                        row = previous.Insert(image, currentWidthPos, 0);
                        previous.Dispose();
                    }

                    currentWidthPos += imageWidth + Spacing;

                    imageCounter = imageCounter >= 4 ? 0 : imageCounter + 1;
                }

                // Materialise the row. Without this the whole wall stays lazy and every one of the
                // 12 million output pixels re-walks the row pipelines during the warp below.
                rows[i] = row.CopyMemory();
                row.Dispose();
            }

            var wall = Image.Black(WallWidth, WallHeight, bands: 3)
                .Copy(interpretation: Enums.Interpretation.Srgb);

            for (var i = 0; i < Rows; i++)
            {
                var previous = wall;

                // Rows run off the bottom of the wall by design; Insert clips them.
                wall = previous.Insert(rows[i], 0, i * ((WallHeight / Rows) + Spacing));
                previous.Dispose();
            }

            return wall;
        }
        finally
        {
            foreach (var row in rows)
            {
                row?.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies the perspective transform, cropping to the final resolution.
    /// </summary>
    private static Image Transform3D(Image input)
    {
        var inverse = Invert(_forwardTransform);

        // mapim is a reverse map: for each output pixel it asks where in the source to sample. Build
        // that as a two band coordinate image by pushing the output grid through the inverse
        // homography, then dividing through by the homogeneous component.
        using var grid = Image.Xyz(FinalWidth, FinalHeight);
        using var u = grid[0];
        using var v = grid[1];

        using var w = (u * inverse[6]) + (v * inverse[7]) + inverse[8];
        using var sourceX = ((u * inverse[0]) + (v * inverse[1]) + inverse[2]) / w;
        using var sourceY = ((u * inverse[3]) + (v * inverse[4]) + inverse[5]) / w;
        using var index = sourceX.Bandjoin(sourceY);

        using var interpolate = Interpolate.NewFromName("bilinear");

        // Anything the wall does not reach stays black, matching the cleared Skia canvas.
        return input.Mapim(index, interpolate, background: [0, 0, 0], extend: Enums.Extend.Black);
    }

    /// <summary>
    /// Inverts a row major 3x3 matrix.
    /// </summary>
    private static double[] Invert(double[] m)
    {
        var c00 = (m[4] * m[8]) - (m[5] * m[7]);
        var c01 = (m[5] * m[6]) - (m[3] * m[8]);
        var c02 = (m[3] * m[7]) - (m[4] * m[6]);

        var determinant = (m[0] * c00) + (m[1] * c01) + (m[2] * c02);
        if (determinant == 0)
        {
            throw new InvalidOperationException("The splashscreen transform is not invertible.");
        }

        var inverseDeterminant = 1.0 / determinant;

        return
        [
            c00 * inverseDeterminant,
            ((m[2] * m[7]) - (m[1] * m[8])) * inverseDeterminant,
            ((m[1] * m[5]) - (m[2] * m[4])) * inverseDeterminant,

            c01 * inverseDeterminant,
            ((m[0] * m[8]) - (m[2] * m[6])) * inverseDeterminant,
            ((m[2] * m[3]) - (m[0] * m[5])) * inverseDeterminant,

            c02 * inverseDeterminant,
            ((m[1] * m[6]) - (m[0] * m[7])) * inverseDeterminant,
            ((m[0] * m[4]) - (m[1] * m[3])) * inverseDeterminant
        ];
    }
}
