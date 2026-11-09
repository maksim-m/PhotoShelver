using System.Globalization;
using System.Text.RegularExpressions;

namespace PhotoShelver.Core.Metadata;

/// <summary>Parses ISO 6709 location strings like "+48.8577+002.2950+035.000/".</summary>
internal static partial class Iso6709
{
    [GeneratedRegex(@"^\s*(?<lat>[+-]\d+(?:\.\d+)?)(?<lon>[+-]\d+(?:\.\d+)?)")]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = Pattern().Match(value);
        if (!m.Success) return false;
        if (!TryParseCoordinate(m.Groups["lat"].Value, 2, out latitude) ||
            !TryParseCoordinate(m.Groups["lon"].Value, 3, out longitude))
            return false;
        return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
    }

    /// <summary>Supports ±DD.DDD, ±DDMM.MMM and ±DDMMSS.SSS forms (degree digits: 2 for lat, 3 for lon).</summary>
    private static bool TryParseCoordinate(string s, int degreeDigits, out double result)
    {
        result = 0;
        var sign = s[0] == '-' ? -1 : 1;
        var body = s[1..];
        var dot = body.IndexOf('.');
        var intLen = dot < 0 ? body.Length : dot;
        var frac = dot < 0 ? "" : body[dot..];

        double deg;
        if (intLen <= degreeDigits)
        {
            if (!double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out deg)) return false;
        }
        else if (intLen == degreeDigits + 2)
        {
            var d = int.Parse(body[..degreeDigits], CultureInfo.InvariantCulture);
            if (!double.TryParse(body[degreeDigits..intLen] + frac, NumberStyles.Float, CultureInfo.InvariantCulture, out var min)) return false;
            deg = d + min / 60;
        }
        else if (intLen == degreeDigits + 4)
        {
            var d = int.Parse(body[..degreeDigits], CultureInfo.InvariantCulture);
            var min = int.Parse(body.Substring(degreeDigits, 2), CultureInfo.InvariantCulture);
            if (!double.TryParse(body[(degreeDigits + 2)..intLen] + frac, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return false;
            deg = d + min / 60.0 + sec / 3600;
        }
        else
        {
            return false;
        }

        result = sign * deg;
        return true;
    }
}
