namespace PhotoImporter.Core;

public enum MediaKind
{
    Photo,
    Video,
}

public static class MediaTypes
{
    private static readonly Dictionary<string, MediaKind> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Photo,
        [".jpeg"] = MediaKind.Photo,
        [".heic"] = MediaKind.Photo,
        [".heif"] = MediaKind.Photo,
        [".png"] = MediaKind.Photo,
        [".tif"] = MediaKind.Photo,
        [".tiff"] = MediaKind.Photo,
        [".dng"] = MediaKind.Photo,
        [".cr2"] = MediaKind.Photo,
        [".cr3"] = MediaKind.Photo,
        [".nef"] = MediaKind.Photo,
        [".arw"] = MediaKind.Photo,
        [".raf"] = MediaKind.Photo,
        [".orf"] = MediaKind.Photo,
        [".rw2"] = MediaKind.Photo,

        [".mp4"] = MediaKind.Video,
        [".mov"] = MediaKind.Video,
        [".m4v"] = MediaKind.Video,
        [".avi"] = MediaKind.Video,
        [".mts"] = MediaKind.Video,
        [".m2ts"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
    };

    /// <summary>Video containers based on ISO-BMFF / QuickTime that we can parse for dates.</summary>
    private static readonly HashSet<string> IsoBmffVideo = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".3gp",
    };

    public static bool TryGetKind(string path, out MediaKind kind) =>
        Extensions.TryGetValue(Path.GetExtension(path), out kind);

    public static bool IsIsoBmffVideo(string path) => IsoBmffVideo.Contains(Path.GetExtension(path));
}
