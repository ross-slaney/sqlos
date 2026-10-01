using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace SqlOS.BehaviorLock.Host.Diagnostics;

/// <summary>
/// Answers an exception that escapes the application the way Kestrel does when the host adds no
/// exception handling: the response is reset (status, headers, and cookies) to an empty
/// <c>500</c>. TestServer instead rethrows the exception into the calling test, which would end
/// the scenario instead of recording what a client receives. The harness-only
/// <see cref="BehaviorLockHost.UnhandledExceptionHeader"/> response header names the escaped
/// exception, so a transcript shows both the external result and the failure a host would log.
/// <para>
/// Installed by <see cref="BehaviorLockHostOptions.AnswerUnhandledExceptionsAsServerErrors"/> as the
/// last startup filter, so it is the innermost one: it wraps the application pipeline (routing,
/// authentication, and the SqlOS and host endpoints) inside SqlOS's own dashboard middleware. An
/// exception the dashboard middleware throws itself still reaches the caller.
/// </para>
/// </summary>
internal sealed class UnhandledExceptionStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            try
            {
                await nextMiddleware(context);
            }
            catch (Exception exception) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
            {
                // Kestrel's answer to an exception that escapes before the response starts.
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentLength = 0;
                context.Response.Headers[BehaviorLockHost.UnhandledExceptionHeader] = Describe(exception);
            }
        });
        next(app);
    };

    /// <summary>The exception's type and message, as printable ASCII so it is a valid header value.</summary>
    private static string Describe(Exception exception)
    {
        var text = $"{exception.GetType().FullName}: {exception.Message}";
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(character is >= ' ' and <= '~' ? character : ' ');
        }

        return builder.ToString();
    }
}
