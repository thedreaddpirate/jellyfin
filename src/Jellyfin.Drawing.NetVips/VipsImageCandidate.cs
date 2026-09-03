namespace Jellyfin.Drawing.NetVips;

/// <summary>
/// A readable image and its stored dimensions, as returned by <see cref="VipsImageCursor"/>.
/// </summary>
/// <param name="Path">The path to the image.</param>
/// <param name="Width">The stored width.</param>
/// <param name="Height">The stored height.</param>
internal readonly record struct VipsImageCandidate(string Path, int Width, int Height);
