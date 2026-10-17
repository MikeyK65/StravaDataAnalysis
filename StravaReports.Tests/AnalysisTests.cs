using StravaReports.Core.Analysis;
using StravaReports.Core.Export;
using StravaReports.Core.Settings;

namespace StravaReports.Tests;

public class AnalysisTests
{
    [Fact]
    public void Resample_carries_short_gaps_and_zeroes_long_ones()
    {
        var perSecond = StreamMath.Resample1Hz([0, 2, 20], [100, 200, 300]);
        Assert.Equal(21, perSecond.Length);
        Assert.Equal(new double[] { 100, 100, 200 }, perSecond[..3]);
        Assert.Equal(0, perSecond[10]);     // an 18 s gap is a stop, not 200 W held
        Assert.Equal(300, perSecond[20]);
    }

    [Fact]
    public void Best_means_and_normalised_power()
    {
        var steady = Enumerable.Repeat(250.0, 1300).ToArray();
        var peaks = StreamMath.BestMeans(steady, StreamMath.PeakDurations);
        Assert.Equal(250, peaks[1200], 6);
        Assert.False(peaks.ContainsKey(1800));
        Assert.Equal(250, StreamMath.NormalizedPower(steady)!.Value, 6);

        // Surges raise NP above the average.
        var surges = Enumerable.Range(0, 600).Select(i => i / 60 % 2 == 0 ? 350.0 : 150.0).ToArray();
        Assert.True(StreamMath.NormalizedPower(surges) > surges.Average());
        Assert.Equal(350, StreamMath.BestMeans(surges, [60])[60], 6);
    }

    [Fact]
    public void Tss_is_100_for_an_hour_at_ftp_and_trimp_rises_with_intensity()
    {
        Assert.Equal(100, StreamMath.Tss(3600, 250, 250)!.Value, 6);
        Assert.Null(StreamMath.Tss(3600, 250, null));
        var easy = StreamMath.Trimp(3600, 130, 190, 50)!.Value;
        var hard = StreamMath.Trimp(3600, 170, 190, 50)!.Value;
        Assert.True(hard > easy * 1.5);
        Assert.Null(StreamMath.Trimp(3600, null, 190, 50));
    }

    [Fact]
    public void Zone_seconds_sum_to_moving_time()
    {
        var zones = new HrZoneSet([120, 140, 160, 180], "test");
        int[] time = [0, 1, 2, 3, 63, 64];
        double?[] hr = [110, 115, 150, 150, 170, 190];
        bool[] moving = [true, true, true, false, true, true];
        var hist = StreamMath.HeartRateHistogram(time, hr, moving);
        // The paused sample and the 60 s gap are not counted.
        Assert.Equal(3, hist.Values.Sum());

        var z = StreamMath.ZoneSeconds(hist, zones, movingTimeS: 1234);
        Assert.Equal(1234, z.Sum(), 6);
        Assert.Equal(1234 / 3.0, z[0], 6);
        Assert.Equal(0, z[3]);
        Assert.Equal(1234 / 3.0, z[4], 6);
    }

    [Fact]
    public void Zone_sets_from_settings_or_max_hr()
    {
        Assert.Equal(new[] { 130, 145, 160, 175 }, HrZoneSet.Resolve(new AppSettingsModel { HrZones = "130, 145,160 175" }, null)!.UpperBounds);
        Assert.Null(HrZoneSet.TryParse("150,140,160,170"));
        Assert.Equal(new[] { 114, 133, 152, 171 }, HrZoneSet.Resolve(new AppSettingsModel { MaxHr = 190 }, 200)!.UpperBounds);
        Assert.Contains("highest recorded", HrZoneSet.Resolve(new AppSettingsModel(), 180)!.Source);
        Assert.Null(HrZoneSet.Resolve(new AppSettingsModel(), null));
        var set = new HrZoneSet([120, 140, 160, 180], "");
        Assert.Equal((0, 1, 4), (set.ZoneOf(120), set.ZoneOf(121), set.ZoneOf(181)));
    }

    [Fact]
    public void Fitness_and_fatigue_approach_a_steady_load()
    {
        var start = new DateOnly(2026, 1, 1);
        var days = Enumerable.Range(0, 400).Select(i => (start.AddDays(i), 50.0)).ToList();
        var series = StreamMath.FitnessFatigue(days);
        Assert.Equal(50, series[^1].Fitness, 0);
        Assert.Equal(50, series[^1].Fatigue, 3);
        Assert.True(series[10].Fatigue > series[10].Fitness);   // fatigue responds faster
        Assert.True(series[10].Form < 0);
    }

    [Fact]
    public void Downsample_keeps_shape_within_the_limit()
    {
        var x = Enumerable.Range(0, 2000).Select(i => (double)i).ToList();
        var y = x.Select(v => (double?)v).ToList();
        var d = StreamMath.Downsample(x, y, 500);
        Assert.Equal(500, d.Count);
        Assert.Equal(1999, d[^1].X);
        Assert.Equal(1.5, d[0].Y);
    }

    [Fact]
    public void Csv_escapes_quotes_newlines_and_formula_text()
    {
        var csv = new CsvBuilder().Row("a,b", "say \"hi\"", "line1\nline2", "=HYPERLINK(1)", -5.25, 3.0, null, true).ToString();
        Assert.Equal("\"a,b\",\"say \"\"hi\"\"\",\"line1\nline2\",'=HYPERLINK(1),-5.25,3,,true\r\n", csv);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, new CsvBuilder().Row("x").ToBytes()[..3]);
    }
}
