using System.Globalization;
using System.Text.RegularExpressions;
using GeoTimeZone;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace PhotoShelver.Core.Metadata;

/// <summary>Determines the capture date of a photo or video (see SPEC §3).</summary>
public sealed partial class CaptureDateReader
{
    private readonly TimeZoneInfo _localZone;

    public CaptureDateReader(TimeZoneInfo? localZone = null)
    {
        _localZone = localZone ?? TimeZoneInfo.Local;
    }

    public CaptureDate Read(string path, MediaKind kind, DateTime lastWriteTimeUtc)
    {
        CaptureDate? fromMetadata = null;
        try
        {
            fromMetadata = kind == MediaKind.Photo
                ? ReadPhoto(path)
                : MediaTypes.IsIsoBmffVideo(path) ? ReadVideo(path) : null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Malformed, truncated or unsupported metadata (parsers may throw anything): fall back to file time.
        }

        return fromMetadata ?? new CaptureDate(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc), _localZone),
            DateSource.FileModified);
    }

    private static CaptureDate? ReadPhoto(string path) => ExifDate(ImageMetadataReader.ReadMetadata(path));

    /// <summary>Capture date from EXIF: the camera's local clock, used as is (SPEC §3).</summary>
    private static CaptureDate? ExifDate(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var dir in directories.OfType<ExifSubIfdDirectory>())
        {
            if (TryGetExifDate(dir, ExifDirectoryBase.TagDateTimeOriginal, out var d) ||
                TryGetExifDate(dir, ExifDirectoryBase.TagDateTimeDigitized, out d))
                return new CaptureDate(d, DateSource.Metadata);
        }
        foreach (var dir in directories.OfType<ExifIfd0Directory>())
        {
            if (TryGetExifDate(dir, ExifDirectoryBase.TagDateTime, out var d))
                return new CaptureDate(d, DateSource.Metadata);
        }
        return null;
    }

    private static bool TryGetExifDate(MetadataExtractor.Directory dir, int tag, out DateTime value)
    {
        if (dir.TryGetDateTime(tag, out value) && value.Year >= 1900)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    private CaptureDate? ReadVideo(string path)
    {
        var info = IsoBmffReader.Read(path);
        return ResolveVideoDate(info);
    }

    internal CaptureDate? ResolveVideoDate(IsoBmffInfo info)
    {
        // 1. Apple creation date carries local time with offset.
        if (TryParseAppleDate(info.AppleCreationDate, out var appleLocal, out var isUtc))
        {
            if (!isUtc) return new CaptureDate(appleLocal, DateSource.Metadata);
            info = info with { CreationUtc = info.CreationUtc ?? appleLocal };
        }

        // 2. EXIF the camera embedded in the video (Canon): the camera's own clock, exactly like its photos,
        //    so photos and videos of the same moment always land in the same folder.
        if (info.EmbeddedJpeg is { } jpeg)
        {
            try
            {
                if (ExifDate(ImageMetadataReader.ReadMetadata(new MemoryStream(jpeg, writable: false))) is { } fromExif)
                    return fromExif;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // A damaged thumbnail: continue with the container's own times.
            }
        }

        if (info.CreationUtc is not { } utc) return null;
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        // 3. GPS -> time zone of the shooting place.
        if (Iso6709.TryParse(info.Iso6709Location, out var lat, out var lon) && TryZoneFromGps(lat, lon, out var zone))
            return new CaptureDate(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, zone), DateTimeKind.Unspecified), DateSource.Metadata);

        // 4. Computer's time zone (rules at the capture date).
        return new CaptureDate(DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, _localZone), DateTimeKind.Unspecified), DateSource.MetadataGuessedZone);
    }

    [GeneratedRegex(@"^(?<dt>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?(?<tz>Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex AppleDatePattern();

    /// <summary>Parses e.g. "2026-07-15T18:23:45+0200". Returns the wall-clock part; isUtc when the zone is 'Z'.</summary>
    internal static bool TryParseAppleDate(string? value, out DateTime local, out bool isUtc)
    {
        local = default;
        isUtc = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = AppleDatePattern().Match(value.Trim());
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups["dt"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out local))
            return false;
        isUtc = m.Groups["tz"].Value == "Z";
        return true;
    }

    private static bool TryZoneFromGps(double lat, double lon, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (lat == 0 && lon == 0) return false; // "null island": usually missing GPS
        try
        {
            var iana = TimeZoneLookup.GetTimeZone(lat, lon).Result;
            zone = TimeZoneInfo.FindSystemTimeZoneById(iana);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return false;
        }
    }
}
