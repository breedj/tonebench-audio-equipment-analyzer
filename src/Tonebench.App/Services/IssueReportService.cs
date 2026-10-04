using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tonebench.App.Diagnostics;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.SoundCard;

namespace Tonebench.App.Services;

/// <summary>One of the dialog's questions, with what the user typed under it.</summary>
/// <param name="Question">
/// The question as the user read it, sent with the answer rather than looked up here. The dialog
/// owns the wording — it is what is on screen — and a server-side copy of the list would be a second
/// spelling of it to keep in step; sending the pair means the report cannot print a question that
/// was not asked.
/// </param>
public sealed record IssueReportAnswer(string Id, string Question, string Answer);

/// <summary>
/// What the dialog asks for, and which of the machine's own files to put in beside it.
/// </summary>
/// <param name="ScreenshotPngBase64">
/// The chart on screen, as the client's own <c>exportPng</c> produced it, base64 without a data-URL
/// prefix. Null when the user unticked it or the screen has no chart to export. A picture of the
/// <i>whole</i> window would need a capture API the browser prompts for every time; what makes this
/// worth having instead is that the state dump beside it says far more than pixels can.
/// </param>
/// <param name="LogDays">
/// How many days of log to include, counting back from today. 1 is today's file alone — which is
/// also the one that is still open and being written, hence the share-mode read below.
/// </param>
/// <param name="IncludeRecordings">
/// The newest stream-recording set. Off unless asked for: the cap is 256 MB a file and a set is two
/// of them, so this is the one choice here that can turn a 300 kB report into a 500 MB one. Its
/// value is that it is the only thing that can show the *wrong stimulus* having been played.
/// </param>
public sealed record IssueReportRequest(
    IReadOnlyList<IssueReportAnswer>? Answers = null,
    string? ScreenshotPngBase64 = null,
    int LogDays = 2,
    bool IncludeDiagnostics = true,
    bool IncludeRecordings = false,
    bool IncludeSettings = true,
    string? ClientStateJson = null);

/// <summary>One file the report could carry, so the dialog can say what it is about to send.</summary>
public sealed record IssueReportFile(string Name, long Bytes, string? ModifiedUtc = null);

/// <summary>
/// What is on this machine to put in a report — the dialog's tick boxes draw themselves from this,
/// so a user is told the size before a 500 MB zip is built rather than after.
/// </summary>
public sealed record IssueReportContents(
    IReadOnlyList<IssueReportFile> Logs,
    IReadOnlyList<IssueReportFile> Diagnostics,
    IReadOnlyList<IssueReportFile> Recordings,
    long SettingsBytes,
    bool StreamRecordingEnabled,
    bool DiagnosticLoggingEnabled,
    string ReportsDirectory);

/// <summary>
/// Which folders a report reads and writes.
///
/// <para>A record rather than <see cref="TonebenchAppData"/> read directly, for the reason
/// <c>StreamRecorder</c>'s directory parameter exists: a test for this service must not write into
/// the user's own data folder, and one that does is a test nobody will run twice. An <c>init</c>
/// property with a default rather than a constructor parameter, so the container still resolves the
/// service by its primary constructor alone and only a test spells this out.</para>
/// </summary>
public sealed record IssueReportPaths(
    string Logs, string Diagnostics, string Recordings, string Reports, string ConfigDatabase)
{
    public static IssueReportPaths Default { get; } = new(
        TonebenchAppData.LogsDirectory,
        TonebenchAppData.DiagnosticsDirectory,
        TonebenchAppData.RecordingsDirectory,
        TonebenchAppData.IssueReportsDirectory,
        TonebenchAppData.ConfigDatabasePath);
}

/// <summary>The finished zip: where it is, how big, and what went into it (or did not, and why).</summary>
public sealed record IssueReportResult(
    string FileName,
    string Path,
    long Bytes,
    IReadOnlyList<string> Included,
    IReadOnlyList<string> Warnings);

/// <summary>
/// <b>Report an issue</b>: the user's description of a fault, plus everything about this bench that
/// would otherwise have to be asked for one question at a time, in one zip they can send.
///
/// <para>The point is the <i>bundling</i>. Every fault this app has had was reported from a bench
/// the maintainer cannot see, and the first two or three exchanges on each were always the same —
/// which analyzer, which calibration, what was it playing, is the log still there. Each of those is
/// already recorded somewhere under <see cref="TonebenchAppData"/>; what was missing was a way to
/// collect them at the moment the fault was on screen, which is the only moment the live state
/// exists at all.</para>
///
/// <para><b>Written to a file, not returned as a response body.</b> With a recording ticked the zip
/// is hundreds of megabytes, which is not a thing to push through a JSON response and a browser
/// Blob; and a file in a known folder is still there tomorrow, when the user gets round to
/// attaching it. The dialog then offers a download and an <i>Open folder</i>, the same pair the
/// diagnostics and recordings settings already offer.</para>
///
/// <para><b>Nothing here leaves the machine.</b> The app has no uploader and gains none: it writes
/// a zip and tells the user where it is. That is deliberate rather than unfinished — the contents
/// include this bench's calibrations and its project paths, and the person who decides whether that
/// is sent anywhere is the person at the bench.</para>
/// </summary>
public sealed class IssueReportService(
    DeviceSessionService session,
    SpectrumPipeline pipeline,
    SpectrumBroadcastHub hub,
    DiagnosticRecorder diagnostics,
    StreamRecorder recorder,
    DiagnosticLoggingSetting diagnosticLogging,
    DatabaseHealth databaseHealth,
    CurrentProjectFile currentProject,
    SoundCardSetting soundCard,
    ConfigRepository config,
    ILogger<IssueReportService> logger)
{
    /// <inheritdoc cref="IssueReportPaths"/>
    public IssueReportPaths Paths { get; init; } = IssueReportPaths.Default;

    /// <summary>How many reports are kept. The newest few, like the recorder's ten file sets.</summary>
    public const int KeepNewestReports = 5;

    /// <summary>The most log the dialog will offer. The retention is 14 days, and a fault nobody noticed for a fortnight is not this button's case.</summary>
    public const int MaxLogDays = 14;

    public async Task<IssueReportContents> DescribeAsync(CancellationToken ct)
    {
        var logs = ListFiles(Paths.Logs, "tonebench-*.log");
        var caps = ListFiles(Paths.Diagnostics, "*");
        var wavs = ListFiles(Paths.Recordings, "*");

        long settingsBytes = 0;
        try
        {
            var info = new FileInfo(Paths.ConfigDatabase);
            settingsBytes = info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            // A size is decoration here; the report itself reads the file through the repository.
        }

        // Read rather than remembered: the switch can be changed from another tab, and the dialog's
        // wording about what the recordings box will find depends on it.
        StreamRecordingConfig recording = await ConfigEndpoints.ReadStreamRecordingAsync(config, ct);

        return new IssueReportContents(
            logs, caps, wavs, settingsBytes, recording.Enabled, diagnosticLogging.Enabled, Paths.Reports);
    }

    public async Task<IssueReportResult> BuildAsync(IssueReportRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.Reports);
        PurgeOldReports();

        DateTimeOffset now = DateTimeOffset.Now;
        string fileName = $"tonebench-issue-{now:yyyyMMdd-HHmmss}.zip";
        string path = Path.Combine(Paths.Reports, fileName);

        var included = new List<string>();
        var warnings = new List<string>();

        // Snapshot the live state *before* the zip is opened, and before any file copying: the
        // device status is a question asked of a running stream, and the whole value of this button
        // is that it is pressed while the fault is on screen. Half a second of file I/O first and
        // the generator may have been stopped by the user in the meantime.
        string statusJson = await StatusJsonAsync(ct);
        IReadOnlyList<StoredSetting> settings = request.IncludeSettings
            ? await SafeSettingsAsync(warnings, ct)
            : [];

        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await WriteTextEntryAsync(
                zip, "report.json", BuildJson(request, now, statusJson, settings), ct);
            included.Add("report.json \u2014 every setting and the full device status, machine-readable");

            if (request.ScreenshotPngBase64 is { Length: > 0 } png)
            {
                try
                {
                    // NoCompression: a PNG is already deflated, so the second pass costs time and
                    // saves nothing measurable.
                    byte[] image = Convert.FromBase64String(png);
                    ZipArchiveEntry entry = zip.CreateEntry("screenshot.png", CompressionLevel.NoCompression);
                    await using Stream target = entry.Open();
                    await target.WriteAsync(image, ct);
                    included.Add($"screenshot.png ({Describe(image.Length)})");
                }
                catch (FormatException)
                {
                    warnings.Add("The screenshot could not be decoded and was left out.");
                }
            }

            CopyLogs(zip, Paths.Logs, Math.Clamp(request.LogDays, 0, MaxLogDays), included, warnings);

            if (request.IncludeDiagnostics)
            {
                CopyFolder(zip, Paths.Diagnostics, "diagnostics", "*", included, warnings);
            }

            if (request.IncludeRecordings)
            {
                CopyNewestRecordingSet(zip, Paths.Recordings, included, warnings);
            }

            if (request.IncludeSettings)
            {
                // The database's *documents*, not the file. A live SQLite database copied byte for
                // byte while this process holds it open with WAL on is a file whose last writes are
                // in a -wal nobody sent, so what goes in is the rows themselves — which is also the
                // readable form, and the form report.json already carries.
                await WriteTextEntryAsync(zip, "settings.json", SettingsJson(settings), ct);
                included.Add($"settings.json \u2014 {settings.Count} stored documents, calibrations included");
            }

            // **Last, so its manifest is the truth.** It used to be written first and listed what a
            // report *could* hold -- which meant the reader was told about a screenshot.png and a
            // recordings/ folder that were not in the zip in front of them, and had no way to tell
            // an unticked box from a file that failed to copy. Now it prints `included` and
            // `warnings`, which are assembled by the copying above, so the list cannot disagree
            // with the archive.
            await WriteTextEntryAsync(zip, "report.md", BuildMarkdown(request, now, included, warnings), ct);
        }

        long bytes = new FileInfo(path).Length;

        logger.LogInformation(
            "Issue report written: {Path} ({Bytes} bytes), including {Included}{Warnings}",
            path, bytes, string.Join("; ", included),
            warnings.Count == 0 ? string.Empty : $" — warnings: {string.Join("; ", warnings)}");

        return new IssueReportResult(fileName, path, bytes, included, warnings);
    }

    /// <summary>
    /// The live status as JSON, taken through the app's own serializer so the report says exactly
    /// what <c>GET /api/device</c> says. Never throws: a report about a device that has just been
    /// unplugged is precisely the report worth having.
    /// </summary>
    private async Task<string> StatusJsonAsync(CancellationToken ct)
    {
        try
        {
            DeviceStatusResponse status = await session.GetStatusAsync(ct);
            var (droppedMax, droppedTotal) = hub.DroppedFrames();
            return JsonSerializer.Serialize(
                status with
                {
                    DatabaseProblem = databaseHealth.Problem,
                    DroppedStreamFramesMax = droppedMax,
                    DroppedStreamFramesTotal = droppedTotal,
                },
                AppJsonContext.Default.DeviceStatusResponse);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the device status for the issue report.");
            return $"{{\"problem\":{JsonSerializer.Serialize(ex.Message)}}}";
        }
    }

    private async Task<IReadOnlyList<StoredSetting>> SafeSettingsAsync(List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await config.ListAllAsync(ct);
        }
        catch (Exception ex)
        {
            warnings.Add($"The settings could not be read ({ex.Message}).");
            return [];
        }
    }

    private string BuildMarkdown(
        IssueReportRequest request, DateTimeOffset now,
        IReadOnlyList<string> included, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tonebench issue report");
        sb.AppendLine();
        sb.AppendLine($"- **Written** {now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- **Version** {UpdateCheckService.CurrentVersion}");
        sb.AppendLine($"- **System** {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, "
            + $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, "
            + $".NET {Environment.Version}, {Environment.ProcessorCount} cores");
        sb.AppendLine($"- **Analyzer** {session.Kind}"
            + (session.Info is { } info ? $" — {info.ProductName}, serial {info.SerialNumber}, firmware {info.FirmwareVersion}" : " — not connected"));
        sb.AppendLine($"- **Project** {currentProject.Path ?? "none open"}");
        sb.AppendLine();

        sb.AppendLine("## What the user reported");
        sb.AppendLine();
        foreach (IssueReportAnswer answer in request.Answers ?? [])
        {
            sb.AppendLine($"### {answer.Question}");
            sb.AppendLine();
            sb.AppendLine(string.IsNullOrWhiteSpace(answer.Answer) ? "_(not answered)_" : answer.Answer.Trim());
            sb.AppendLine();
        }

        // A plain-text summary of the settings most reports turn out to be about, so the first read
        // of a report needs no JSON at all. Everything else is in report.json beside it.
        sb.AppendLine("## The bench at the moment of the report");
        sb.AppendLine();
        sb.AppendLine($"- Acquiring: {session.Acquiring}");
        sb.AppendLine(
            "- Sample rate: "
                + (session.StreamConfig is { } stream ? $"{stream.SampleRateHz:0} Hz" : "no stream open"));
        sb.AppendLine($"- Analysis: {pipeline.Options.FftSize} points, {pipeline.Options.Window} window, "
            + $"{pipeline.Options.Averaging} x{pipeline.Options.AverageCount}");
        sb.AppendLine($"- Input range: {session.InputRangeDbv:0.#} dBV, ranging {session.RangingMode}");
        sb.AppendLine($"- Output range: {session.OutputRangeDbv:0.#} dBV");
        sb.AppendLine($"- Capture delay: {session.CaptureDelayMs:0} ms, stream settle {session.StreamSettleMs:0} ms");
        sb.AppendLine($"- Dropped: {session.DroppedRxChunks} RX chunks, {session.DroppedBlocks} blocks this acquisition");

        if (session.Kind == DeviceKind.SoundCard)
        {
            SoundCardConfig card = soundCard.Config;
            sb.AppendLine($"- Sound card: {card.InputHostApi} / {card.InputDeviceName} + {card.OutputDeviceName}, "
                + $"channels {card.InputChannelOffset + 1}/{card.InputChannelOffset + 2}, settle {card.StreamStartSettleMs:0} ms");
            sb.AppendLine($"- Sound card calibration: output {Mv(card.OutputFullScalePeakMv)}, "
                + $"input {Mv(card.InputFullScalePeakMvLeft)} / {Mv(card.InputFullScalePeakMvRight)} mV peak, "
                + $"front-end gain {card.FrontEndGainLeftDb:0.##} / {card.FrontEndGainRightDb:0.##} dB, "
                + $"calibrated {card.CalibratedUtc?.ToString("u") ?? "never"}");
        }

        if (databaseHealth.Problem is { Length: > 0 } problem)
            sb.AppendLine($"- **Database problem:** {problem}");

        sb.AppendLine();
        sb.AppendLine("## What else is in this zip");
        sb.AppendLine();
        foreach (string line in included)
            sb.AppendLine($"- {line}");

        if (warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### What could not be included");
            sb.AppendLine();
            foreach (string line in warnings)
                sb.AppendLine($"- {line}");
        }

        return sb.ToString();

        static string Mv(double? mv) => mv is { } v ? v.ToString("0.0") : "not measured";
    }

    /// <summary>
    /// The machine-readable half, written with <see cref="Utf8JsonWriter"/> rather than serialized.
    ///
    /// <para>Two reasons, and the first is the load-bearing one. The stored settings are
    /// <i>already</i> JSON, as text, including documents this build may not be able to deserialize —
    /// which is exactly the case worth reporting — and <c>WriteRawValue</c> passes them through
    /// untouched where a typed model would have to parse them first and could refuse. The second is
    /// trimming: a published build has reflection-based serialization switched off entirely (see
    /// <see cref="AppJsonContext"/>), so an ad-hoc object graph is not an option here at all.</para>
    /// </summary>
    private string BuildJson(
        IssueReportRequest request, DateTimeOffset now, string statusJson, IReadOnlyList<StoredSetting> settings)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();

            w.WriteString("writtenUtc", now.UtcDateTime);
            w.WriteString("writtenLocal", now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            w.WriteString("version", UpdateCheckService.CurrentVersion);

            w.WriteStartObject("environment");
            w.WriteString("os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            w.WriteString("osVersion", Environment.OSVersion.Version.ToString());
            w.WriteString("architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            w.WriteString("runtime", Environment.Version.ToString());
            w.WriteNumber("cores", Environment.ProcessorCount);
            w.WriteNumber("availableMemoryBytes", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
            w.WriteBoolean("serverGc", System.Runtime.GCSettings.IsServerGC);
            w.WriteString("dataDirectory", TonebenchAppData.RootDirectory);
            w.WriteBoolean("detailedLogging", diagnosticLogging.Enabled);
            w.WriteEndObject();

            w.WriteStartObject("answers");
            foreach (IssueReportAnswer answer in request.Answers ?? [])
            {
                w.WriteStartObject(answer.Id);
                w.WriteString("question", answer.Question);
                w.WriteString("answer", answer.Answer);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WritePropertyName("deviceStatus");
            w.WriteRawValue(statusJson, skipInputValidation: false);

            w.WritePropertyName("pipelineOptions");
            w.WriteRawValue(
                JsonSerializer.Serialize(pipeline.Options, AppJsonContext.Default.SpectrumPipelineOptions),
                skipInputValidation: false);

            w.WriteStartObject("project");
            w.WriteString("path", currentProject.Path);
            w.WriteString("databaseProblem", databaseHealth.Problem);
            w.WriteEndObject();

            w.WriteStartObject("diagnostics");
            w.WriteBoolean("recorderArmed", diagnostics.Enabled);
            w.WriteBoolean("streamRecorderArmed", recorder.Enabled);
            w.WriteString("recordingsDirectory", recorder.Directory);
            w.WriteEndObject();

            w.WritePropertyName("settings");
            WriteSettings(w, settings);

            // Whatever the browser knows and the server cannot: its own user agent, the window
            // size, which measurement was selected and the settings staged in its panels. Passed
            // through as raw JSON for the same reason the stored documents are -- the client owns
            // that vocabulary, and a server-side model of it would be a second copy to keep in
            // step with every panel that gains a field.
            w.WritePropertyName("client");
            if (request.ClientStateJson is { Length: > 0 } client)
            {
                try
                {
                    w.WriteRawValue(client, skipInputValidation: false);
                }
                catch (JsonException)
                {
                    w.WriteString("problem", "The client sent a state dump that was not valid JSON.");
                }
            }
            else
            {
                w.WriteNullValue();
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string SettingsJson(IReadOnlyList<StoredSetting> settings)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            WriteSettings(w, settings);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteSettings(Utf8JsonWriter w, IReadOnlyList<StoredSetting> settings)
    {
        w.WriteStartObject();
        foreach (StoredSetting setting in settings)
        {
            w.WriteStartObject(setting.Key);
            w.WriteString("updatedUtc", setting.UpdatedUtc);
            w.WritePropertyName("value");

            // A document this build cannot read is the interesting one, so an unparseable value
            // goes in as a string rather than taking the report down with it.
            try
            {
                w.WriteRawValue(setting.Value, skipInputValidation: false);
            }
            catch (JsonException)
            {
                w.WriteStringValue(setting.Value);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    /// <summary>
    /// The log files, newest first, <b>opened with <see cref="FileShare.ReadWrite"/></b> — today's
    /// is the one that matters most and Serilog still has it open, so anything less throws
    /// <c>IOException: being used by another process</c> on exactly the file the report exists for.
    /// </summary>
    private static void CopyLogs(
        ZipArchive zip, string directory, int days, List<string> included, List<string> warnings)
    {
        if (days <= 0)
            return;

        DateTime oldest = DateTime.Today.AddDays(-(days - 1));
        var copied = new List<string>();

        foreach (IssueReportFile file in ListFiles(directory, "tonebench-*.log"))
        {
            string full = Path.Combine(directory, file.Name);
            if (File.GetLastWriteTime(full).Date < oldest)
                continue;

            if (TryCopy(zip, full, $"logs/{file.Name}", warnings))
                copied.Add(file.Name);
        }

        included.Add(copied.Count == 0 ? "no log files found" : $"{copied.Count} log file(s): {string.Join(", ", copied)}");
    }

    private static void CopyFolder(
        ZipArchive zip, string directory, string prefix, string pattern,
        List<string> included, List<string> warnings)
    {
        var files = ListFiles(directory, pattern);
        int copied = 0;
        long bytes = 0;

        foreach (IssueReportFile file in files)
        {
            if (TryCopy(zip, Path.Combine(directory, file.Name), $"{prefix}/{file.Name}", warnings))
            {
                copied++;
                bytes += file.Bytes;
            }
        }

        included.Add(copied == 0 ? $"no files in {prefix}" : $"{copied} file(s) in {prefix} ({Describe(bytes)})");
    }

    /// <summary>
    /// The newest recording set only — one acquisition's input WAV, output WAV and sidecar.
    ///
    /// <para>The folder keeps the newest ten sets and each file may be 256 MB, so "the recordings"
    /// is up to five gigabytes. The newest set is the one that was being written when the fault was
    /// on screen, which is the whole reason to send one; a report that needs an older one is a
    /// request the maintainer can make, with the file sitting in a folder the dialog names.</para>
    /// </summary>
    private static void CopyNewestRecordingSet(
        ZipArchive zip, string directory, List<string> included, List<string> warnings)
    {
        var files = ListFiles(directory, "*");
        if (files.Count == 0)
        {
            included.Add("no recordings found — the stream recorder writes one set per acquisition, and only while it is armed");
            return;
        }

        // A set shares its stem up to the "-in"/"-out" suffix, so grouping by the file name with
        // those removed is what keeps a pair (and its sidecar) together.
        string newestStem = Stem(files[0].Name);
        long bytes = 0;
        int copied = 0;

        foreach (IssueReportFile file in files.Where(f => Stem(f.Name) == newestStem))
        {
            if (TryCopy(zip, Path.Combine(directory, file.Name), $"recordings/{file.Name}", warnings))
            {
                copied++;
                bytes += file.Bytes;
            }
        }

        included.Add($"{copied} recording file(s) for {newestStem} ({Describe(bytes)})");

        static string Stem(string name)
        {
            string bare = Path.GetFileNameWithoutExtension(name);
            foreach (string suffix in (string[])["-in", "-out"])
            {
                if (bare.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return bare[..^suffix.Length];
            }

            return bare;
        }
    }

    private static bool TryCopy(ZipArchive zip, string path, string entryName, List<string> warnings)
    {
        try
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using Stream target = entry.Open();
            source.CopyTo(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{Path.GetFileName(path)} could not be read ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Newest first, and never throwing: a folder that does not exist yet is simply empty.</summary>
    private static IReadOnlyList<IssueReportFile> ListFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory))
                return [];

            return
            [
                .. new DirectoryInfo(directory).GetFiles(pattern)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => new IssueReportFile(f.Name, f.Length, f.LastWriteTimeUtc.ToString("u")))
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// All but the newest few reports, deleted at each build. Same reasoning as the recorder's purge
    /// of all but ten WAV sets: these are copies of files that still exist, a user presses this
    /// button more than once while chasing one fault, and a folder of them is the largest thing this
    /// app writes without being asked twice.
    /// </summary>
    private void PurgeOldReports()
    {
        foreach (IssueReportFile stale in ListFiles(Paths.Reports, "*.zip").Skip(KeepNewestReports - 1))
        {
            try
            {
                File.Delete(Path.Combine(Paths.Reports, stale.Name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete the old issue report {Name}.", stale.Name);
            }
        }
    }

    private static async Task WriteTextEntryAsync(ZipArchive zip, string name, string content, CancellationToken ct)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream target = entry.Open();
        await target.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    internal static string Describe(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} kB"
        : $"{bytes} bytes";
}
