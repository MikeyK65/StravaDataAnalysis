using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StravaReports.Core.Archive;

namespace StravaReports.Tests;

public class ArchiveTests
{
    // A trimmed copy of Strava's activities.csv layout, including its repeated headers.
    private const string Header =
        "Activity ID,Activity Date,Activity Name,Activity Type,Activity Description,Elapsed Time,Distance,Max Heart Rate,Relative Effort,Commute," +
        "Activity Gear,Filename,Elapsed Time,Moving Time,Distance,Max Speed,Average Speed,Elevation Gain,Average Heart Rate,Average Watts,Calories,Relative Effort,Commute";

    private const string Run1001 = "1001,\"Jan 5, 2020, 7:30:12 AM\",Morning Run,Run,\"Felt good,\nnew \"\"fast\"\" shoes\",3100,10.02,170,45,false,Trainers,activities/1001.fit.gz,3100.0,3000.0,10020.0,4.5,3.34,55.0,150.0,,700,45,0";
    private const string Ride1002 = "1002,\"Feb 1, 2020, 9:00:00 AM\",Zwift,Virtual Ride,,3600,30.5,160,60,false,,activities/1002.fit.gz,3600.0,3600.0,30500.0,12,8.47,200,140,210,800,60,1";

    private static Stream Csv(params string[] rows) =>
        new MemoryStream(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", [Header, .. rows]) + "\r\n")).ToArray());

    private static async Task<List<ArchivedActivity>> AllAsync(TestHost host)
    {
        await using var db = await host.Get<IDbContextFactory<ArchiveDbContext>>().CreateDbContextAsync();
        return await db.Activities.AsNoTracking().OrderBy(a => a.ActivityId).ToListAsync();
    }

    [Fact]
    public async Task Repeat_imports_never_duplicate_and_pick_up_changes()
    {
        await using var host = await TestHost.CreateAsync();
        var archive = host.Get<ArchiveService>();

        var first = await archive.ImportAsync(Csv(Run1001, Ride1002), "activities.csv");
        Assert.True(first.Success, first.Batch.Message);
        Assert.Equal((2, 0, 0), (first.Batch.Inserted, first.Batch.Updated, first.Batch.Unchanged));

        var run = (await AllAsync(host)).Single(a => a.ActivityId == 1001);
        Assert.Equal(10_020, run.DistanceM);                       // metres column, not the km one
        Assert.Equal((3000, 3100), (run.MovingTimeS, run.ElapsedTimeS));
        Assert.Equal(new DateTime(2020, 1, 5, 7, 30, 12, DateTimeKind.Utc), run.StartDateUtc);
        Assert.Equal("Felt good,\nnew \"fast\" shoes", run.Description);
        Assert.False(run.IsCommute);
        Assert.Contains("\"Distance (2)\":\"10020.0\"", run.RawJson);
        var ride = (await AllAsync(host)).Single(a => a.ActivityId == 1002);
        Assert.Equal(("Virtual Ride", "VirtualRide", 210.0, true), (ride.ActivityType, ride.SportType, ride.AvgWatts, ride.IsCommute));

        var again = await archive.ImportAsync(Csv(Run1001, Ride1002), "activities.csv");
        Assert.Equal((0, 0, 2), (again.Batch.Inserted, again.Batch.Updated, again.Batch.Unchanged));
        Assert.Equal(2, (await AllAsync(host)).Count);

        // A newer export: one renamed activity, one new one, and a row repeated within the file.
        var renamed = Ride1002.Replace(",Zwift,", ",Zwift - Watopia,");
        const string run1003 = "1003,\"Mar 1, 2020, 6:00:00 PM\",Evening Run,Run,,1800,5.0,165,20,false,Trainers,,1800,1750,5000,4,2.86,20,145,,350,20,0";
        var third = await archive.ImportAsync(Csv(Run1001, renamed, run1003, run1003), "export_2.zip.csv");
        Assert.Equal((1, 1, 1, 1), (third.Batch.Inserted, third.Batch.Updated, third.Batch.Unchanged, third.Batch.DuplicatesInFile));

        var all = await AllAsync(host);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Select(a => a.ActivityId).Distinct().Count());
        var updated = all.Single(a => a.ActivityId == 1002);
        Assert.Equal("Zwift - Watopia", updated.Name);
        Assert.Equal((first.Batch.Id, third.Batch.Id), (updated.FirstBatchId, updated.LastBatchId));
        Assert.Equal(new DateTime(2020, 3, 1, 18, 0, 0, DateTimeKind.Utc), all.Single(a => a.ActivityId == 1003).StartDateUtc);

        var summary = await archive.SummaryAsync();
        Assert.Equal((3, 0, 3), (summary.Activities, summary.AlsoSynced, summary.OnlyInArchive));
        Assert.Equal(2, summary.Years.Single().Runs);
        Assert.Equal(3, (await archive.BatchesAsync()).Count(b => b.Status == "Completed"));
    }

    [Fact]
    public async Task Imports_activities_csv_from_inside_the_export_zip()
    {
        await using var host = await TestHost.CreateAsync();
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var w = new StreamWriter(zip.CreateEntry("export_123/activities.csv").Open())) await w.WriteAsync(Header + "\n" + Run1001 + "\n");
            await using (var w = new StreamWriter(zip.CreateEntry("export_123/activities/1001.gpx").Open())) await w.WriteAsync("<gpx/>");
        }
        zipBytes.Position = 0;
        // Uploads are not seekable, which forces the temp-file path.
        var result = await host.Get<ArchiveService>().ImportAsync(new NonSeekable(zipBytes), "export_123.zip");

        Assert.True(result.Success, result.Batch.Message);
        Assert.Equal(1001, (await AllAsync(host)).Single().ActivityId);
    }

    [Fact]
    public async Task A_bad_file_fails_cleanly_and_changes_nothing()
    {
        await using var host = await TestHost.CreateAsync();
        var archive = host.Get<ArchiveService>();
        await archive.ImportAsync(Csv(Run1001), "activities.csv");

        var bad = await archive.ImportAsync(new MemoryStream("Name,Date\nfoo,bar\n"u8.ToArray()), "other.csv");

        Assert.False(bad.Success);
        Assert.Equal("Failed", bad.Batch.Status);
        Assert.Contains("Activity ID", bad.Batch.Message);
        Assert.Single(await AllAsync(host));

        await archive.DeleteAllAsync();
        Assert.Empty(await AllAsync(host));
        Assert.Empty(await archive.BatchesAsync());
    }

    [Fact]
    public async Task Upload_streams_that_only_read_asynchronously_are_supported()
    {
        // Blazor Server's browser file stream throws on synchronous reads; the importer must never use them.
        await using var host = await TestHost.CreateAsync();
        var result = await host.Get<ArchiveService>().ImportAsync(new AsyncOnly(Csv(Run1001, Ride1002)), "activities.csv");
        Assert.True(result.Success, result.Batch.Message);
        Assert.Equal(2, result.Batch.Inserted);
    }

    [Fact]
    public async Task An_interrupted_import_is_marked_and_does_not_block_the_next()
    {
        await using var host = await TestHost.CreateAsync();
        var archive = host.Get<ArchiveService>();
        var interrupted = await archive.ImportAsync(new FailingStream(), "activities.csv");
        Assert.False(interrupted.Success);
        Assert.Equal("Failed", interrupted.Batch.Status);

        var ok = await archive.ImportAsync(Csv(Run1001), "activities.csv");
        Assert.True(ok.Success);
        Assert.DoesNotContain(await archive.BatchesAsync(), b => b.Status == "Running");
    }

    [Theory]
    [InlineData("Jan 5, 2020, 7:30:12 AM")]
    [InlineData("5 Jan 2020, 07:30:12")]
    [InlineData("2020-01-05 07:30:12")]
    public void Parses_export_dates_as_utc(string text) =>
        Assert.Equal(new DateTime(2020, 1, 5, 7, 30, 12, DateTimeKind.Utc), StravaExportCsv.Date(text));

    [Fact]
    public void Csv_reader_handles_quotes_and_embedded_newlines()
    {
        var rows = CsvReader.Read(new StringReader("a,\"b,1\",\"c\"\"d\"\r\n\"multi\nline\",,x")).ToList();
        Assert.Equal(new List<string> { "a", "b,1", "c\"d" }, rows[0]);
        Assert.Equal(new List<string> { "multi\nline", "", "x" }, rows[1]);
    }

    private sealed class AsyncOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Synchronous reads are not supported.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Simulates the browser going away mid-upload.</summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Circuit disconnected.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("Circuit disconnected.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
