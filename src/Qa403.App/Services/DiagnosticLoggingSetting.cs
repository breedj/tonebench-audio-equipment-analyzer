using Serilog.Core;
using Serilog.Events;

namespace Qa403.App.Services;

/// <summary>
/// The live copy of <see cref="Qa403.Data.DiagnosticLoggingConfig"/>: Configuration ▸ General ▸
/// "Detailed logging", which is what a tester is asked to switch on before reproducing a problem so
/// the log file they send in says what happened.
///
/// <para>Two things change when it is on, both through the one <see cref="LoggingLevelSwitch"/>
/// Serilog's minimum level is bound to at startup. The floor drops from Information to Debug, so
/// the Debug-level lines the device layer writes (register exchanges, ranging steps, pump
/// diagnostics) reach the file; and <c>Program</c>'s request-logging middleware, whose level is
/// <see cref="RequestLogLevel"/>, starts writing one line per API call with its status and timing —
/// at Verbose while off, which the Information floor discards, so the middleware costs nothing
/// until it is wanted. The ordinary log is always written; this only makes it say more.</para>
///
/// <para>A singleton the way <see cref="CaptureDelaySetting"/> is: the switch is created before the
/// host exists (the logger has to be), <c>Program</c> loads the stored value once the configuration
/// database is up, and the <c>/api/config/diagnostic-logging</c> endpoint writes both the document
/// and this.</para>
/// </summary>
public sealed class DiagnosticLoggingSetting
{
    private readonly LoggingLevelSwitch _levelSwitch;

    public DiagnosticLoggingSetting(LoggingLevelSwitch levelSwitch)
    {
        _levelSwitch = levelSwitch;
    }

    /// <summary>Whether the detailed record is being written. Reads the switch itself, so there is one source of truth.</summary>
    public bool Enabled
    {
        get => _levelSwitch.MinimumLevel <= LogEventLevel.Debug;
        set => _levelSwitch.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
    }

    /// <summary>
    /// The level the request-logging middleware writes at. Information reaches the file only while
    /// the floor is at Debug, so this is the same switch read the other way round. A request that
    /// threw, or answered 500, is always worth a line, whatever the setting; a 503 is a refusal
    /// with its own log line (Connect with no device) and is not an error of the app's.
    /// </summary>
    public LogEventLevel RequestLogLevel(int statusCode, Exception? exception) =>
        exception is not null || statusCode == 500 ? LogEventLevel.Error
        : Enabled ? LogEventLevel.Information
        : LogEventLevel.Verbose;
}

/// <summary>
/// What <c>GET /api/config/diagnostic-logging</c> returns: the switch, and where the file the
/// tester is going to be asked for lives — shown beside the checkbox, since "send me the log" is
/// only a useful request when the person knows where to look.
/// </summary>
public sealed record DiagnosticLoggingResponse(bool Enabled, string LogsDirectory);
