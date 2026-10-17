using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StravaReports.Core.Archive;

/// <summary>Streaming RFC 4180 reader: quoted fields may contain commas, quotes and newlines.</summary>
public static class CsvReader
{
    public static IEnumerable<List<string>> Read(TextReader reader)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r':
                    if (reader.Peek() == '\n') reader.Read();
                    goto case '\n';
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    yield return record;
                    record = [];
                    any = false;
                    break;
                default: field.Append(ch); break;
            }
        }
        if (any)
        {
            record.Add(field.ToString());
            yield return record;
        }
    }
}

/// <summary>One parsed row of Strava's bulk-export activities.csv.</summary>
public sealed record ExportRow(long ActivityId, ArchivedActivity Values, string RawJson, string Hash);

/// <summary>
/// Maps Strava's activities.csv onto <see cref="ArchivedActivity"/>. The file repeats some headers
/// (Distance, Elapsed Time, Max Heart Rate, Relative Effort, Commute): the first Distance is in
/// kilometres for display, and the later one is metres, which is the one used.
/// </summary>
public sealed class StravaExportCsv
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, List<int>> _index = new(StringComparer.OrdinalIgnoreCase);

    public StravaExportCsv(IReadOnlyList<string> header)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim().TrimStart('﻿');
            var n = seen[name] = seen.GetValueOrDefault(name) + 1;
            _keys.Add(n == 1 ? name : $"{name} ({n})");
            if (!_index.TryGetValue(name, out var list)) _index[name] = list = [];
            list.Add(i);
        }
        if (!_index.ContainsKey("Activity ID"))
            throw new FormatException("This file has no \"Activity ID\" column. Choose activities.csv from Strava's \"Download your data\" export, or the export .zip.");
    }

    public IReadOnlyList<string> Keys => _keys;

    /// <summary>Returns null for rows without a usable activity ID (blank lines, footers).</summary>
    public ExportRow? Parse(IReadOnlyList<string> fields)
    {
        if (!long.TryParse(First(fields, "Activity ID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0) return null;

        var raw = new Dictionary<string, string>(_keys.Count);
        for (var i = 0; i < _keys.Count; i++) raw[_keys[i]] = i < fields.Count ? fields[i] : "";
        var json = JsonSerializer.Serialize(raw);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

        var type = First(fields, "Activity Type") ?? "";
        var distanceColumns = _index.GetValueOrDefault("Distance")?.Count ?? 0;
        var distance = distanceColumns >= 2 ? Num(Last(fields, "Distance")) : Num(First(fields, "Distance")) * 1000;

        var a = new ArchivedActivity
        {
            ActivityId = id,
            Name = First(fields, "Activity Name") ?? "",
            ActivityType = type,
            SportType = NormaliseSportType(type),
            StartDateUtc = Date(First(fields, "Activity Date")),
            Description = Blank(First(fields, "Activity Description")),
            DistanceM = distance,
            MovingTimeS = Int(First(fields, "Moving Time")),
            ElapsedTimeS = Int(Last(fields, "Elapsed Time")),
            ElevationGainM = Num(First(fields, "Elevation Gain")),
            MaxSpeed = Num(First(fields, "Max Speed")),
            AvgSpeed = Num(First(fields, "Average Speed")),
            AvgHr = Num(First(fields, "Average Heart Rate")),
            MaxHr = Num(Last(fields, "Max Heart Rate")),
            AvgWatts = Num(First(fields, "Average Watts")),
            WeightedAvgWatts = Num(First(fields, "Weighted Average Power")),
            AvgCadence = Num(First(fields, "Average Cadence")),
            Calories = Num(First(fields, "Calories")),
            RelativeEffort = Num(Last(fields, "Relative Effort")),
            IsCommute = Bool(Last(fields, "Commute")),
            GearName = Blank(First(fields, "Activity Gear")),
            Filename = Blank(First(fields, "Filename")),
        };
        return new ExportRow(id, a, json, hash);
    }

    private string? First(IReadOnlyList<string> f, string name) =>
        _index.TryGetValue(name, out var idx) && idx[0] < f.Count ? f[idx[0]] : null;

    private string? Last(IReadOnlyList<string> f, string name)
    {
        if (!_index.TryGetValue(name, out var idx)) return null;
        // Prefer the last non-blank copy of a repeated column.
        for (var i = idx.Count - 1; i >= 0; i--)
            if (idx[i] < f.Count && !string.IsNullOrWhiteSpace(f[idx[i]])) return f[idx[i]];
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static string NormaliseSportType(string activityType) =>
        new(activityType.Where(char.IsLetterOrDigit).ToArray());

    public static double? Num(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        // Exports from some locales use a decimal comma.
        if (s.Contains(',') && !s.Contains('.') && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out v)) return v;
        return null;
    }

    private static int? Int(string? s) => Num(s) is { } v ? (int)Math.Round(v) : null;

    private static bool? Bool(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => null,
    };

    private static readonly string[] DateFormats =
    [
        "MMM d, yyyy, h:mm:ss tt", "MMM d, yyyy, H:mm:ss", "MMM d, yyyy h:mm:ss tt",
        "d MMM yyyy, HH:mm:ss", "d MMM yyyy HH:mm:ss", "dd MMM yyyy, HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss",
        "M/d/yyyy h:mm:ss tt", "dd/MM/yyyy HH:mm:ss", "dd.MM.yyyy HH:mm:ss",
    ];

    public static DateTime? Date(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Replace(' ', ' ').Replace(' ', ' ').Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces;
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-GB") })
            if (DateTime.TryParseExact(s, DateFormats, culture, styles, out var d)) return DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, styles, out var any) ? DateTime.SpecifyKind(any, DateTimeKind.Utc) : null;
    }
}
