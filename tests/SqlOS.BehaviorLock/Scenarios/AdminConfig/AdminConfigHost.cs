using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// Starts admin-configuration scenarios. Many admin configuration routes let a service
/// <see cref="InvalidOperationException"/> escape the endpoint: the application pipeline has no
/// exception handler, so a production server answers <c>500</c> with an empty body and logs the
/// exception. Every host started here answers that way too
/// (<see cref="ScenarioOptions.AnswerUnhandledExceptionsAsServerErrors"/>), and names the escaped
/// exception in the harness-only <c>X-BehaviorLock-Unhandled-Exception</c> response header, so the
/// transcript shows both the external result and the failure a host would log.
/// </summary>
internal static class AdminConfigHost
{
    public static Task<Transcript> StartAsync(string profile, Action<ScenarioOptions>? configure = null)
        => Transcript.StartAsync(profile, options =>
        {
            options.AnswerUnhandledExceptionsAsServerErrors = true;
            configure?.Invoke(options);
        });
}
