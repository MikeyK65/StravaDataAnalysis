using System.Globalization;
using System.Text.Json;
using StravaReports.Core.Settings;

namespace StravaReports.Core.Analysis;

/// <summary>Decoded streams for one activity. Arrays are aligned by index; missing streams are null.</summary>
public sealed class StreamData
{
    public int[] Time { get; init; } = [];
    public double[]? Distance { get; init; }
    public double[]? Altitude { get; init; }
    public double[]? Velocity { get; init; }
    public double?[]? HeartRate { get; init; }
    public double?[]? Watts { get; init; }
    public double?[]? Cadence { get; init; }
    public bool[]? Moving { get; init; }

    public int Count => Time.Length;

    /// <summary>Builds from the stored JSON arrays keyed by Strava stream type.</summary>
    public static StreamData? FromJson(IReadOnlyDictionary<string, string> json)
    {
        if (!json.TryGetValue("time", out var timeJson)) return null;
        var time = ParseNumbers(timeJson).Select(v => (int)(v ?? 0)).ToArray();
        if (time.Length == 0) return null;
        double[]? Dense(string key) => json.TryGetValue(key, out var j) ? Align(ParseNumbers(j).Select(v => v ?? 0).ToArray(), time.Length) : null;
        double?[]? Sparse(string key) => json.TryGetValue(key, out var j) ? Align(ParseNumbers(j), time.Length) : null;

        return new StreamData
        {
            Time = time,
            Distance = Dense("distance"),
            Altitude = Dense("altitude"),
            Velocity = Dense("velocity_smooth"),
            HeartRate = Sparse("heartrate"),
            Watts = Sparse("watts"),
            Cadence = Sparse("cadence"),
            Moving = json.TryGetValue("moving", out var m) ? Align(ParseBools(m), time.Length) : null,
        };
    }

    private static T[] Align<T>(T[] values, int length)
    {
        if (values.Length == length) return values;
        var result = new T[length];
        Array.Copy(values, result, Math.Min(length, values.Length));
        return result;
    }

    internal static double?[] ParseNumbers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        var list = new double?[doc.RootElement.GetArrayLength()];
        var i = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
            list[i++] = el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
        return list;
    }

    private static bool[] ParseBools(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.True).ToArray();
    }
}

/// <summary>Pure stream and training-load maths, free of EF so it is easy to test.</summary>
public static class StreamMath
{
    /// <summary>Durations (seconds) of the power curve.</summary>
    public static readonly int[] PeakDurations = [5, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];

    /// <summary>Gaps longer than this are treated as stopped (zero power), not carried forward.</summary>
    public const int MaxCarryForwardSeconds = 5;

    /// <summary>Resamples an irregular stream to one value per second, carrying values across short gaps.</summary>
    public static double[] Resample1Hz(int[] time, double?[] values)
    {
        if (time.Length == 0) return [];
        var length = Math.Max(0, time[^1] - time[0]) + 1;
        var result = new double[length];
        for (var i = 0; i < time.Length; i++)
        {
            var start = time[i] - time[0];
            var v = values[i] ?? 0;
            var next = i + 1 < time.Length ? time[i + 1] - time[0] : start + 1;
            var end = next - start <= MaxCarryForwardSeconds ? next : start + 1;
            for (var t = start; t < end && t < length; t++) result[t] = v;
        }
        return result;
    }

    /// <summary>Best mean value over each window length, using prefix sums. Windows longer than the data are skipped.</summary>
    public static Dictionary<int, double> BestMeans(double[] perSecond, IEnumerable<int> windows)
    {
        var prefix = new double[perSecond.Length + 1];
        for (var i = 0; i < perSecond.Length; i++) prefix[i + 1] = prefix[i] + perSecond[i];
        var result = new Dictionary<int, double>();
        foreach (var w in windows)
        {
            if (w <= 0 || w > perSecond.Length) continue;
            var best = 0.0;
            for (var i = w; i <= perSecond.Length; i++) best = Math.Max(best, prefix[i] - prefix[i - w]);
            result[w] = best / w;
        }
        return result;
    }

    /// <summary>Normalised power: fourth root of the mean of the fourth power of 30 s rolling averages.</summary>
    public static double? NormalizedPower(double[] perSecond)
    {
        if (perSecond.Length < 30) return null;
        double window = 0, sum4 = 0;
        var n = 0;
        for (var i = 0; i < perSecond.Length; i++)
        {
            window += perSecond[i];
            if (i >= 30) window -= perSecond[i - 30];
            if (i < 29) continue;
            var avg = window / 30;
            sum4 += avg * avg * avg * avg;
            n++;
        }
        return Math.Pow(sum4 / n, 0.25);
    }

    /// <summary>
    /// Seconds spent at each heart rate (bpm, rounded), counting only moving samples. Each sample
    /// is credited with the time since the previous one; gaps over 30 s are ignored as pauses.
    /// </summary>
    public static Dictionary<int, double> HeartRateHistogram(int[] time, double?[] hr, bool[]? moving)
    {
        var bins = new Dictionary<int, double>();
        for (var i = 1; i < time.Length; i++)
        {
            var dt = time[i] - time[i - 1];
            if (dt <= 0 || dt > 30) continue;
            if (moving is not null && !moving[i]) continue;
            if (hr[i] is not { } bpm || bpm <= 0) continue;
            var key = (int)Math.Round(bpm);
            bins[key] = bins.GetValueOrDefault(key) + dt;
        }
        return bins;
    }

    /// <summary>
    /// Splits a histogram into five zones, scaled so the zones sum exactly to <paramref name="movingTimeS"/>
    /// (Strava's own moving time), which keeps zone reports consistent with every other total.
    /// </summary>
    public static double[] ZoneSeconds(IReadOnlyDictionary<int, double> histogram, HrZoneSet zones, int movingTimeS)
    {
        var result = new double[5];
        foreach (var (bpm, seconds) in histogram) result[zones.ZoneOf(bpm)] += seconds;
        var total = result.Sum();
        if (total <= 0 || movingTimeS <= 0) return result;
        var scale = movingTimeS / total;
        for (var z = 0; z < 5; z++) result[z] *= scale;
        return result;
    }

    /// <summary>Banister TRIMP from average heart rate (male weighting, 0.64·e^(1.92·x)).</summary>
    public static double? Trimp(int movingTimeS, double? avgHr, int maxHr, int restingHr)
    {
        if (avgHr is not { } hr || hr <= restingHr || maxHr <= restingHr || movingTimeS <= 0) return null;
        var x = Math.Clamp((hr - restingHr) / (maxHr - restingHr), 0, 1);
        return movingTimeS / 60.0 * x * 0.64 * Math.Exp(1.92 * x);
    }

    /// <summary>Training stress score: duration × NP × IF / (FTP × 3600) × 100.</summary>
    public static double? Tss(int movingTimeS, double? normalizedPower, int? ftp)
    {
        if (normalizedPower is not { } np || ftp is not { } f || f <= 0 || movingTimeS <= 0) return null;
        var intensity = np / f;
        return movingTimeS * np * intensity / (f * 3600.0) * 100;
    }

    /// <summary>
    /// Exponentially weighted fitness (CTL, 42 days) and fatigue (ATL, 7 days) over consecutive daily loads.
    /// Form (TSB) is yesterday's fitness minus yesterday's fatigue, as most training tools report it.
    /// </summary>
    public static List<LoadDay> FitnessFatigue(IReadOnlyList<(DateOnly Day, double Load)> dailyLoads, int ctlDays = 42, int atlDays = 7)
    {
        var result = new List<LoadDay>(dailyLoads.Count);
        double ctl = 0, atl = 0;
        foreach (var (day, load) in dailyLoads)
        {
            var form = ctl - atl;
            ctl += (load - ctl) / ctlDays;
            atl += (load - atl) / atlDays;
            result.Add(new LoadDay(day, load, ctl, atl, form));
        }
        return result;
    }

    /// <summary>Evenly reduces a series to at most <paramref name="max"/> points by averaging buckets.</summary>
    public static List<(double X, double? Y)> Downsample(IReadOnlyList<double> x, IReadOnlyList<double?> y, int max)
    {
        var n = Math.Min(x.Count, y.Count);
        var result = new List<(double, double?)>(Math.Min(n, max));
        if (n == 0) return result;
        var bucket = Math.Max(1, (int)Math.Ceiling(n / (double)max));
        for (var start = 0; start < n; start += bucket)
        {
            var end = Math.Min(n, start + bucket);
            double sum = 0; var count = 0;
            for (var i = start; i < end; i++) if (y[i] is { } v && !double.IsNaN(v)) { sum += v; count++; }
            result.Add((x[end - 1], count == 0 ? null : sum / count));
        }
        return result;
    }
}

public record LoadDay(DateOnly Day, double Load, double Fitness, double Fatigue, double Form);

/// <summary>Five heart-rate zones defined by the upper bounds (bpm) of zones 1–4.</summary>
public sealed record HrZoneSet(int[] UpperBounds, string Source)
{
    public static readonly string[] Names = ["Z1 Recovery", "Z2 Endurance", "Z3 Tempo", "Z4 Threshold", "Z5 Max"];
    public static readonly double[] MaxHrFractions = [0.60, 0.70, 0.80, 0.90];

    public int ZoneOf(double bpm)
    {
        for (var z = 0; z < 4; z++) if (bpm <= UpperBounds[z]) return z;
        return 4;
    }

    public string Range(int zone) => zone switch
    {
        0 => $"≤ {UpperBounds[0]}",
        4 => $"> {UpperBounds[3]}",
        _ => $"{UpperBounds[zone - 1] + 1}–{UpperBounds[zone]}",
    };

    /// <summary>Custom bounds from Settings first, then percentages of max HR (set, or the highest seen).</summary>
    public static HrZoneSet? Resolve(AppSettingsModel s, double? observedMaxHr)
    {
        if (TryParse(s.HrZones) is { } custom) return new HrZoneSet(custom, "custom zones from Settings");
        var max = s.MaxHr ?? (observedMaxHr is { } o && o > 100 ? (int)Math.Round(o) : null);
        if (max is not { } m) return null;
        var source = s.MaxHr is not null ? $"60/70/80/90% of max HR {m}" : $"60/70/80/90% of highest recorded HR {m} (set max HR in Settings)";
        return new HrZoneSet(MaxHrFractions.Select(f => (int)Math.Round(m * f)).ToArray(), source);
    }

    public static int[]? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        var values = new int[4];
        for (var i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])) return null;
        for (var i = 1; i < 4; i++) if (values[i] <= values[i - 1]) return null;
        return values;
    }
}
