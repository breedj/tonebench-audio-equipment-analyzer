using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Qa403.App.Endpoints;
using Qa403.App.Services;
using Qa403.App.Streaming;
using Qa403.Device;
using Qa403.Device.Simulation;
using Serilog;

var noBrowser = args.Contains("--no-browser");

// Real hardware transport lands in phase 6; --simulate is wired now so that swap is a
// one-line change to the branch below instead of a rewrite of this startup path.
var simulate = !args.Contains("--no-simulate");

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

    if (simulate)
        builder.Services.AddSingleton<IAnalyzerDevice, SimulatedDevice>();
    else
        builder.Services.AddSingleton<IAnalyzerDevice, SimulatedDevice>(); // TODO(phase 6): swap for the real Qa40xUsbDevice

    // WindowType/AveragingMode read back as "Hann"/"Linear" etc, matching what /spectrum-config
    // already accepts on the way in — one representation, not int-in/string-out.
    builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddSingleton<SpectrumBroadcastHub>();
    builder.Services.AddSingleton<SpectrumPipeline>();
    builder.Services.AddSingleton<DeviceSessionService>();
    builder.Services.AddHostedService<DeviceLifecycleHostedService>();

    var app = builder.Build();

    app.UseWebSockets();

    var wwwroot = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = wwwroot });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = wwwroot });

    app.MapDeviceEndpoints();
    app.MapGeneratorEndpoints();
    app.MapAcquisitionEndpoints();
    app.MapStreamingEndpoints();

    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = wwwroot });

    var url = $"http://127.0.0.1:{port}";
    Log.Information("QA403 Audio Analyzer listening on {Url} (simulate={Simulate})", url, simulate);

    if (!noBrowser)
        LaunchBrowser(url);

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
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
