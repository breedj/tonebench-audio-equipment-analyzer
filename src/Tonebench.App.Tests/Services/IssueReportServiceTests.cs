using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tonebench.App.Diagnostics;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Simulation;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Tests.Services;

/// <summary>
/// The issue report's zip. What is pinned here is the three properties a report is useless without,
/// each of which fails silently rather than loudly: that it contains <b>today's log</b> — the file
/// Serilog still has open — that a file or a document it cannot read costs a <b>warning</b> rather
/// than the whole report, and that <c>report.json</c> is <b>valid JSON</b> however odd the stored
/// settings are. The last is the one that cannot be eyeballed: it is written with
/// <c>Utf8JsonWriter</c> and passes stored documents through raw, so a single bad document would
/// otherwise produce a file that only fails when the maintainer tries to read it.
/// </summary>
public class IssueReportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"tonebench-issue-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // The pools hold the configuration database open, so they go first or the delete fails on
        // Windows -- the same ordering every other test here uses.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class PinnedFactory(SoundCardSetting soundCard)
        : AnalyzerDeviceFactory(NullLoggerFactory.Instance, DeviceKind.Simulated, soundCard)
    {
        public override IAnalyzerDevice Create(DeviceKind kind) => new SimulatedDevice();
    }

    private async Task<IssueReportService> BuildAsync(params (string Key, string Value)[] settings)
    {
        Directory.CreateDirectory(_root);
        string configPath = Path.Combine(_root, "config.db");
        var repo = new ConfigRepository(
            new ConfigConnectionFactory(new TonebenchConfigDatabaseOptions { Path = configPath }));

        await using (var connection = await SqliteConnectionFactory.OpenAtAsync(configPath, create: true, CancellationToken.None))
            await new MigrationRunner(MigrationRunner.ConfigResourcePrefix).RunAsync(connection, CancellationToken.None);

        foreach ((string key, string value) in settings)
            await repo.SetAsync(key, value, CancellationToken.None);

        var card = new SoundCardSetting();
        var pipeline = new SpectrumPipeline(new SpectrumBroadcastHub(), NullLogger<SpectrumPipeline>.Instance);
        var hub = new SpectrumBroadcastHub();
        var session = new DeviceSessionService(
            new PinnedFactory(card),
            pipeline,
            new CaptureDelaySetting(),
            NullLogger<DeviceSessionService>.Instance,
            new ExternalGainSetting(),
            card);

        foreach (string sub in (string[])["logs", "diagnostics", "recordings", "reports"])
            Directory.CreateDirectory(Path.Combine(_root, sub));

        return new IssueReportService(
            session,
            pipeline,
            hub,
            new DiagnosticRecorder(NullLogger<DiagnosticRecorder>.Instance),
            new StreamRecorder(NullLogger<StreamRecorder>.Instance, Path.Combine(_root, "recordings")),
            new DiagnosticLoggingSetting(new Serilog.Core.LoggingLevelSwitch()),
            new DatabaseHealth(),
            new CurrentProjectFile(),
            card,
            repo,
            NullLogger<IssueReportService>.Instance)
        {
            Paths = new IssueReportPaths(
                Path.Combine(_root, "logs"),
                Path.Combine(_root, "diagnostics"),
                Path.Combine(_root, "recordings"),
                Path.Combine(_root, "reports"),
                Path.Combine(_root, "nonexistent-config.db")),
        };
    }

    private static Dictionary<string, string> Read(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        var entries = new Dictionary<string, string>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            entries[entry.FullName] = reader.ReadToEnd();
        }

        return entries;
    }

    /// <summary>
    /// The log file the report exists for is <b>today's</b>, and Serilog holds it open for writing
    /// for as long as the app runs. Anything less than <see cref="FileShare.ReadWrite"/> on the way
    /// in throws "being used by another process" on exactly that file and on no other — so a report
    /// built on a developer's machine, where the app is not running, would look perfectly fine.
    /// </summary>
    [Fact]
    public async Task TheLog_IsCopiedWhileSomethingElseHasItOpenForWriting()
    {
        IssueReportService service = await BuildAsync();

        string log = Path.Combine(_root, "logs", $"tonebench-{DateTime.Today:yyyyMMdd}.log");
        await File.WriteAllTextAsync(log, "the line that explains everything\n");

        // Exactly how Serilog holds it: open, writable, shared for reading only.
        await using var held = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.Read);

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(LogDays: 1), CancellationToken.None);

        var entries = Read(result.Path);
        Assert.Contains($"logs/tonebench-{DateTime.Today:yyyyMMdd}.log", entries.Keys);
        Assert.Contains("the line that explains everything", entries[$"logs/tonebench-{DateTime.Today:yyyyMMdd}.log"]);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The answers reach both halves: <c>report.md</c>, which is what gets read first, and
    /// <c>report.json</c>, which is what gets grepped.
    /// </summary>
    [Fact]
    public async Task TheAnswers_AreInTheReadableReportAndTheMachineReadableOne()
    {
        IssueReportService service = await BuildAsync();

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(
                Answers:
                [
                    new IssueReportAnswer("what", "What went wrong?", "The second press said nothing is arriving."),
                    new IssueReportAnswer("steps", "What were you doing?", "Calibrating the MOTU, twice."),
                ]),
            CancellationToken.None);

        var entries = Read(result.Path);

        Assert.Contains("What went wrong?", entries["report.md"]);
        Assert.Contains("The second press said nothing is arriving.", entries["report.md"]);

        using JsonDocument json = JsonDocument.Parse(entries["report.json"]);
        JsonElement answers = json.RootElement.GetProperty("answers");
        Assert.Equal(
            "The second press said nothing is arriving.",
            answers.GetProperty("what").GetProperty("answer").GetString());
        Assert.Equal("What were you doing?", answers.GetProperty("steps").GetProperty("question").GetString());
    }

    /// <summary>
    /// <b>A stored document this build cannot parse is the interesting one</b>, so it goes in as a
    /// string rather than taking the report down with it — and <c>report.json</c> stays valid JSON
    /// either way. Both halves are the point: passing documents through with <c>WriteRawValue</c> is
    /// what keeps a newer build's settings readable, and it is also the one thing that could emit a
    /// broken file.
    /// </summary>
    [Fact]
    public async Task ASettingThatIsNotValidJson_IsCarriedAsTextAndLeavesTheReportParseable()
    {
        IssueReportService service = await BuildAsync(
            ("soundcard.settings", """{"inputDeviceName":"MOTU M Series","streamStartSettleMs":2000}"""),
            ("something.broken", "not json at all"));

        IssueReportResult result = await service.BuildAsync(new IssueReportRequest(), CancellationToken.None);
        var entries = Read(result.Path);

        using JsonDocument json = JsonDocument.Parse(entries["report.json"]);
        JsonElement stored = json.RootElement.GetProperty("settings");

        // The good one came through as an object, so it is still queryable.
        Assert.Equal(
            "MOTU M Series",
            stored.GetProperty("soundcard.settings").GetProperty("value").GetProperty("inputDeviceName").GetString());

        // The bad one came through as a string, and is still there to be looked at.
        Assert.Equal("not json at all", stored.GetProperty("something.broken").GetProperty("value").GetString());

        // settings.json beside it says the same thing, and parses too.
        using JsonDocument standalone = JsonDocument.Parse(entries["settings.json"]);
        Assert.True(standalone.RootElement.TryGetProperty("something.broken", out _));
    }

    /// <summary>
    /// <c>report.md</c>'s manifest is the list of what <b>actually</b> went in, not a legend of what
    /// a report can hold. Found by building one and reading it: the manifest was a fixed table, so
    /// the reader was told about a <c>screenshot.png</c> and a <c>recordings/</c> folder that were
    /// not in the zip in front of them — and had no way to tell an unticked box from a file that
    /// failed to copy. It is built from the same <c>included</c> list the result carries, which is
    /// assembled by the copying itself, so it cannot disagree with the archive.
    /// </summary>
    [Fact]
    public async Task TheReadableReportsManifest_NamesOnlyWhatIsActuallyInTheZip()
    {
        IssueReportService service = await BuildAsync(("soundcard.settings", "{}"));
        await File.WriteAllTextAsync(Path.Combine(_root, "recordings", "acq-in.wav"), "wav");

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(LogDays: 0, IncludeDiagnostics: false, IncludeRecordings: false),
            CancellationToken.None);

        var entries = Read(result.Path);
        string markdown = entries["report.md"];

        Assert.DoesNotContain("screenshot.png", markdown);
        Assert.DoesNotContain("recordings/", markdown);
        Assert.Contains("settings.json", markdown);

        // And every line of the manifest is one the result itself reports, so the two cannot drift.
        foreach (string line in result.Included)
            Assert.Contains(line, markdown);
    }

    /// <summary>
    /// A screenshot that will not decode costs a line in <c>Warnings</c> and nothing else. The
    /// report is the thing being asked for; the picture is an attachment to it.
    /// </summary>
    [Fact]
    public async Task AScreenshotThatWillNotDecode_IsAWarningRatherThanAFailure()
    {
        IssueReportService service = await BuildAsync();

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(ScreenshotPngBase64: "this is not base64 %%%"), CancellationToken.None);

        Assert.Contains("screenshot", Assert.Single(result.Warnings), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screenshot.png", Read(result.Path).Keys);
        Assert.Contains("report.md", Read(result.Path).Keys);
    }

    [Fact]
    public async Task AScreenshot_GoesInAsItsOwnFile()
    {
        IssueReportService service = await BuildAsync();

        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3, 4];
        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(ScreenshotPngBase64: Convert.ToBase64String(png)), CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(result.Path);
        ZipArchiveEntry entry = Assert.Single(zip.Entries, e => e.FullName == "screenshot.png");

        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.Equal(png, buffer.ToArray());
    }

    /// <summary>
    /// Only the <b>newest</b> recording set, and the whole of it. The folder keeps ten sets of up to
    /// 256 MB a file, so "the recordings" would be several gigabytes; the set that was being written
    /// when the fault was on screen is the one worth sending, and its sidecar has to travel with it
    /// or the volts in the WAV mean nothing.
    /// </summary>
    [Fact]
    public async Task TheRecordings_AreTheNewestSetOnly_SidecarIncluded()
    {
        IssueReportService service = await BuildAsync();
        string recordings = Path.Combine(_root, "recordings");

        foreach (string name in (string[])["old-in.wav", "old-out.wav", "old.json"])
        {
            await File.WriteAllTextAsync(Path.Combine(recordings, name), "old");
            File.SetLastWriteTimeUtc(Path.Combine(recordings, name), DateTime.UtcNow.AddHours(-2));
        }

        foreach (string name in (string[])["new-in.wav", "new-out.wav", "new.json"])
            await File.WriteAllTextAsync(Path.Combine(recordings, name), "new");

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(IncludeRecordings: true), CancellationToken.None);

        var keys = Read(result.Path).Keys;
        Assert.Contains("recordings/new-in.wav", keys);
        Assert.Contains("recordings/new-out.wav", keys);
        Assert.Contains("recordings/new.json", keys);
        Assert.DoesNotContain("recordings/old-in.wav", keys);
    }

    [Fact]
    public async Task WithoutTheRecordingsBox_NoWavIsCopiedHoweverManyAreThere()
    {
        IssueReportService service = await BuildAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "recordings", "new-in.wav"), "new");

        IssueReportResult result = await service.BuildAsync(
            new IssueReportRequest(IncludeRecordings: false), CancellationToken.None);

        Assert.DoesNotContain(Read(result.Path).Keys, k => k.StartsWith("recordings/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A user chasing one fault presses this more than once, and each press is a copy of files that
    /// still exist — so the folder is capped, the way the stream recorder caps its WAV sets.
    /// </summary>
    [Fact]
    public async Task OnlyTheNewestFewReportsAreKept()
    {
        IssueReportService service = await BuildAsync();

        for (int i = 0; i < IssueReportService.KeepNewestReports + 3; i++)
        {
            IssueReportResult built = await service.BuildAsync(new IssueReportRequest(), CancellationToken.None);

            // The file name carries whole seconds, so successive builds inside one second would
            // overwrite each other rather than accumulate; nudging the timestamps apart is what
            // makes "newest" mean anything here.
            File.SetLastWriteTimeUtc(built.Path, DateTime.UtcNow.AddSeconds(i));
            File.Move(built.Path, Path.Combine(Path.GetDirectoryName(built.Path)!, $"tonebench-issue-{i:00}.zip"));
        }

        Assert.Equal(
            IssueReportService.KeepNewestReports,
            Directory.GetFiles(Path.Combine(_root, "reports"), "*.zip").Length);
    }

    /// <summary>
    /// The dialog has to be able to say what it is about to send before the zip is built, because
    /// one of the boxes can turn a 300 kB report into a 500 MB one.
    /// </summary>
    [Fact]
    public async Task TheListing_NamesWhatIsThereNewestFirst()
    {
        IssueReportService service = await BuildAsync();

        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "tonebench-20261001.log"), "older");
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "logs", "tonebench-20261001.log"), DateTime.UtcNow.AddDays(-3));
        await File.WriteAllTextAsync(Path.Combine(_root, "logs", "tonebench-20261004.log"), "today");

        IssueReportContents contents = await service.DescribeAsync(CancellationToken.None);

        Assert.Equal("tonebench-20261004.log", contents.Logs[0].Name);
        Assert.Equal(2, contents.Logs.Count);
        Assert.Empty(contents.Recordings);
        Assert.Equal(Path.Combine(_root, "reports"), contents.ReportsDirectory);
    }
}
