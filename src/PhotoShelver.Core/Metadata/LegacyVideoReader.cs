using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PhotoShelver.Core.Metadata;

/// <summary>
/// Capture dates of the older camera video containers. Both hold the camera's own clock (local time, like EXIF):
/// AVI (compact cameras, Motion JPEG) - the RIFF 'IDIT' chunk; AVCHD (.MTS/.M2TS camcorders) - the 'MDPM' block
/// in the H.264 stream (Sony, Canon, Panasonic).
/// </summary>
internal static class LegacyVideoReader
{
    private const int MaxChunks = 256;
    private const int MaxIditLength = 64;

    /// <summary>How far into an AVCHD file to look for MDPM: it comes with the first frame, near the start.</summary>
    internal const int AvchdSearchBytes = 1024 * 1024;

    // ---------------------------------------------------------------- AVI

    public static DateTime? ReadAviDate(Stream s)
    {
        var header = Read(s, 0, 12);
        if (header is null || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "AVI ")
            return null;
        return FindIdit(s, 12, Math.Min(s.Length, 12 + (long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) - 4), 0);
    }

    private static DateTime? FindIdit(Stream s, long start, long end, int depth)
    {
        var pos = start;
        for (var n = 0; n < MaxChunks && pos + 8 <= end; n++)
        {
            var h = Read(s, pos, 12);
            if (h is null) return null;
            var id = Encoding.ASCII.GetString(h, 0, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            if (pos + 8 + size > end) return null;
            if (id == "IDIT" && size is > 0 and <= MaxIditLength && Read(s, pos + 8, (int)size) is { } text)
                return ParseIdit(Encoding.ASCII.GetString(text));
            if (id == "LIST" && depth == 0 && size >= 4 && Encoding.ASCII.GetString(h, 8, 4) != "movi"
                && FindIdit(s, pos + 12, pos + 8 + size, depth + 1) is { } inList)
                return inList;
            pos += 8 + size + (size & 1);
        }
        return null;
    }

    private static readonly string[] IditFormats =
    [
        "ddd MMM d HH:mm:ss yyyy",  // C asctime(), e.g. Canon: "Thu Jul 04 17:06:39 2002"
        "ddd MMM dd HH:mm:ss yyyy",
        "yyyy/MM/dd HH:mm:ss",      // Pentax
        "yyyy:MM:dd HH:mm:ss",      // EXIF style
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
    ];

    internal static DateTime? ParseIdit(string text)
    {
        var clean = string.Join(' ', text.Replace('\0', ' ').Split(' ', '\r', '\n', '\t').Where(p => p.Length > 0));
        return DateTime.TryParseExact(clean, IditFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && Sane(d)
            ? d
            : null;
    }

    // ---------------------------------------------------------------- AVCHD

    /// <summary>The MDPM user-data UUID in the H.264 SEI of AVCHD camcorders, followed by "MDPM".</summary>
    private static readonly byte[] MdpmMarker =
        [.. Convert.FromHexString("17EE8C60F84D11D98CD60800200C9A66"), .. "MDPM"u8];

    public static DateTime? ReadAvchdDate(Stream s)
    {
        var head = Read(s, 0, (int)Math.Min(s.Length, 8));
        if (head is null || head.Length < 8) return null;
        // MPEG transport stream: 0x47 sync byte at 0 (.TS, 188-byte packets) or at 4 (.MTS/.M2TS, 192-byte packets).
        if (head[0] != 0x47 && head[4] != 0x47) return null;

        var data = Read(s, 0, (int)Math.Min(s.Length, AvchdSearchBytes));
        if (data is null) return null;
        for (var at = data.AsSpan().IndexOf(MdpmMarker); at >= 0;)
        {
            if (ParseMdpm(data.AsSpan(at + MdpmMarker.Length)) is { } date) return date;
            var next = data.AsSpan(at + 1).IndexOf(MdpmMarker);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return null;
    }

    /// <summary>
    /// MDPM: a count, then 5-byte entries (tag + 4 bytes). 0x18 = time zone, year (2 BCD bytes), month; 0x19 = day,
    /// hour, minute, second (BCD). The bytes are inside an H.264 NAL unit, so "00 00 03" emulation bytes are removed first.
    /// </summary>
    internal static DateTime? ParseMdpm(ReadOnlySpan<byte> raw)
    {
        var bytes = new List<byte>(256);
        var zeros = 0;
        foreach (var b in raw[..Math.Min(raw.Length, 512)])
        {
            if (zeros >= 2 && b == 0x03) { zeros = 0; continue; }
            zeros = b == 0 ? zeros + 1 : 0;
            bytes.Add(b);
        }
        if (bytes.Count < 1) return null;
        int count = bytes[0];
        byte[]? ymd = null, dhms = null;
        for (var i = 0; i < count && 1 + i * 5 + 5 <= bytes.Count; i++)
        {
            var o = 1 + i * 5;
            if (bytes[o] == 0x18) ymd = [bytes[o + 1], bytes[o + 2], bytes[o + 3], bytes[o + 4]];
            else if (bytes[o] == 0x19) dhms = [bytes[o + 1], bytes[o + 2], bytes[o + 3], bytes[o + 4]];
        }
        if (ymd is null || dhms is null) return null;
        if (Bcd(ymd[1]) is not { } yh || Bcd(ymd[2]) is not { } yl || Bcd(ymd[3]) is not { } month
            || Bcd(dhms[0]) is not { } day || Bcd(dhms[1]) is not { } hour || Bcd(dhms[2]) is not { } minute || Bcd(dhms[3]) is not { } second)
            return null;
        var year = yh * 100 + yl;
        if (year < 1990 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return null;
        var date = new DateTime(year, month, day, hour, minute, second);
        return Sane(date) ? date : null;
    }

    private static int? Bcd(byte b) => (b >> 4) <= 9 && (b & 0xF) <= 9 ? (b >> 4) * 10 + (b & 0xF) : null;

    /// <summary>Unset camera clocks give 1970, 1980 or similar; a date in the future is not a capture date either.</summary>
    private static bool Sane(DateTime d) => d.Year >= 1990 && d <= DateTime.Now.AddYears(1);

    private static byte[]? Read(Stream s, long offset, int count)
    {
        if (count < 0 || offset < 0 || offset > s.Length - count) return null;
        s.Position = offset;
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }
}
