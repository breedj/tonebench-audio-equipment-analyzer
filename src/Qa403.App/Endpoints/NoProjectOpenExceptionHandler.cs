using Qa403.Data;

namespace Qa403.App.Endpoints;

/// <summary>
/// Turns <see cref="NoProjectOpenException"/> into a 409 carrying its message, for every endpoint at
/// once. It can surface from any repository call — a save, a history list, a measurement-type apply —
/// and a try/catch in each of those handlers would be fifteen copies of one line. The client's
/// <c>request()</c> appends a failed response's body to the error it throws, so the message reaches the
/// status bar as written.
///
/// <para>A plain middleware rather than an <c>IExceptionHandler</c>: the exception-handler middleware
/// takes over every unhandled exception, and one its handler declines (a malformed request body, say,
/// which used to be a 400) is re-run down an empty path and comes back as a 404 it then refuses to
/// send — a 500 about the exception handler in place of the original error. This catches the one
/// type it is about and lets everything else pass exactly as before.</para>
/// </summary>
public static class NoProjectOpenExceptionHandler
{
    public static void UseNoProjectOpenHandler(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (NoProjectOpenException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(ex.Message, context.RequestAborted);
            }
        });
    }
}
