using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StravaReports.Core.Analysis;

public readonly record struct LatLng(double Lat, double Lng);

/// <summary>Google's encoded polyline format, as used by Strava's map.polyline and map.summary_polyline.</summary>
public static class Polyline
{
    public static List<LatLng> Decode(string? encoded)
    {
        var points = new List<LatLng>();
        if (string.IsNullOrEmpty(encoded)) return points;
        int index = 0, lat = 0, lng = 0;
        while (index < encoded.Length)
        {
            if (!TryNext(encoded, ref index, out var dLat) || !TryNext(encoded, ref index, out var dLng)) break;
            lat += dLat;
            lng += dLng;
            points.Add(new LatLng(lat / 1e5, lng / 1e5));
        }
        return points;
    }

    public static string Encode(IEnumerable<LatLng> points)
    {
        var sb = new StringBuilder();
        int lastLat = 0, lastLng = 0;
        foreach (var p in points)
        {
            int lat = (int)Math.Round(p.Lat * 1e5), lng = (int)Math.Round(p.Lng * 1e5);
            Write(sb, lat - lastLat);
            Write(sb, lng - lastLng);
            (lastLat, lastLng) = (lat, lng);
        }
        return sb.ToString();

        static void Write(StringBuilder sb, int v)
        {
            var u = v < 0 ? ~(v << 1) : v << 1;
            while (u >= 0x20) { sb.Append((char)((0x20 | (u & 0x1f)) + 63)); u >>= 5; }
            sb.Append((char)(u + 63));
        }
    }

    private static bool TryNext(string s, ref int index, out int value)
    {
        int result = 0, shift = 0, b;
        do
        {
            if (index >= s.Length) { value = 0; return false; }
            b = s[index++] - 63;
            result |= (b & 0x1f) << shift;
            shift += 5;
        } while (b >= 0x20 && shift < 35);
        value = (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        return true;
    }

    /// <summary>Strava's latlng stream: a JSON array of [lat, lng] pairs.</summary>
    public static List<LatLng> FromLatLngJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<LatLng>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in doc.RootElement.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2)
                list.Add(new LatLng(p[0].GetDouble(), p[1].GetDouble()));
        return list;
    }
}

/// <summary>
/// The visible area in Web Mercator pixel space at <see cref="Zoom"/>, the same space map tiles use,
/// so routes and optional tiles line up without a mapping library.
/// </summary>
public record MapView(int Zoom, double X0, double Y0, double Width, double Height)
{
    public const int TileSize = 256;
    public double WorldSize => TileSize * Math.Pow(2, Zoom);
}

public static class MapLayout
{
    public const int MaxZoom = 17;

    /// <summary>Web Mercator, normalised to 0..1 in both axes.</summary>
    public static (double X, double Y) Project(LatLng p)
    {
        var lat = Math.Clamp(p.Lat, -85.05112878, 85.05112878) * Math.PI / 180;
        return ((p.Lng + 180) / 360, (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2);
    }

    /// <summary>The highest zoom at which every point fits in the viewport (with padding), centred.</summary>
    public static MapView Fit(IEnumerable<LatLng> points, double width, double height, double padding = 24, int maxZoom = MaxZoom)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            var (x, y) = Project(p);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (minX > maxX) return new MapView(0, 0, 0, width, height);
        var zoom = maxZoom;
        for (; zoom > 0; zoom--)
        {
            var world = MapView.TileSize * Math.Pow(2, zoom);
            if ((maxX - minX) * world + 2 * padding <= width && (maxY - minY) * world + 2 * padding <= height) break;
        }
        var w = MapView.TileSize * Math.Pow(2, zoom);
        var cx = (minX + maxX) / 2 * w;
        var cy = (minY + maxY) / 2 * w;
        return new MapView(zoom, cx - width / 2, cy - height / 2, width, height);
    }

    /// <summary>SVG path data relative to the view, thinned to at most <paramref name="maxPoints"/> points.</summary>
    public static string ToPath(IReadOnlyList<LatLng> points, MapView view, int maxPoints = 2000)
    {
        if (points.Count == 0) return "";
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / (double)maxPoints));
        var sb = new StringBuilder(points.Count / step * 14);
        for (var i = 0; i < points.Count; i += step) Append(sb, i == 0 ? 'M' : 'L', points[i], view);
        if ((points.Count - 1) % step != 0) Append(sb, 'L', points[^1], view);
        return sb.ToString();
    }

    public static (double X, double Y) ToView(LatLng p, MapView v)
    {
        var (x, y) = Project(p);
        return (x * v.WorldSize - v.X0, y * v.WorldSize - v.Y0);
    }

    /// <summary>Tile coordinates covering the view.</summary>
    public static IEnumerable<(int X, int Y)> Tiles(MapView v)
    {
        var max = (1 << v.Zoom) - 1;
        for (var ty = Math.Max(0, (int)Math.Floor(v.Y0 / MapView.TileSize)); ty <= Math.Min(max, (int)Math.Floor((v.Y0 + v.Height) / MapView.TileSize)); ty++)
            for (var tx = Math.Max(0, (int)Math.Floor(v.X0 / MapView.TileSize)); tx <= Math.Min(max, (int)Math.Floor((v.X0 + v.Width) / MapView.TileSize)); tx++)
                yield return (tx, ty);
    }

    public static string TileUrl(string template, int z, int x, int y) =>
        template.Replace("{z}", z.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", x.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", y.ToString(CultureInfo.InvariantCulture));

    /// <summary>Great-circle distance in metres.</summary>
    public static double DistanceM(LatLng a, LatLng b)
    {
        const double R = 6_371_000;
        var dLat = (b.Lat - a.Lat) * Math.PI / 180;
        var dLng = (b.Lng - a.Lng) * Math.PI / 180;
        var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static void Append(StringBuilder sb, char cmd, LatLng p, MapView v)
    {
        var (x, y) = ToView(p, v);
        sb.Append(cmd).Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
    }
}
