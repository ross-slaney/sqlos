using System.Text;
using System.Text.Json.Nodes;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Probes;

/// <summary>Helpers for the library-probe scenarios.</summary>
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
    /// Records a call whose library API may fail with an exception the probe does not translate
    /// (a database error, not a documented rejection). TestServer surfaces such a failure to the
    /// caller instead of a response, as a server would answer it with a bare 500; the transcript
    /// records the exception type, so a later change to a real response is still visible.
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
