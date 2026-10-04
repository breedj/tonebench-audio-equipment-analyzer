using Microsoft.Extensions.Logging;

namespace Qa403.Device.Tests.SoundCard;

/// <summary>
/// Keeps every formatted log message, so a diagnostic line can be asserted the way behaviour is.
///
/// <para>Worth having for exactly one kind of line: the ones whose whole purpose is to be read by a
/// person afterwards. A message template with the wrong number of placeholders does not throw —
/// <c>Microsoft.Extensions.Logging</c> renders the placeholder name literally or drops the spare
/// argument — so a stall report can be silently unreadable, and the next stall is the one that finds
/// out. The sound-card pump's segment split is the case: it exists to explain a fault that happens
/// about once in four hours of streaming.</para>
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        string message = formatter(state, exception);
        lock (_gate)
        {
            _messages.Add(message);
        }
    }

    /// <summary>The first message containing <paramref name="fragment"/>, or a failure naming what was logged instead.</summary>
    public string Single(string fragment)
    {
        IReadOnlyList<string> messages = Messages;
        foreach (string message in messages)
        {
            if (message.Contains(fragment, StringComparison.Ordinal))
                return message;
        }

        throw new Xunit.Sdk.XunitException(
            $"no logged message contained \"{fragment}\". Logged:{Environment.NewLine}  "
                + string.Join(Environment.NewLine + "  ", messages));
    }
}
