using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoImporter.Core.Metadata;

namespace PhotoImporter.App.Services;

/// <summary>
/// Thumbnail from the JPEG preview the camera embedded in a RAW file: fast and independent of installed codecs.
/// </summary>
internal static class RawThumbnail
{
    /// <summary>Returns a frozen bitmap (already turned upright) or null when the file has no usable preview.</summary>
    public static BitmapSource? Get(string path, int size)
    {
        if (EmbeddedPreview.TryRead(path) is not { } preview) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = size; // decoded at thumbnail size: fast even for a full-size preview
            image.StreamSource = new MemoryStream(preview.Jpeg, writable: false);
            image.EndInit();

            BitmapSource result = Orient(image, preview.Orientation);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Applies the EXIF orientation (1..8) so the picture is upright.</summary>
    private static BitmapSource Orient(BitmapSource image, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return transform is null ? image : new TransformedBitmap(image, transform);
    }
}
