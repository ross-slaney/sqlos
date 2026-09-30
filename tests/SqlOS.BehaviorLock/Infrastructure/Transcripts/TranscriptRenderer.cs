using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>
/// Renders transcript entries into the approved text format, then scrubs it in one pass:
/// <code>
/// ## 2. browser: submit the password
/// > POST /sqlos/auth/login/password
/// > Content-Type: application/x-www-form-urlencoded
/// > Origin: https://sqlos.example.test
/// >   email={email:alice}
/// &lt; 200 OK
/// &lt; Cache-Control: no-store
///     &lt;html ...
///   ~ email (auth) to {email:alice}
/// </code>
/// Request lines start with <c>&gt;</c>, response headers with <c>&lt;</c>, bodies are indented
/// four spaces, and decoded JWTs and outbound effects (<c>~</c>) follow the exchange that caused
/// them. Response headers are sorted by name; volatile transport headers are left out.
/// </summary>
internal static partial class TranscriptRenderer
{
    /// <summary>Response headers that vary per run or per transport and carry no SqlOS behavior.</summary>
    private static readonly HashSet<string> VolatileResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "Server", "Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive",
        "ETag", "Last-Modified", "Request-Context", "traceparent", "tracestate"
    };

    public static string Render(ScenarioContext scenario, string profile, IReadOnlyList<TranscriptEntry> entries, Scrubber scrubber)
    {
        var sink = new TranscriptValueSink(scrubber);
        var builder = new StringBuilder();
        builder.Append("# SqlOS behavior lock transcript\n");
        builder.Append("scenario: ").Append(scenario.Name).Append('\n');
        builder.Append("profile: ").Append(profile).Append('\n');
        var exchangeNumber = 0;
        foreach (var entry in entries)
        {
            builder.Append('\n');
            switch (entry.Kind)
            {
                case "exchange":
                    RenderExchange(builder, ++exchangeNumber, entry.Caption, entry.Exchange!, sink);
                    break;
                case "audit":
                    RenderAudit(builder, entry.Caption, entry.AuditEvents!, sink);
                    break;
                case "effects":
                    builder.Append("## effects: ").Append(entry.Caption).Append('\n');
                    RenderEffects(builder, entry.Effects!, sink);
                    if (entry.Effects!.Count == 0)
                    {
                        builder.Append("  (none)\n");
                    }

                    break;
                case "note":
                    builder.Append("# ").Append(entry.Note).Append('\n');
                    break;
                case "document":
                    builder.Append("## document: ").Append(entry.Caption).Append('\n');
                    foreach (var line in entry.Note!.Split('\n'))
                    {
                        builder.Append("    ").Append(line).Append('\n');
                    }

                    break;
            }
        }

        return scrubber.Scrub(builder.ToString()).TrimEnd('\n') + "\n";
    }

    private static void RenderExchange(StringBuilder builder, int number, string? caption, HttpExchange exchange, TranscriptValueSink sink)
    {
        builder.Append("## ").Append(number).Append(". ").Append(exchange.Actor);
        if (!string.IsNullOrWhiteSpace(caption))
        {
            builder.Append(": ").Append(caption);
        }

        builder.Append('\n');
        RenderRequest(builder, exchange, sink);
        RenderResponse(builder, exchange, sink);
        foreach (var jwt in sink.DrainJwts())
        {
            builder.Append(JwtRendering.Render(jwt, sink, "  "));
        }

        RenderEffects(builder, exchange.Effects, sink);
        foreach (var jwt in sink.DrainJwts())
        {
            builder.Append(JwtRendering.Render(jwt, sink, "  "));
        }
    }

    private static void RenderRequest(StringBuilder builder, HttpExchange exchange, TranscriptValueSink sink)
    {
        sink.Inspect(exchange.Target);
        builder.Append("> ").Append(exchange.Method).Append(' ').Append(exchange.Target).Append('\n');
        var headers = exchange.RequestHeaders.ToList();
        if (exchange.RequestContentType != null)
        {
            headers.Add(new KeyValuePair<string, string>("Content-Type", exchange.RequestContentType));
        }

        foreach (var (name, value) in headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split(' ', 2);
                if (parts.Length == 2)
                {
                    sink.Inspect(parts[1]);
                    sink.Register(parts[1], parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) ? "access-token" : "credentials");
                }
            }
            else if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);
                    if (separator > 0)
                    {
                        sink.Register(pair[(separator + 1)..], "cookie");
                    }
                }
            }

            builder.Append("> ").Append(name).Append(": ").Append(value).Append('\n');
        }

        if (exchange.RequestBody is { Length: > 0 } body)
        {
            foreach (var line in RenderBody(exchange.RequestContentType, body, sink, isRequest: true))
            {
                builder.Append(">   ").Append(line).Append('\n');
            }
        }
    }

    private static void RenderResponse(StringBuilder builder, HttpExchange exchange, TranscriptValueSink sink)
    {
        builder.Append("< ").Append(exchange.StatusCode).Append(' ')
            .Append(ReasonPhrases.GetReasonPhrase(exchange.StatusCode)).Append('\n');
        foreach (var (name, value) in exchange.ResponseHeaders
                     .Where(header => !VolatileResponseHeaders.Contains(header.Key))
                     .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("Location", StringComparison.OrdinalIgnoreCase))
            {
                sink.Inspect(value);
            }
            else if (name.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match nonce in CspNonce().Matches(value))
                {
                    sink.Register(nonce.Groups["nonce"].Value, "csp-nonce");
                }
            }

            var shown = name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) && long.TryParse(value, out var seconds)
                ? CanonicalJson.ApproximateSeconds(seconds)
                : value;
            builder.Append("< ").Append(name).Append(": ").Append(shown).Append('\n');
        }

        foreach (var setCookie in exchange.SetCookies)
        {
            builder.Append("< Set-Cookie: ")
                .Append(SetCookieRendering.Render(setCookie, exchange.Started, exchange.Completed, sink))
                .Append('\n');
        }

        if (exchange.ResponseBody.Length > 0)
        {
            foreach (var line in RenderBody(exchange.ResponseContentType, exchange.ResponseBody, sink, isRequest: false))
            {
                builder.Append("    ").Append(line).Append('\n');
            }
        }
    }

    internal static IEnumerable<string> RenderBody(string? contentType, string body, TranscriptValueSink sink, bool isRequest)
    {
        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        string rendered;
        if (mediaType == "application/x-www-form-urlencoded")
        {
            rendered = RenderForm(body, sink);
        }
        else if (mediaType.EndsWith("json", StringComparison.Ordinal) || LooksLikeJson(body))
        {
            rendered = CanonicalJson.TryRender(body, sink) ?? body;
        }
        else if (mediaType == "text/html" || (!isRequest && body.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)))
        {
            rendered = HtmlCanonicalizer.Render(body, sink);
        }
        else if (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType.EndsWith("xml", StringComparison.Ordinal) || mediaType.Length == 0)
        {
            foreach (Match url in Url().Matches(body))
            {
                sink.Inspect(url.Value);
            }

            rendered = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        }
        else
        {
            rendered = $"{{binary:{mediaType}}}";
        }

        return rendered.Split('\n');
    }

    private static string RenderForm(string body, TranscriptValueSink sink)
    {
        var lines = new List<string>();
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Decode(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]);
            sink.RegisterRole(key, value);
            sink.Inspect(value);
            lines.Add($"{key}={value}");
        }

        return string.Join('\n', lines);
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static bool LooksLikeJson(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static void RenderAudit(StringBuilder builder, string? caption, IReadOnlyList<JsonNode> events, TranscriptValueSink sink)
    {
        builder.Append("## audit");
        if (!string.IsNullOrWhiteSpace(caption))
        {
            builder.Append(": ").Append(caption);
        }

        builder.Append('\n');
        if (events.Count == 0)
        {
            builder.Append("  (no new events)\n");
            return;
        }

        foreach (var item in events)
        {
            using var document = JsonDocument.Parse(item.ToJsonString());
            var rendered = CanonicalJson.Render(document.RootElement, sink);
            builder.Append("  - ").Append(rendered.Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
        }
    }

    private static void RenderEffects(StringBuilder builder, IReadOnlyList<OutboundEffect> effects, TranscriptValueSink sink)
    {
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case EmailEffect email:
                    RegisterEmailSecrets(email, sink);
                    builder.Append("  ~ email (").Append(email.Channel).Append(") to ").Append(email.To).Append('\n');
                    builder.Append("      subject: ").Append(email.Subject).Append('\n');
                    if (!string.IsNullOrWhiteSpace(email.TextBody))
                    {
                        builder.Append("      text:\n");
                        foreach (var line in email.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n').Split('\n'))
                        {
                            builder.Append("        ").Append(line.TrimEnd()).Append('\n');
                        }
                    }

                    builder.Append("      html:\n");
                    foreach (var line in HtmlCanonicalizer.Render(email.HtmlBody, sink).Split('\n'))
                    {
                        builder.Append("        ").Append(line).Append('\n');
                    }

                    break;

                case SmsEffect sms:
                    if (sms.Code != null)
                    {
                        sink.Register(sms.Code, "sms-code");
                    }

                    builder.Append("  ~ sms ").Append(sms.Operation).Append(" to ").Append(sms.To)
                        .Append(" (").Append(sms.Purpose).Append(')');
                    if (sms.Code != null)
                    {
                        builder.Append(" code ").Append(sms.Code);
                    }

                    if (sms.Approved is { } approved)
                    {
                        builder.Append(approved ? " approved" : " rejected");
                    }

                    builder.Append('\n');
                    break;

                case HttpEffect http:
                    sink.Inspect(http.Url);
                    builder.Append("  ~ http ").Append(http.Client).Append(": ").Append(http.Method).Append(' ')
                        .Append(http.Url).Append(" -> ").Append(http.StatusCode).Append('\n');
                    if (!string.IsNullOrEmpty(http.RequestBody))
                    {
                        foreach (var line in RenderBody(http.RequestContentType, http.RequestBody, sink, isRequest: true))
                        {
                            builder.Append("      ").Append(line).Append('\n');
                        }
                    }

                    break;

                case DnsEffect dns:
                    builder.Append("  ~ dns TXT ").Append(dns.RecordName).Append(" expecting ").Append(dns.ExpectedValue)
                        .Append(dns.Found ? " -> found" : " -> not found").Append('\n');
                    break;
            }
        }
    }

    /// <summary>One-time codes appear only as text in an email; register them before rendering.</summary>
    private static void RegisterEmailSecrets(EmailEffect email, TranscriptValueSink sink)
    {
        foreach (var text in new[] { email.Subject, email.TextBody ?? string.Empty })
        {
            foreach (Match code in SixDigitCode().Matches(text))
            {
                sink.Register(code.Value, "otp");
            }

            foreach (Match url in Url().Matches(text))
            {
                sink.Inspect(url.Value);
            }
        }
    }

    [GeneratedRegex("'nonce-(?<nonce>[^']+)'")]
    private static partial Regex CspNonce();

    [GeneratedRegex(@"(?<![\d])\d{6}(?![\d])")]
    private static partial Regex SixDigitCode();

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex Url();
}
