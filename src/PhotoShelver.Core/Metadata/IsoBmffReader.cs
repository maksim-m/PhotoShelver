using System.Buffers.Binary;
using System.Text;

namespace PhotoShelver.Core.Metadata;

/// <summary>Date/location information extracted from an MP4/MOV (ISO-BMFF / QuickTime) container.</summary>
/// <param name="CreationUtc">moov/mvhd creation time - UTC by the specification, but see <paramref name="MvhdIsLocalTime"/>.</param>
/// <param name="EmbeddedJpeg">
/// A JPEG thumbnail the camera embedded in the container together with its own EXIF (Canon: CNTH/CNDA, in moov/uuid
/// in MP4 files, in moov/udta in MOV files). Its EXIF holds the capture time on the camera's clock - the same clock its
/// photos use.
/// </param>
/// <param name="SonyCreationDate">Sony's XML (top-level meta/xml, NonRealTimeMeta/CreationDate): with a zone offset or 'Z'.</param>
/// <param name="MvhdIsLocalTime">
/// The camera writes its own clock (local time) into mvhd instead of UTC, as GoPro cameras do (verified with GPS time).
/// </param>
internal sealed record IsoBmffInfo(
    DateTime? CreationUtc,
    string? AppleCreationDate,
    string? Iso6709Location,
    byte[]? EmbeddedJpeg = null,
    string? SonyCreationDate = null,
    bool MvhdIsLocalTime = false);

/// <summary>
/// Minimal reader for the parts of MP4/MOV files we need: moov/mvhd creation time,
/// Apple metadata keys (com.apple.quicktime.creationdate / location.ISO6709) and udta/©xyz.
/// </summary>
internal static partial class IsoBmffReader
{
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int MaxDepth = 8;
    private const long MaxStringBox = 64 * 1024;
    private const long MaxEmbeddedJpeg = 2 * 1024 * 1024;

    /// <summary>Canon's metadata box in MP4 and CR3 files.</summary>
    private static readonly Guid CanonUuid = new("85c0b687-820f-11e0-8111-f4ce462b6a48");

    public static IsoBmffInfo Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        return Read(fs);
    }

    /// <summary>
    /// Box types an ISO-BMFF / QuickTime file can start with: 'ftyp' (MP4, MOV, 3GP, Insta360, GoPro, Canon CRM…),
    /// or the first box of an old QuickTime file without one. Decided by the content, so any extension works and other
    /// containers (AVI, MTS, MKV, MXF…) are never misread.
    /// </summary>
    private static readonly HashSet<string> FirstBoxTypes = ["ftyp", "moov", "mdat", "wide", "free", "skip", "pnot"];

    public static bool IsIsoBmff(Stream stream)
    {
        var h = ReadBytes(stream, 0, 8);
        return h is not null && FirstBoxTypes.Contains(Encoding.Latin1.GetString(h, 4, 4));
    }

    public static IsoBmffInfo Read(Stream stream)
    {
        var state = new State();
        if (!IsIsoBmff(stream)) return new IsoBmffInfo(null, null, null);
        var moovSeen = false;
        foreach (var box in Children(stream, 0, stream.Length))
        {
            if (box.Type == "moov" && !moovSeen)
            {
                ParseContainer(stream, box, state, 1);
                moovSeen = true;
            }
            else if (box.Type == "meta")
            {
                state.SonyCreationDate ??= ReadSonyXmlDate(stream, box); // Sony: next to moov, not inside it
            }
        }
        return new IsoBmffInfo(state.CreationUtc, state.AppleCreationDate, state.AppleLocation ?? state.UdtaLocation,
            state.EmbeddedJpeg, state.SonyCreationDate, state.GoPro);
    }

    private sealed class State
    {
        public DateTime? CreationUtc;
        public string? AppleCreationDate;
        public string? AppleLocation;
        public string? UdtaLocation;
        public byte[]? EmbeddedJpeg;
        public string? SonyCreationDate;
        public bool GoPro;
    }

    private readonly record struct Box(string Type, long PayloadStart, long End);

    private static void ParseContainer(Stream s, Box parent, State state, int depth)
    {
        if (depth > MaxDepth) return;
        foreach (var box in Children(s, parent.PayloadStart, parent.End))
        {
            switch (box.Type)
            {
                case "mvhd":
                    state.CreationUtc ??= ReadMvhd(s, box);
                    break;
                case "udta":
                    ParseContainer(s, box, state, depth + 1);
                    break;
                case "©xyz":
                    state.UdtaLocation ??= ReadUdtaString(s, box);
                    break;
                case "meta":
                    ParseMeta(s, box, state);
                    break;
                case "uuid":
                    state.EmbeddedJpeg ??= ReadCanonThumbnail(s, box);
                    break;
                case "CNTH": // Canon MOV: directly in moov/udta
                    state.EmbeddedJpeg ??= ReadCnda(s, box);
                    break;
                case "FIRM": // GoPro firmware, e.g. "HD6.01.02.01.00" (HERO6), "H22.01.01.10.70" (HERO11)
                    state.GoPro |= IsGoProFirmware(ReadBytes(s, box.PayloadStart, (int)Math.Min(32, box.End - box.PayloadStart)));
                    break;
                case "GPMF": // GoPro telemetry settings (HERO5 and later)
                    state.GoPro = true;
                    break;
            }
        }
    }

    private static bool IsGoProFirmware(byte[]? firm)
    {
        if (firm is null || firm.Length < 4) return false;
        var text = Encoding.ASCII.GetString(firm);
        return text.StartsWith("HD", StringComparison.Ordinal) && char.IsAsciiDigit(text[2])
               || text[0] == 'H' && char.IsAsciiDigit(text[1]) && char.IsAsciiDigit(text[2]) && text[3] == '.';
    }

    private const long MaxXml = 256 * 1024;

    /// <summary>Sony XAVC: top-level meta (full box) / xml (full box) with NonRealTimeMeta; returns CreationDate/@value.</summary>
    private static string? ReadSonyXmlDate(Stream s, Box meta)
    {
        foreach (var box in Children(s, meta.PayloadStart + 4, meta.End))
        {
            if (box.Type != "xml ") continue;
            var length = box.End - box.PayloadStart - 4;
            if (length is <= 0 or > MaxXml) return null;
            var xml = ReadBytes(s, box.PayloadStart + 4, (int)length);
            if (xml is null) return null;
            var match = SonyCreationDate().Match(Encoding.UTF8.GetString(xml));
            return match.Success ? match.Groups[1].Value : null;
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("<CreationDate\\s+value\\s*=\\s*\"([^\"]{10,40})\"")]
    private static partial System.Text.RegularExpressions.Regex SonyCreationDate();

    /// <summary>moov/uuid(Canon)/CNTH/CNDA: the JPEG thumbnail with the camera's EXIF.</summary>
    private static byte[]? ReadCanonThumbnail(Stream s, Box uuid)
    {
        var id = ReadBytes(s, uuid.PayloadStart, 16);
        if (id is null || new Guid(id, bigEndian: true) != CanonUuid) return null;
        foreach (var cnth in Children(s, uuid.PayloadStart + 16, uuid.End))
        {
            if (cnth.Type == "CNTH" && ReadCnda(s, cnth) is { } jpeg) return jpeg;
        }
        return null;
    }

    /// <summary>CNTH/CNDA: Canon's JPEG thumbnail with EXIF.</summary>
    private static byte[]? ReadCnda(Stream s, Box cnth)
    {
        foreach (var cnda in Children(s, cnth.PayloadStart, cnth.End))
        {
            var length = cnda.End - cnda.PayloadStart;
            if (cnda.Type == "CNDA" && length is > 4 and <= MaxEmbeddedJpeg)
                return ReadBytes(s, cnda.PayloadStart, (int)length);
        }
        return null;
    }

    private static void ParseMeta(Stream s, Box meta, State state)
    {
        // ISO 'meta' is a full box (4 bytes version/flags); QuickTime 'meta' is not.
        var start = meta.PayloadStart;
        var probe = ReadBytes(s, start, 8);
        if (probe is null) return;
        if (Encoding.ASCII.GetString(probe, 4, 4) != "hdlr") start += 4;

        List<string>? keys = null;
        Box? ilst = null;
        foreach (var box in Children(s, start, meta.End))
        {
            if (box.Type == "keys") keys = ReadKeys(s, box);
            else if (box.Type == "ilst") ilst = box;
        }
        if (keys is null || ilst is null) return;

        foreach (var item in Children(s, ilst.Value.PayloadStart, ilst.Value.End))
        {
            var indexBytes = Encoding.Latin1.GetBytes(item.Type);
            var index = BinaryPrimitives.ReadInt32BigEndian(indexBytes);
            if (index < 1 || index > keys.Count) continue;
            var key = keys[index - 1];
            if (key != "com.apple.quicktime.creationdate" && key != "com.apple.quicktime.location.ISO6709") continue;

            foreach (var data in Children(s, item.PayloadStart, item.End))
            {
                if (data.Type != "data") continue;
                var len = data.End - data.PayloadStart - 8; // type indicator + locale
                if (len <= 0 || len > MaxStringBox) continue;
                var bytes = ReadBytes(s, data.PayloadStart + 8, (int)len);
                if (bytes is null) continue;
                var value = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
                if (key == "com.apple.quicktime.creationdate") state.AppleCreationDate ??= value;
                else state.AppleLocation ??= value;
            }
        }
    }

    private static List<string>? ReadKeys(Stream s, Box box)
    {
        var header = ReadBytes(s, box.PayloadStart, 8);
        if (header is null) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var keys = new List<string>();
        var pos = box.PayloadStart + 8;
        for (var i = 0; i < count && pos + 8 <= box.End; i++)
        {
            var eh = ReadBytes(s, pos, 8);
            if (eh is null) return keys;
            var size = BinaryPrimitives.ReadUInt32BigEndian(eh);
            if (size < 8 || pos + size > box.End || size > MaxStringBox) return keys;
            var name = ReadBytes(s, pos + 8, (int)size - 8);
            keys.Add(name is null ? "" : Encoding.UTF8.GetString(name));
            pos += size;
        }
        return keys;
    }

    private static DateTime? ReadMvhd(Stream s, Box box)
    {
        var b = ReadBytes(s, box.PayloadStart, 12);
        if (b is null) return null;
        ulong seconds = b[0] == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(4))
            : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        // 0 = unset; anything past year 9999 is garbage (and would throw, losing the file's other dates too).
        if (seconds == 0 || seconds > (ulong)(DateTime.MaxValue - Epoch1904).TotalSeconds) return null;
        var result = Epoch1904.AddSeconds(seconds);
        // Sanity: reject obviously wrong values (unset clocks etc).
        if (result.Year < 1980 || result > DateTime.UtcNow.AddYears(1)) return null;
        return result;
    }

    private static string? ReadUdtaString(Stream s, Box box)
    {
        var header = ReadBytes(s, box.PayloadStart, 4);
        if (header is null) return null;
        var len = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (len == 0 || box.PayloadStart + 4 + len > box.End) return null;
        var bytes = ReadBytes(s, box.PayloadStart + 4, len);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
    }

    private static IEnumerable<Box> Children(Stream s, long start, long end)
    {
        var pos = start;
        while (pos + 8 <= end)
        {
            var h = ReadBytes(s, pos, 8);
            if (h is null) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.Latin1.GetString(h, 4, 4);
            var headerLen = 8L;
            if (size == 1)
            {
                var ext = ReadBytes(s, pos + 8, 8);
                if (ext is null) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerLen || pos + size > end) yield break;
            yield return new Box(type, pos + headerLen, pos + size);
            pos += size;
        }
    }

    private static byte[]? ReadBytes(Stream s, long offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > s.Length) return null;
        s.Position = offset;
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }
}
