namespace PhotoShelver.Core;

public enum MediaKind
{
    Photo,
    Video,
}

public static class MediaTypes
{
    private static readonly Dictionary<string, MediaKind> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Photos
        [".jpg"] = MediaKind.Photo,
        [".jpeg"] = MediaKind.Photo,
        [".heic"] = MediaKind.Photo,
        [".heif"] = MediaKind.Photo,
        [".hif"] = MediaKind.Photo,   // Fujifilm HEIF
        [".avif"] = MediaKind.Photo,
        [".jxl"] = MediaKind.Photo,
        [".webp"] = MediaKind.Photo,
        [".png"] = MediaKind.Photo,
        [".tif"] = MediaKind.Photo,
        [".tiff"] = MediaKind.Photo,
        [".insp"] = MediaKind.Photo,  // Insta360 photo (JPEG)

        // RAW
        [".dng"] = MediaKind.Photo,
        [".cr2"] = MediaKind.Photo,
        [".cr3"] = MediaKind.Photo,
        [".crw"] = MediaKind.Photo,   // old Canon
        [".nef"] = MediaKind.Photo,
        [".nrw"] = MediaKind.Photo,   // Nikon compacts
        [".arw"] = MediaKind.Photo,
        [".srf"] = MediaKind.Photo,   // old Sony
        [".sr2"] = MediaKind.Photo,
        [".raf"] = MediaKind.Photo,
        [".orf"] = MediaKind.Photo,
        [".rw2"] = MediaKind.Photo,
        [".rwl"] = MediaKind.Photo,   // Leica (Panasonic-made)
        [".pef"] = MediaKind.Photo,   // Pentax
        [".srw"] = MediaKind.Photo,   // Samsung
        [".gpr"] = MediaKind.Photo,   // GoPro RAW
        [".3fr"] = MediaKind.Photo,   // Hasselblad
        [".fff"] = MediaKind.Photo,
        [".iiq"] = MediaKind.Photo,   // Phase One
        [".erf"] = MediaKind.Photo,   // Epson
        [".mef"] = MediaKind.Photo,   // Mamiya
        [".mos"] = MediaKind.Photo,   // Leaf
        [".dcr"] = MediaKind.Photo,   // Kodak
        [".kdc"] = MediaKind.Photo,
        [".mrw"] = MediaKind.Photo,   // Minolta
        [".x3f"] = MediaKind.Photo,   // Sigma

        // Video
        [".mp4"] = MediaKind.Video,
        [".mov"] = MediaKind.Video,
        [".m4v"] = MediaKind.Video,
        [".3gp"] = MediaKind.Video,
        [".avi"] = MediaKind.Video,
        [".mts"] = MediaKind.Video,   // AVCHD
        [".m2ts"] = MediaKind.Video,
        [".mpg"] = MediaKind.Video,   // older camcorders (MPEG-2)
        [".mpeg"] = MediaKind.Video,
        [".mod"] = MediaKind.Video,   // JVC / Panasonic / Canon SD camcorders
        [".tod"] = MediaKind.Video,
        [".mkv"] = MediaKind.Video,
        [".webm"] = MediaKind.Video,
        [".insv"] = MediaKind.Video,  // Insta360 (MP4)
        [".360"] = MediaKind.Video,   // GoPro MAX (MP4)
        [".crm"] = MediaKind.Video,   // Canon Cinema RAW Light
        [".mxf"] = MediaKind.Video,   // professional camcorders
        [".braw"] = MediaKind.Video,  // Blackmagic RAW
        [".r3d"] = MediaKind.Video,   // RED
        [".nev"] = MediaKind.Video,   // Nikon N-RAW

        // Deliberately not media: camera proxies and thumbnails next to the real files
        // (GoPro .LRV/.THM, DJI .LRF) - importing them would only duplicate the videos in low quality.
    };

    public static bool TryGetKind(string path, out MediaKind kind) =>
        Extensions.TryGetValue(Path.GetExtension(path), out kind);

    /// <summary>All extensions taken as photos or videos (lower case, with the dot).</summary>
    public static IReadOnlyCollection<string> KnownExtensions => Extensions.Keys;
}
