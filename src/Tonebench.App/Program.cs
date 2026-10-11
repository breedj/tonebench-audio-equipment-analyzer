using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Tonebench.App.Diagnostics;
using Tonebench.App.Endpoints;
using Tonebench.App.Json;
using Tonebench.App.Services;
using Tonebench.App.Streaming;
using Tonebench.Data;
using Tonebench.Device;
using Tonebench.Device.Hardware;
using Tonebench.Device.Simulation;
using Serilog;
using Serilog.Core;
using Serilog.Events;

var noBrowser = args.Contains("--no-browser");

// Default is real QA403 hardware; pass --simulate to fall back to the synthetic device.
var simulate = args.Contains("--simulate");

// --project <path> opens that project file at startup, ahead of the "reopen the last project"
// preference — see ProjectStartupHostedService.
var startupProject = StartupProjectArgument.Parse(args);

string poolFloorNote;

// The QA403's RX transfer completions are delivered through the thread pool, and the USB transfer
// pump blocks on them: a completion that arrives late is a pump iteration that submits no TX, which
// drains the DAC's queue (Qa40xUsbDevice.TargetInFlightSeconds) and puts a flat span in the
// generator output. That span is a real
// dropout — measured at 0.3-3.6 ms in captured waveforms, enough to lift a 341 ms FFT record's
// broadband floor by ~70 dB and show up as one bright row in the spectrogram.
//
// The pool's own starvation response is the problem: past its minimum it injects roughly one thread
// per second, so a burst of DSP/broadcast work parks completions behind a queue that takes seconds
// to clear. Measured on this machine (12 cores) during dropouts: "pool 14 threads / 47 queued", with
// pump read waits of 44-125 ms against a normal 2.7 ms chunk period. GC is *not* the cause — total
// GC pause moved under 1 ms across a 125 ms stall.
//
// Raising the floor means those threads already exist when a burst arrives, so a completion never
// waits on thread injection. Costs nothing while idle: unused pool threads are not created eagerly.
{
    ThreadPool.GetMinThreads(out int minWorker, out int minIo);
    int floor = Math.Max(Environment.ProcessorCount * 4, 32);

    // Return value checked, not discarded: SetMinThreads fails silently if the value exceeds the
    // pool maximum, and a floor that was never applied looks exactly like one that did not help.
    bool applied = ThreadPool.SetMinThreads(Math.Max(minWorker, floor), Math.Max(minIo, floor));
    ThreadPool.GetMinThreads(out int nowWorker, out int nowIo);
    poolFloorNote = applied
        ? $"thread pool floor {minWorker}/{minIo} -> {nowWorker}/{nowIo}"
        : $"thread pool floor NOT applied, still {nowWorker}/{nowIo}";
}

// Logs live in the per-user data folder next to the configuration database, never beside the exe:
// an installed binary's folder belongs to the installer (and is read-only under /usr/lib), and a
// rebuild must not take the logs with it. Deliberately not tied to the "Config:Path" override below —
// that names one file, and a log location that silently moved with it would be a surprise.
//
// The minimum level is a switch rather than a constant: Configuration ▸ General ▸ Detailed logging
// drops it to Debug at runtime (see DiagnosticLoggingSetting), and the stored setting is applied
// once the configuration database is up, below.
// Carried over from the QA403-era data folder, once, before anything opens the configuration
// database. Deferred to the log below rather than written to the console here, because Serilog is
// configured on the next line and a Console.WriteLine would be the one startup line not in the file.
string? configMigrationNote = TonebenchAppData.MigrateLegacyConfigDatabase();

var logDirectory = TonebenchAppData.LogsDirectory;
var logLevelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(logLevelSwitch)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDirectory, "tonebench-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // One instance at a time: an earlier one still running is asked to shut down (gracefully -- it
    // may be holding the QA403) and waited for, so this one takes 8399 and not the next free port.
    ReplaceRunningInstance(8399, 8409);

    var port = FindAvailablePort(8399, 8409);
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

    // A project report's request carries one PNG per stored result (see ReportsEndpoints). The
    // endpoint declares its own limit as metadata too, but the server is loopback-only, so raising
    // Kestrel's 30 MB default globally is the one that cannot depend on which middleware honours it.
    builder.WebHost.ConfigureKestrel(options =>
        options.Limits.MaxRequestBodySize = ReportsEndpoints.MaxReportRequestBytes);

    // --simulate picks which analyzer the app *starts* on; it is no longer which one it is stuck
    // with. The device is built by AnalyzerDeviceFactory and owned by DeviceSessionService, which
    // can replace it at runtime — see POST /api/device/kind. Registering IAnalyzerDevice as a
    // singleton here would give the container a handle it cannot replace and must not dispose out
    // from under a swap.
    //
    // Which analyzer it starts on is the one remembered from last time (AnalyzerKindSetting,
    // loaded from the configuration database below) unless --simulate says otherwise. Read here
    // rather than at registration because this lambda runs lazily, after that load -- the same
    // arrangement SoundCardSetting relies on, and the reason both loads sit in one block.
    builder.Services.AddSingleton(sp => new AnalyzerDeviceFactory(
        sp.GetRequiredService<ILoggerFactory>(),
        simulate ? DeviceKind.Simulated : sp.GetRequiredService<AnalyzerKindSetting>().Kind,
        sp.GetRequiredService<SoundCardSetting>(),
        sp.GetRequiredService<Experimental384kSetting>(),
        sp.GetRequiredService<AutoRangeStepDownDelaySetting>()));

    // WindowType/AveragingMode read back as "Hann"/"Linear" etc, matching what /spectrum-config
    // already accepts on the way in — one representation, not int-in/string-out. Enum-as-string
    // now comes from AppJsonContext's UseStringEnumConverter rather than a runtime
    // JsonStringEnumConverter, which needs reflection per enum type — see AppJsonContext's remarks
    // on why a trimmed publish has no reflection-based JSON fallback to fall back on.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

    // Down from the 30 s default, and sized to fit inside the ~5 s Windows allows a CTRL_CLOSE_EVENT
    // handler before it terminates the process regardless — see RegisterConsoleCloseShutdown. The
    // nesting is pump join 2 s < this 3 s < watchdog 3.5 s < console-close wait 4.5 s; raising any
    // inner value past an outer one puts libusb_close back in the position of running with transfers
    // still in flight.
    //
    // Note the stop order is the opposite of what it looks like: GenericWebHostService is registered
    // during builder.Build(), *after* AddHostedService<DeviceLifecycleHostedService>() below, and
    // hosted services stop in reverse registration order — so Kestrel stops first and the device
    // teardown only begins once it has drained. /ws/stream therefore has to observe
    // ApplicationStopping (see StreamingEndpoints) or an open browser tab spends this entire budget
    // before the QA403 is touched.
    builder.Host.ConfigureHostOptions(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));

    builder.Services.AddSingleton(new DiagnosticLoggingSetting(logLevelSwitch));
    builder.Services.AddSingleton<DiagnosticRecorder>();
    // A factory rather than AddSingleton<StreamRecorder>(): its second constructor parameter is a
    // directory with a default, which is a test seam rather than something to ask the container for.
    builder.Services.AddSingleton(sp => new StreamRecorder(sp.GetRequiredService<ILogger<StreamRecorder>>()));
    builder.Services.AddSingleton<SpectrumBroadcastHub>();
    builder.Services.AddSingleton<SpectrumPipeline>();
    builder.Services.AddSingleton<CaptureDelaySetting>();
    builder.Services.AddSingleton<Experimental384kSetting>();
    builder.Services.AddSingleton<AutoRangeStepDownDelaySetting>();
    builder.Services.AddSingleton<ExternalGainSetting>();
    builder.Services.AddSingleton<SoundCardSetting>();
    builder.Services.AddSingleton<AnalyzerKindSetting>();
    builder.Services.AddSingleton<SoundCardCalibrationRunner>();
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<SoundCardCalibrationRunner>());
    // Report an issue. A singleton like every other service here, although it holds no state: it
    // asks nine of them for theirs, which is the whole job.
    builder.Services.AddSingleton<IssueReportService>();
    builder.Services.AddSingleton<UserWeightingSetting>();
    builder.Services.AddSingleton<UserWeightingService>();
    builder.Services.AddSingleton<DeviceSessionService>();
    builder.Services.AddSingleton<ThdSweepRunner>();
    builder.Services.AddSingleton<ImdSweepRunner>();
    builder.Services.AddSingleton<FrequencyResponseRunner>();
    builder.Services.AddSingleton<SteppedResponseRunner>();
    builder.Services.AddSingleton<CrosstalkRunner>();
    builder.Services.AddSingleton<OutputImpedanceRunner>();
    builder.Services.AddSingleton<ILoopbackReferenceStore, SqliteLoopbackReferenceStore>();
    builder.Services.AddSingleton<ImpulseResponseRunner>();
    builder.Services.AddSingleton<ImpedanceRunner>();
    // Before the device service on purpose: hosted services stop in reverse order, so the database
    // is checkpointed and closed *after* Kestrel has drained and the device is down — the one moment
    // nothing can still be writing. See DatabaseLifecycleHostedService for why the process otherwise
    // never closes its database at all.
    builder.Services.AddSingleton<DatabaseHealth>();
    builder.Services.AddHostedService<DatabaseLifecycleHostedService>();
    // Between the two on purpose: the configuration database is ready, the device (slower, and
    // unrelated) has not started, and it stops before the database service checkpoints the file.
    builder.Services.AddHostedService<ProjectStartupHostedService>();
    builder.Services.AddHostedService<DeviceLifecycleHostedService>();

    // Update-check (docs/tonebench-stack-spec.md §8): a short timeout and silent-on-failure
    // UpdateCheckService are what keep an offline bench machine from ever seeing an error over
    // this — see its own remarks.
    builder.Services.AddHttpClient("GitHubReleases", client =>
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("tonebench");
        client.Timeout = TimeSpan.FromSeconds(5);
    });
    builder.Services.AddSingleton<UpdateCheckService>();

    // Projects are one SQLite file each, opened and switched at runtime by ProjectSessionService;
    // the holder starts empty and every repository reads it on each open. A request that needs a
    // project while none is open is a 409, not a 500 — see NoProjectOpenExceptionHandler.
    builder.Services.AddSingleton<CurrentProjectFile>();
    builder.Services.AddSingleton<SqliteConnectionFactory>();
    builder.Services.AddSingleton(startupProject);
    builder.Services.AddSingleton<ProjectSessionService>();
    builder.Services.AddSingleton<FileBrowserService>();

    // Everything that must be stopped before the project file may change — see IMeasurementRun.
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<DeviceSessionService>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<ThdSweepRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<ImdSweepRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<FrequencyResponseRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<SteppedResponseRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<CrosstalkRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<OutputImpedanceRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<ImpulseResponseRunner>());
    builder.Services.AddSingleton<IMeasurementRun>(sp => sp.GetRequiredService<ImpedanceRunner>());

    // The configuration lives in its own database file, overridable with the "Config:Path" key the
    // same way. Separate from the measurement history on purpose — see TonebenchConfigDatabaseOptions:
    // the impedance fixture calibration describes this machine's QA403 and this bench's leads, and
    // it must not travel inside a measurement database copied to another machine.
    var configPath = builder.Configuration["Config:Path"];
    var configOptions = string.IsNullOrWhiteSpace(configPath)
        ? TonebenchConfigDatabaseOptions.Default
        : new TonebenchConfigDatabaseOptions { Path = configPath };
    builder.Services.AddSingleton(configOptions);
    builder.Services.AddSingleton<ConfigConnectionFactory>();
    builder.Services.AddSingleton<ConfigRepository>();
    builder.Services.AddSingleton<LoopbackReferenceRepository>();
    builder.Services.AddSingleton<ProjectRepository>();
    builder.Services.AddSingleton<MeasurementRepository>();
    builder.Services.AddSingleton<TrashRepository>();
    builder.Services.AddSingleton<MeasurementSaveService>();
    builder.Services.AddSingleton<SimulationImportService>();

    var app = builder.Build();

    // Runs once at startup, before the host serves any request — see MigrationRunner's own
    // remarks on why it's idempotent and why the .sql files are embedded rather than loose. Only
    // the configuration database: a project file is migrated when it is opened.
    {
        using var scope = app.Services.CreateScope();
        var configFactory = scope.ServiceProvider.GetRequiredService<ConfigConnectionFactory>();
        await using var configConnection = await configFactory.OpenAsync(CancellationToken.None);
        await new MigrationRunner(MigrationRunner.ConfigResourcePrefix)
            .RunAsync(configConnection, CancellationToken.None);

        // Every in-memory setting, in the one order that works -- see StartupSettingsLoader, which
        // owns that order precisely because it is invisible: the first device is built the moment
        // anything resolves the session, from settings that have to be loaded before it.
        await StartupSettingsLoader.LoadAsync(scope.ServiceProvider, CancellationToken.None);

        // Detailed logging, as it was left: the reproduction a tester is asked for usually involves
        // a restart, and a switch that reset on every start would miss it. On by default now (see
        // DiagnosticLoggingConfig) -- applied both ways rather than only when on, since the stored
        // document is the only thing that can say "this bench turned it off".
        var diagnosticLogging = await ReadDiagnosticLoggingAsync(
            scope.ServiceProvider.GetRequiredService<ConfigRepository>(), CancellationToken.None);
        app.Services.GetRequiredService<DiagnosticLoggingSetting>().Enabled = diagnosticLogging.Enabled;

        // Unconditional, and the first thing in every log file: a file sent in by a user has no
        // other way of saying which build, OS or runtime wrote it, and every answer below it is
        // worthless without that. Cheap enough to pay for on a start that never reports a fault.
        //
        // OSDescription rather than OSVersion alone -- the version number alone cannot tell
        // Windows 11 from Windows 10 (both are 10.0.x) nor name a Linux distribution at all, and
        // this app ships on both. The memory figure is the GC's view of what it may use, which on
        // Windows is the physical RAM and under a container is the container's limit: the question
        // it answers is whether a 1M-point FFT and a 256 MB WAV had room, and the limit is the
        // number that decides that rather than the hardware's.
        var memory = GC.GetGCMemoryInfo();
        Log.Information(
            "Tonebench - Audio Equipment Analyzer {Version} starting: {Os} ({OsVersion}), {Arch}, "
                + ".NET {Runtime}, {Cores} cores, {MemoryGb:0.0} GB available to the runtime, "
                + "server GC {ServerGc}, detailed logging {DetailedLogging}",
            UpdateCheckService.CurrentVersion,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Environment.OSVersion.Version,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version,
            Environment.ProcessorCount,
            memory.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0),
            System.Runtime.GCSettings.IsServerGC ? "on" : "off",
            diagnosticLogging.Enabled ? "on" : "off");
    }

    // One line per API request with its status and timing -- but only while detailed logging is
    // on, and never for a *polled* GET that succeeded: at several lines a second the file would be
    // the poll and nothing else, which is the one thing a log sent in by a tester cannot afford.
    app.UseSerilogRequestLogging(options =>
    {
        var setting = app.Services.GetRequiredService<DiagnosticLoggingSetting>();
        options.GetLevel = (httpContext, _, exception) =>
            IsSuccessfulPoll(httpContext, exception)
                ? LogEventLevel.Verbose
                : setting.RequestLogLevel(httpContext.Response.StatusCode, exception);
    });

    // A request that needs a project while none is open is a 409 with a message, from any endpoint.
    app.UseNoProjectOpenHandler();
    app.UseWebSockets();

    var wwwroot = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = wwwroot });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = wwwroot, OnPrepareResponse = CacheHeaders });

    // Wired here rather than in either constructor: the recorder is created by the pipeline's
    // dependency graph and so cannot take the session (which depends on the pipeline), while the
    // session has a primary constructor with nowhere to put the assignment. One visible line at
    // composition time is clearer than either workaround.
    app.Services.GetRequiredService<DiagnosticRecorder>().ContextProvider =
        app.Services.GetRequiredService<DeviceSessionService>().CaptureDiagnosticContext;

    app.MapAppEndpoints();
    app.MapDeviceEndpoints();
    app.MapGeneratorEndpoints();
    app.MapAcquisitionEndpoints();
    app.MapDiagnosticsEndpoints();
    app.MapSweepEndpoints();
    app.MapImdSweepEndpoints();
    app.MapFrequencyResponseEndpoints();
    app.MapImpulseResponseEndpoints();
    app.MapSteppedResponseEndpoints();
    app.MapCrosstalkEndpoints();
    app.MapOutputImpedanceEndpoints();
    app.MapImpedanceEndpoints();
    app.MapConfigEndpoints();
    app.MapSoundCardEndpoints();
    app.MapStreamingEndpoints();
    app.MapProjectsEndpoints();
    app.MapProjectSessionEndpoints();
    app.MapMeasurementsEndpoints();
    app.MapSimulationEndpoints();
    app.MapTrashEndpoints();
    app.MapReportsEndpoints();
    app.MapUpdateCheckEndpoints();
    app.MapIssueReportEndpoints();

    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = wwwroot, OnPrepareResponse = CacheHeaders });

    var url = $"http://127.0.0.1:{port}";
    Log.Information("Tonebench - Audio Equipment Analyzer listening on {Url} (simulate={Simulate})", url, simulate);

    // The console is what the user is looking at when something goes wrong, so it says where to look.
    Log.Information("Tonebench data: logs {Logs}, diagnostics {Diagnostics}, configuration {Config}",
        TonebenchAppData.LogsDirectory, TonebenchAppData.DiagnosticsDirectory, configOptions.Path);

    // Logged, because "the fix is in the binary" and "the fix took effect" are different claims and
    // a pump stall looks the same either way.
    Log.Information("Tonebench {PoolFloorNote}", poolFloorNote);

    if (configMigrationNote is not null)
        Log.Information("Tonebench {ConfigMigrationNote}", configMigrationNote);

    if (!noBrowser)
        LaunchBrowser(url);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var shutdownComplete = new ManualResetEventSlim(false);

    StartShutdownWatchdog(lifetime);
    using var consoleClose = RegisterConsoleCloseShutdown(lifetime, shutdownComplete);

    app.Run();

    // Logged and flushed *before* the console-close handler is released: that handler returning is
    // what lets Windows terminate the process, and it does so immediately — for ten days this line
    // sat after the Set() and never once reached the log, which made every close look like a crash.
    Log.Information("Host stopped cleanly; process exiting");
    Log.CloseAndFlush();
    shutdownComplete.Set();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Runs a graceful shutdown when the console window's close button is used.
///
/// Without this, closing the window ran no shutdown at all — the log jumped straight from
/// "Application started" to nothing, with no "Application is shutting down...", no
/// <c>QA403 teardown:</c> lines, and no chance for the watchdog (armed on <c>ApplicationStopping</c>)
/// to arm. Ctrl+C was always fine, which is what made this look like an intermittent USB bug rather
/// than a missing signal handler.
///
/// The cause is a mapping gap. .NET's Windows <see cref="PosixSignalRegistration"/> installs a
/// console control handler and maps CTRL_C_EVENT to <see cref="PosixSignal.SIGINT"/>,
/// CTRL_BREAK_EVENT to <see cref="PosixSignal.SIGQUIT"/> and the window close button
/// (CTRL_CLOSE_EVENT) to <see cref="PosixSignal.SIGHUP"/> — but the generic host's
/// <c>ConsoleLifetime</c> only subscribes to SIGINT, SIGQUIT and SIGTERM. Nothing listens for
/// SIGHUP, so the default action runs and Windows terminates the process outright.
///
/// Terminating mid-transfer is what left the QA403 unreachable: the pump still had bulk reads
/// pending on EP2, and a process cannot finish dying while a thread sits in an uninterruptible
/// kernel-mode USB I/O — which is exactly why the stuck process only disappeared when the cable was
/// pulled, and why the device then needed a power cycle for any app to claim it.
///
/// <c>Cancel = true</c> suppresses that default termination. The handler must then *block*: returning
/// from a CTRL_CLOSE_EVENT handler is what tells Windows to proceed with the kill. Windows allows
/// roughly 5 s before it terminates regardless, so every timeout on this path is sized to fit inside
/// it — pump join 2 s &lt; ShutdownTimeout 3 s &lt; watchdog 3.5 s &lt; this wait 4.5 s.
///
/// On Linux the same signal is a real SIGHUP: the .deb's desktop entry runs the app in a terminal
/// (<c>Terminal=true</c>, because the console is the log and its close button the way to quit), and
/// closing that terminal delivers SIGHUP, whose default action is the same termination mid-transfer.
/// So the handler is registered on every platform; only the blocking wait is Windows-specific, since
/// nothing on Linux kills the process when the handler returns — the host simply finishes stopping.
static IDisposable? RegisterConsoleCloseShutdown(IHostApplicationLifetime lifetime, ManualResetEventSlim shutdownComplete)
{
    try
    {
        return PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            Log.Information("Console window close requested — running graceful shutdown");

            lifetime.StopApplication();

            if (OperatingSystem.IsWindows() && !shutdownComplete.Wait(TimeSpan.FromSeconds(4.5)))
                Log.Error("Shutdown did not finish before the console-close deadline; Windows will terminate the process");
        });
    }
    catch (PlatformNotSupportedException ex)
    {
        // Not fatal: Ctrl+C still shuts down cleanly, the window close button just goes back to
        // being an ungraceful kill.
        Log.Warning(ex, "Could not hook the console close button; closing the window will not shut down gracefully");
        return null;
    }
}

/// <summary>
/// Guarantees the process actually dies once shutdown starts, even if teardown blocks.
///
/// This exists because a QA403 left claimed by a process that never exits is indistinguishable, from
/// the outside, from a QA403 that needs a power cycle — and it is the far likelier of the two. The
/// hazard is documented in <c>Qa40xUsbDevice.StreamAsync</c>: LibUsbDotNet's <c>Close()</c> signals
/// its event thread to stop before closing the handle, and <c>libusb_close</c> then waits with no
/// timeout for an acknowledgement from a thread that may already be gone. That wait is native and
/// uninterruptible — no CancellationToken, no managed timeout, and no host ShutdownTimeout reaches
/// it. Whether it happens depends on transfer timing, which is exactly why the symptom is
/// intermittent ("most of the time"). The same applies to the GC.WaitForPendingFinalizers() in
/// Qa40xTransport.Dispose.
///
/// Killing ourselves is safe here and strictly better than hanging: StreamingStop has already been
/// written by this point (TransferPump's finally, and again in Qa40xUsbDevice.Dispose), so the device
/// is quiesced, and Windows reclaims the USB handle on process death — which is the one thing a
/// wedged process prevents. Serilog is flushed first so the diagnosis survives.
///
/// Deliberately Process.Kill and not Environment.Exit: Exit runs finalizers and ProcessExit handlers,
/// which can block on the very native call being escaped from.
static void StartShutdownWatchdog(IHostApplicationLifetime lifetime)
{
    // Above the host's own 3 s ShutdownTimeout so a clean shutdown always wins the race, and below
    // the console-close handler's 4.5 s wait so the watchdog — which logs *why* — is what fires
    // first when teardown blocks, rather than Windows silently terminating us.
    var deadline = TimeSpan.FromSeconds(3.5);

    lifetime.ApplicationStopping.Register(() =>
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(deadline);

            Log.Error(
                "Shutdown did not complete within {Seconds}s — forcing process exit. The last 'QA403 teardown:' line "
                    + "above is the step that blocked. The device has already been sent StreamingStop; killing the "
                    + "process is what releases the USB handle so the next connect can claim it.",
                deadline.TotalSeconds);
            Log.CloseAndFlush();

            Process.GetCurrentProcess().Kill();
        })
        {
            IsBackground = true,
            Name = "QA403 shutdown watchdog",
        };

        watchdog.Start();
    });
}

/// <summary>
/// The GETs a screen polls on a timer, which the request log drops to Verbose while they succeed.
///
/// <para>Two of them, and the second was found by reading a log file rather than by reasoning:
/// 1949 of the 2182 lines in one day's log — <b>89 %</b> — were
/// <c>GET /api/soundcard/levels</c>, the sound-card dialog's level bars at 120 ms. That is a log
/// in which nothing else can be found, and it is the log a tester is asked to send. The status
/// poll (<c>GET /api/device</c>, once a second) was already excluded for the same reason; the
/// difference is only that nobody had the dialog open for four minutes until now.</para>
///
/// <para>Only a <i>successful</i> GET. A poll that starts failing is the most interesting thing in
/// the file — it is how a lost device, a wedged runner or a 409 storm announces itself — so the
/// status code and any exception are part of the test, not just the path.</para>
/// </summary>
static bool IsSuccessfulPoll(HttpContext httpContext, Exception? exception)
{
    if (exception is not null || httpContext.Response.StatusCode >= 400 || httpContext.Request.Method != "GET")
        return false;

    PathString path = httpContext.Request.Path;
    return path == "/api/device" || path == "/api/soundcard/levels";
}

/// <summary>The stored detailed-logging switch, or off when nothing is stored or the document is unreadable.</summary>
static async Task<DiagnosticLoggingConfig> ReadDiagnosticLoggingAsync(ConfigRepository repo, CancellationToken ct)
{
    string? stored = await repo.GetAsync(ConfigRepository.DiagnosticLoggingKey, ct);
    if (string.IsNullOrWhiteSpace(stored))
        return DiagnosticLoggingConfig.Default;

    try
    {
        return System.Text.Json.JsonSerializer.Deserialize(stored, AppJsonContext.Default.DiagnosticLoggingConfig)
            ?? DiagnosticLoggingConfig.Default;
    }
    catch (System.Text.Json.JsonException)
    {
        return DiagnosticLoggingConfig.Default;
    }
}

/// <summary>
/// Shuts down any earlier instance of this app found listening on <paramref name="start"/> to
/// <paramref name="end"/>, and waits for it to exit, so the instance starting now can take the
/// first port.
///
/// <para>Two instances is never what the user meant. The browser tab, the Vite dev proxy
/// (<c>web/vite.config.ts</c>, hard-wired to 8399) and the launch scripts all assume one app on
/// one port; a second start used to take 8400 silently, and the visible result was a UI whose Run
/// button did nothing (the dev proxy still pointed at the old process) or a QA403 that "could not
/// be claimed" (the old process still held it). So the newcomer wins, which is also the only order
/// that can work: the old instance may be wedged, and the new one is the process the user is
/// looking at.</para>
///
/// <para>The old instance is asked over its own API (<c>POST /api/app/shutdown</c>) rather than
/// killed, because a kill is exactly the shutdown the hard rules forbid: a process terminated with
/// transfers in flight leaves the QA403 claimed until it is power-cycled. Its graceful shutdown is
/// bounded by its own watchdog (3.5 s, then it kills itself), so the wait here is longer than that
/// and a kill from this side is the fallback for a process that is past helping. A listener that
/// does not identify as this product is left alone and the port scan below steps past it, as it
/// always did.</para>
/// </summary>
static void ReplaceRunningInstance(int start, int end)
{
    // Only the ports something is actually listening on are probed. Asking the stack rather than
    // connecting to each one matters more than it looks: on this Windows machine a connect to a
    // closed loopback port takes two seconds to be refused (measured, 2.02 s for every closed port
    // between 8400 and 8409), and a start that first waits ten seconds on empty ports is a start
    // that reads as hung.
    int[] listening;
    try
    {
        listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(e => e.Port >= start && e.Port <= end)
            .Where(e => e.Address.Equals(IPAddress.Loopback) || e.Address.Equals(IPAddress.Any) || e.Address.Equals(IPAddress.IPv6Any))
            .Select(e => e.Port)
            .Distinct()
            .OrderBy(p => p)
            .ToArray();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not list listening ports; not looking for an earlier instance");
        return;
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

    foreach (var port in listening)
    {
        AppIdentity? other;
        try
        {
            var json = http.GetStringAsync($"http://127.0.0.1:{port}/api/app").GetAwaiter().GetResult();
            other = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.AppIdentity);
        }
        catch (Exception)
        {
            // Nothing listening, or something that is not us (a 404 from another server, a
            // connection refused, a body that is not our record): not ours to touch.
            continue;
        }

        if (other is null || other.Product != AppEndpoints.ProductId || other.Pid == Environment.ProcessId)
            continue;

        Log.Information("Another instance (pid {Pid}, version {Version}) is running on port {Port} — asking it to shut down",
            other.Pid, other.Version, port);

        try
        {
            using var response = http.PostAsync($"http://127.0.0.1:{port}/api/app/shutdown", content: null).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The shutdown request to pid {Pid} failed; falling back to waiting on the process", other.Pid);
        }

        Process? process = null;
        try { process = Process.GetProcessById(other.Pid); }
        catch (ArgumentException) { /* already gone */ }

        if (process is not null)
        {
            using (process)
            {
                // Its own budget is 3.5 s to the watchdog; give it that and a margin before deciding
                // it is stuck. A process that has not gone by then is holding the interface with
                // no way left of releasing it cleanly, and killing it is what releases the handle.
                if (!process.WaitForExit(TimeSpan.FromSeconds(8)))
                {
                    Log.Warning("Pid {Pid} did not exit within 8 s of the shutdown request — terminating it", other.Pid);
                    try
                    {
                        process.Kill();
                        process.WaitForExit(TimeSpan.FromSeconds(3));
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not terminate pid {Pid}; this instance will start on another port", other.Pid);
                        continue;
                    }
                }
            }
        }

        Log.Information("The earlier instance has exited; taking port {Port}", port);
    }
}

static int FindAvailablePort(int start, int end)
{
    for (var port = start; port <= end; port++)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return port;
        }
        catch (SocketException)
        {
        }
    }

    throw new InvalidOperationException($"No available port found in range {start}-{end}.");
}

/// <summary>
/// Cache headers for the embedded UI. Vite fingerprints every asset, so those may be cached
/// forever — but <c>index.html</c> is the file that names which fingerprints are current, and it
/// must never be served from cache without revalidating.
///
/// <para>It shipped with no <c>Cache-Control</c> at all, which leaves browsers free to apply
/// heuristic caching. A tab left open across a rebuild then kept an <c>index.html</c> naming chunks
/// that no longer exist, and the only part of the app that loads chunks lazily is the manual — so
/// every manual page came up <b>blank</b> while the rest of the app, already in memory, worked
/// perfectly. A blank page with no error is the worst possible symptom for a stale cache, which is
/// why the header is set rather than left to the browser's judgement.</para>
/// </summary>
static void CacheHeaders(StaticFileResponseContext context)
{
    var headers = context.Context.Response.GetTypedHeaders();

    if (context.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
    {
        // Revalidate every time. The ETag makes that a 304 in the ordinary case, so it costs a
        // round trip and no body.
        headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return;
    }

    // Vite's build output under assets/ is content-addressed by hash, so a changed file is a changed
    // URL and may be cached forever. Files copied verbatim from web/public (favicon.svg) keep their
    // name across builds, so they get the same revalidate-every-time treatment as index.html — the
    // icon was found still showing the old design a build later, from a tab that had it "immutable".
    var isHashedAsset = context.Context.Request.Path.StartsWithSegments("/assets");
    if (!isHashedAsset)
    {
        headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return;
    }

    headers.CacheControl = new CacheControlHeaderValue
    {
        Public = true,
        MaxAge = TimeSpan.FromDays(365),
        Extensions = { new NameValueHeaderValue("immutable") },
    };
}

static void LaunchBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Log.Warning("Unrecognized OS platform; skipping automatic browser launch for {Url}", url);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Failed to launch browser at {Url}", url);
    }
}

public partial class Program;
