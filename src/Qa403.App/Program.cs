using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Qa403.App.Diagnostics;
using Qa403.App.Endpoints;
using Qa403.App.Json;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Data;
using Qa403.Device;
using Qa403.Device.Hardware;
using Qa403.Device.Simulation;
using Serilog;

var noBrowser = args.Contains("--no-browser");

// Default is real QA403 hardware; pass --simulate to fall back to the synthetic device.
var simulate = args.Contains("--simulate");

string poolFloorNote;

// The QA403's RX transfer completions are delivered through the thread pool, and the USB transfer
// pump blocks on them: a completion that arrives late is a pump iteration that submits no TX, which
// drains the DAC's queue (Qa40xUsbDevice.TargetInFlightSeconds) and puts a flat span in the
// generator output. That span is a real
// dropout — measured at 0.3-3.6 ms in captured waveforms, enough to lift a 341 ms FFT record's
// broadband floor by ~70 dB and show up as one bright row in the waterfall.
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

var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDirectory, "qa403-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    var port = FindAvailablePort(8399, 8409);
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

    // --simulate picks which analyzer the app *starts* on; it is no longer which one it is stuck
    // with. The device is built by AnalyzerDeviceFactory and owned by DeviceSessionService, which
    // can replace it at runtime — see POST /api/device/kind. Registering IAnalyzerDevice as a
    // singleton here would give the container a handle it cannot replace and must not dispose out
    // from under a swap.
    builder.Services.AddSingleton(sp => new AnalyzerDeviceFactory(
        sp.GetRequiredService<ILoggerFactory>(),
        simulate ? DeviceKind.Simulated : DeviceKind.Hardware));

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

    builder.Services.AddSingleton<DiagnosticRecorder>();
    builder.Services.AddSingleton<SpectrumBroadcastHub>();
    builder.Services.AddSingleton<SpectrumPipeline>();
    builder.Services.AddSingleton<DeviceSessionService>();
    builder.Services.AddSingleton<ThdSweepRunner>();
    builder.Services.AddSingleton<FrequencyResponseRunner>();
    builder.Services.AddSingleton<SteppedResponseRunner>();
    builder.Services.AddSingleton<ILoopbackReferenceStore, SqliteLoopbackReferenceStore>();
    builder.Services.AddSingleton<ImpulseResponseRunner>();
    builder.Services.AddSingleton<ImpedanceRunner>();
    builder.Services.AddHostedService<DeviceLifecycleHostedService>();

    // Update-check (docs/qa403-app-stack-spec.md §8): a short timeout and silent-on-failure
    // UpdateCheckService are what keep an offline bench machine from ever seeing an error over
    // this — see its own remarks.
    builder.Services.AddHttpClient("GitHubReleases", client =>
    {
        client.BaseAddress = new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qa403-audio-analyzer");
        client.Timeout = TimeSpan.FromSeconds(5);
    });
    builder.Services.AddSingleton<UpdateCheckService>();

    // First use of configuration binding in this app: appsettings.json's "Database:Path" key
    // overrides the LocalApplicationData default — see Qa403DatabaseOptions.Default's remarks on
    // why measurement history lives there rather than next to the binary.
    var databasePath = builder.Configuration["Database:Path"];
    var databaseOptions = string.IsNullOrWhiteSpace(databasePath)
        ? Qa403DatabaseOptions.Default
        : new Qa403DatabaseOptions { Path = databasePath };
    builder.Services.AddSingleton(databaseOptions);
    builder.Services.AddSingleton<SqliteConnectionFactory>();

    // The configuration lives in its own database file, overridable with the "Config:Path" key the
    // same way. Separate from the measurement history on purpose — see Qa403ConfigDatabaseOptions:
    // the impedance fixture calibration describes this machine's QA403 and this bench's leads, and
    // it must not travel inside a measurement database copied to another machine.
    var configPath = builder.Configuration["Config:Path"];
    var configOptions = string.IsNullOrWhiteSpace(configPath)
        ? Qa403ConfigDatabaseOptions.Default
        : new Qa403ConfigDatabaseOptions { Path = configPath };
    builder.Services.AddSingleton(configOptions);
    builder.Services.AddSingleton<ConfigConnectionFactory>();
    builder.Services.AddSingleton<ConfigRepository>();
    builder.Services.AddSingleton<ProjectRepository>();
    builder.Services.AddSingleton<MeasurementRepository>();
    builder.Services.AddSingleton<MeasurementSaveService>();

    var app = builder.Build();

    // Runs once at startup, before the host serves any request — see MigrationRunner's own
    // remarks on why it's idempotent and why the .sql files are embedded rather than loose.
    {
        using var scope = app.Services.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
        await using var migrationConnection = await connectionFactory.OpenAsync(CancellationToken.None);
        await new MigrationRunner().RunAsync(migrationConnection, CancellationToken.None);

        // The configuration database has its own migration set and its own schema_version inside
        // its own file, so the two version histories advance independently.
        var configFactory = scope.ServiceProvider.GetRequiredService<ConfigConnectionFactory>();
        await using var configConnection = await configFactory.OpenAsync(CancellationToken.None);
        await new MigrationRunner(MigrationRunner.ConfigResourcePrefix)
            .RunAsync(configConnection, CancellationToken.None);
    }

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

    app.MapDeviceEndpoints();
    app.MapGeneratorEndpoints();
    app.MapAcquisitionEndpoints();
    app.MapDiagnosticsEndpoints();
    app.MapSweepEndpoints();
    app.MapFrequencyResponseEndpoints();
    app.MapImpulseResponseEndpoints();
    app.MapSteppedResponseEndpoints();
    app.MapImpedanceEndpoints();
    app.MapConfigEndpoints();
    app.MapStreamingEndpoints();
    app.MapProjectsEndpoints();
    app.MapMeasurementsEndpoints();
    app.MapUpdateCheckEndpoints();

    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = wwwroot, OnPrepareResponse = CacheHeaders });

    var url = $"http://127.0.0.1:{port}";
    Log.Information("QA403 Audio Analyzer listening on {Url} (simulate={Simulate})", url, simulate);

    // Logged, because "the fix is in the binary" and "the fix took effect" are different claims and
    // a pump stall looks the same either way.
    Log.Information("QA403 {PoolFloorNote}", poolFloorNote);

    if (!noBrowser)
        LaunchBrowser(url);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var shutdownComplete = new ManualResetEventSlim(false);

    StartShutdownWatchdog(lifetime);
    using var consoleClose = RegisterConsoleCloseShutdown(lifetime, shutdownComplete);

    app.Run();
    shutdownComplete.Set();
    Log.Information("Host stopped cleanly; process exiting");
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
static IDisposable? RegisterConsoleCloseShutdown(IHostApplicationLifetime lifetime, ManualResetEventSlim shutdownComplete)
{
    if (!OperatingSystem.IsWindows())
        return null;

    try
    {
        return PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            Log.Information("Console window close requested — running graceful shutdown");

            lifetime.StopApplication();

            if (!shutdownComplete.Wait(TimeSpan.FromSeconds(4.5)))
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

    // Everything else is content-addressed by Vite's hash, so a changed file is a changed URL.
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
