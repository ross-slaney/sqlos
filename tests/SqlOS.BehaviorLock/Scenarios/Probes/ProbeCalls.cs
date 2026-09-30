using System.Text;
using System.Text.Json.Nodes;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>Helpers for the library-probe and dashboard scenarios.</summary>
internal static class ProbeCalls
{
    /// <summary>
    /// Returns <paramref name="jwt"/> with one payload claim replaced and the original header and
    /// signature kept: what an attacker who edits a token they hold would present.
    /// </summary>
    public static string WithForgedClaim(string jwt, string claim, string value)
    {
        var parts = jwt.Split('.');
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])))!.AsObject();
        payload[claim] = value;
        parts[1] = Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return string.Join('.', parts);
    }

    /// <summary>
    /// Records a request that may fail with an exception nothing translates into a response (a
    /// database error from a library API, a JSON parse error in the dashboard middleware).
    /// TestServer surfaces such a failure to the caller, where a server would answer a bare 500;
    /// the transcript records the exception type, so a later change to a real response is visible.
    /// </summary>
    public static async Task ObserveOrUnhandledAsync(Transcript transcript, Func<Task<HttpExchange>> send, string caption)
    {
        HttpExchange exchange;
        try
        {
            exchange = await send();
        }
        catch (Exception exception) when (exception is not Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException)
        {
            var failure = exception;
            while (failure.InnerException != null && failure is AggregateException or HttpRequestException)
            {
                failure = failure.InnerException;
            }

            transcript.Note($"{caption}: no response, the request fails with an unhandled {failure.GetType().Name}");
            return;
        }

        transcript.Observe(exchange, caption);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
