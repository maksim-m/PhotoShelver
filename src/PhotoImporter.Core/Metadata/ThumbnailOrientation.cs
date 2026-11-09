namespace PhotoImporter.Core.Metadata;

/// <param name="Width">Width as stored in the file (before applying the orientation).</param>
/// <param name="Height">Height as stored in the file.</param>
/// <param name="Orientation">EXIF orientation 1..8.</param>
public sealed record TiffImageInfo(int Width, int Height, int Orientation);

/// <summary>
/// Windows' thumbnail for TIFF ignores the EXIF orientation tag (Explorer shows such scans lying on the side).
/// Decides which orientation still has to be applied to a thumbnail Windows produced.
/// </summary>
public static class ThumbnailOrientation
{
    /// <summary>
    /// For quarter turns (5..8) the thumbnail's shape tells whether Windows already turned it: if it still has the
    /// stored shape (e.g. landscape for a landscape-stored portrait shot) the orientation is applied, otherwise not,
    /// so a future Windows that honours the tag never causes a double turn. 180° and mirrors (2..4) cannot be
    /// detected that way and are applied (Windows does not apply them for TIFF).
    /// </summary>
    public static int ToApply(int orientation, int imageWidth, int imageHeight, int thumbWidth, int thumbHeight)
    {
        if (orientation is < 2 or > 8) return 1;
        if (orientation < 5) return orientation;
        if (imageWidth <= 0 || imageHeight <= 0 || imageWidth == imageHeight || thumbWidth == thumbHeight)
            return orientation; // shape tells nothing
        var storedLandscape = imageWidth > imageHeight;
        var thumbLandscape = thumbWidth > thumbHeight;
        return storedLandscape == thumbLandscape ? orientation : 1;
    }
}
