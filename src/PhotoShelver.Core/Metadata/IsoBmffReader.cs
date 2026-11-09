using System.Buffers.Binary;
using System.Text;

namespace PhotoShelver.Core.Metadata;

/// <summary>Date/location information extracted from an MP4/MOV (ISO-BMFF / QuickTime) container.</summary>
/// <param name="EmbeddedJpeg">
/// A JPEG thumbnail the camera embedded in the container together with its own EXIF (Canon: moov/uuid/CNTH/CNDA).
/// Its EXIF holds the capture time on the camera's clock - the same clock its photos use.
/// </param>
internal sealed record IsoBmffInfo(
    DateTime? CreationUtc,
    string? AppleCreationDate,
    string? Iso6709Location,
    byte[]? EmbeddedJpeg = null);

/// <summary>
/// Minimal reader for the parts of MP4/MOV files we need: moov/mvhd creation time,
/// Apple metadata keys (com.apple.quicktime.creationdate / location.ISO6709) and udta/©xyz.
/// </summary>
internal static class IsoBmffReader
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

    public static IsoBmffInfo Read(Stream stream)
    {
        var state = new State();
        foreach (var box in Children(stream, 0, stream.Length))
        {
            if (box.Type == "moov")
            {
                ParseContainer(stream, box, state, 1);
                break;
            }
        }
        return new IsoBmffInfo(state.CreationUtc, state.AppleCreationDate, state.AppleLocation ?? state.UdtaLocation, state.EmbeddedJpeg);
    }

    private sealed class State
    {
        public DateTime? CreationUtc;
        public string? AppleCreationDate;
        public string? AppleLocation;
        public string? UdtaLocation;
        public byte[]? EmbeddedJpeg;
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
            }
        }
    }

    /// <summary>moov/uuid(Canon)/CNTH/CNDA: the JPEG thumbnail with the camera's EXIF.</summary>
    private static byte[]? ReadCanonThumbnail(Stream s, Box uuid)
    {
        var id = ReadBytes(s, uuid.PayloadStart, 16);
        if (id is null || new Guid(id, bigEndian: true) != CanonUuid) return null;
        foreach (var cnth in Children(s, uuid.PayloadStart + 16, uuid.End))
        {
            if (cnth.Type != "CNTH") continue;
            foreach (var cnda in Children(s, cnth.PayloadStart, cnth.End))
            {
                var length = cnda.End - cnda.PayloadStart;
                if (cnda.Type == "CNDA" && length is > 4 and <= MaxEmbeddedJpeg)
                    return ReadBytes(s, cnda.PayloadStart, (int)length);
            }
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
        if (seconds == 0) return null;
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
