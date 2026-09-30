using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Starts admin-configuration scenarios. Many admin configuration routes let a service
/// <see cref="InvalidOperationException"/> escape the endpoint: the application pipeline has no
/// exception handler, so a production server answers <c>500</c> with an empty body and logs the
/// exception. TestServer instead rethrows the exception into the calling test, which would make
/// those branches impossible to record. <see cref="StartAsync"/> adds one innermost middleware that
/// does what the production server does (clear the response, answer <c>500</c>) and names the
/// escaped exception in the harness-only <see cref="UnhandledExceptionHeader"/> response header, so
/// the transcript shows both the external result and the failure a host would log.
/// </summary>
internal static class AdminConfigHost
{
    /// <summary>Harness-only response header that names an exception the application did not handle.</summary>
    public const string UnhandledExceptionHeader = "X-BehaviorLock-Unhandled-Exception";

    public static Task<Transcript> StartAsync(string profile, Action<ScenarioOptions>? configure = null)
        => Transcript.StartAsync(profile, options =>
        {
            configure?.Invoke(options);
            var scenarioServices = options.ConfigureServices;
            options.ConfigureServices = services =>
            {
                scenarioServices?.Invoke(services);
                // Registered last, so it is the innermost startup filter: it wraps the application
                // pipeline and the SqlOS endpoints, inside SqlOS's own dashboard middleware.
                services.AddSingleton<IStartupFilter, UnhandledExceptionAsServerErrorStartupFilter>();
            };
        });

    private sealed class UnhandledExceptionAsServerErrorStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                try
                {
                    await nextMiddleware(context);
                }
                catch (Exception exception) when (!context.Response.HasStarted && exception is not OperationCanceledException)
                {
                    // Kestrel's behavior for an exception that escapes the application before the
                    // response starts: discard headers and body, answer 500.
                    context.Response.Clear();
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.Headers[UnhandledExceptionHeader] = Describe(exception);
                }
            });
            next(app);
        };

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
}
