using System.Collections.Generic;
using System.IO;
using NetVips;

namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// Walks a list of image paths, skipping any that are missing or that libvips cannot decode.
///
/// The equivalent of <c>SkiaHelper.GetNextValidImage</c>, except that it hands back the path and the
/// header dimensions rather than a decoded bitmap. Callers resize straight from the file, which keeps
/// shrink-on-load available - it matters for the splashscreen, which touches close to a hundred
/// full size posters.
/// </summary>
internal sealed class VipsImageCursor
{
    private readonly IReadOnlyList<string> _paths;
    private int _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="VipsImageCursor"/> class.
    /// </summary>
    /// <param name="paths">The candidate image paths.</param>
    public VipsImageCursor(IReadOnlyList<string> paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Gets the next usable image, cycling back to the start of the list, or null when none of the
    /// remaining paths can be read.
    /// </summary>
    /// <returns>The candidate, or null.</returns>
    public VipsImageCandidate? Next()
    {
        for (var tried = 0; tried < _paths.Count; tried++)
        {
            if (_index >= _paths.Count)
            {
                _index = 0;
            }

            var path = _paths[_index++];
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                // Header read only. A truncated or unsupported file throws here rather than half way
                // through building the collage.
                using var source = Source.NewFromFile(path);
                using var header = Image.NewFromSource(source, access: Enums.Access.Sequential);

                if (header.Width > 0 && header.Height > 0)
                {
                    return new VipsImageCandidate(path, header.Width, header.Height);
                }
            }
            catch (VipsException)
            {
                // Not decodable, try the next path.
            }
        }

        return null;
    }
}
