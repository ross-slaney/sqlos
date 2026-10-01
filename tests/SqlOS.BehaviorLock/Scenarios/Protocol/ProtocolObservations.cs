using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Protocol;

/// <summary>
/// How the protocol scenarios record exchanges. Most helpers add one narrow normalization for a
/// value SqlOS or the host produces nondeterministically, and name its source; the others record
/// what the harness cannot capture as an exchange (an unhandled exception) or pair an exchange
/// with the audit events that distinguish its branch.
/// </summary>
internal static class ProtocolObservations
{
    /// <summary>
    /// Records an exchange and then the audit events it wrote. Public auth errors collapse to a
    /// few generic messages; the audit event <c>auth.public_error.mapped</c> carries the internal
    /// diagnostic, so it is what tells one failure branch from another.
    /// </summary>
    public static async Task<HttpExchange> ObserveWithAuditAsync(this Transcript t, HttpExchange exchange, string caption)
    {
        NameAccessTokenHashes(t, exchange);
        t.Observe(exchange, caption);
        await t.ObserveAuditAsync();
        return exchange;
    }

    /// <summary>
    /// Records a token response. An ID token's <c>at_hash</c> hashes the access token issued with
    /// it, and access tokens minted in the same second for the same session are byte-identical
    /// (the harness gives them one placeholder). Whether the ID tokens of a code redemption and a
    /// later refresh share an <c>at_hash</c> therefore depends on the clock, so every
    /// <c>at_hash</c> in a token response is named <c>{hash:at_hash}</c>.
    /// </summary>
    public static HttpExchange ObserveTokens(this Transcript t, HttpExchange exchange, string caption)
    {
        NameAccessTokenHashes(t, exchange);
        return t.Observe(exchange, caption);
    }

    /// <summary>
    /// Sends a request that SqlOS does not handle: the exception escapes the endpoint, SqlOS has no
    /// exception middleware, and TestServer rethrows it to the caller instead of answering. Under
    /// Kestrel the client receives <c>500</c> with an empty body. The transcript records the
    /// exception type and message, so a later layer that starts answering shows up as a diff. If
    /// the host does answer, the exchange is recorded like any other.
    /// </summary>
    public static async Task ObserveUnhandledAsync(this Transcript t, Func<Task<HttpExchange>> send, string caption)
    {
        HttpExchange exchange;
        try
        {
            exchange = await send();
        }
        catch (Exception exception) when (exception is not UnitTestAssertException)
        {
            var failure = exception.GetBaseException();
            t.Note($"{caption}: SqlOS throws {failure.GetType().FullName} \"{failure.Message}\" and writes no response (Kestrel answers 500).");
            return;
        }

        t.Observe(exchange, caption);
    }

    /// <summary>
    /// Records a JWKS response whose keys SqlOS returns in an undefined order, as a document with
    /// the keys named in <paramref name="knownKids"/> first and any other key after them.
    /// <c>SqlOSCryptoService.LoadValidationSigningKeysAsync</c> reads the signing keys without an
    /// <c>ORDER BY</c>, so with more than one key the order follows random primary keys. Clients
    /// select keys by <c>kid</c>, so the order is not behavior; every key and field still is.
    /// Name every key the transcript has already shown, so at most one key is new.
    /// </summary>
    public static void ObserveJwksInStableOrder(this Transcript t, HttpExchange exchange, IReadOnlyList<string> knownKids, string caption)
    {
        t.Discard(exchange);
        var keys = (exchange.Json?["keys"] as JsonArray)?.OfType<JsonObject>().ToList()
            ?? throw new InvalidOperationException($"Exchange {exchange.Describe()} is not a JWKS: {exchange.Preview()}");
        var known = knownKids.ToList();
        var ordered = keys
            .OrderBy(key => known.IndexOf(key["kid"]!.GetValue<string>()) is var index and >= 0 ? index : int.MaxValue)
            .ToList();
        if (ordered.Count(key => !known.Contains(key["kid"]!.GetValue<string>())) > 1)
        {
            throw new InvalidOperationException("More than one key is new to the transcript, so their order would not be stable.");
        }

        foreach (var key in ordered)
        {
            t.Scrub(key["kid"]!.GetValue<string>(), "kid");
        }

        var document = new JsonObject { ["keys"] = new JsonArray(ordered.Select(key => (JsonNode)key.DeepClone()).ToArray()) };
        t.ObserveDocument(
            $"{caption} ({exchange.Method} {exchange.Target} -> {exchange.StatusCode} {exchange.ResponseContentType}; keys in first-seen order)",
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Records a hosted page that lists the user's organizations, as a document with the
    /// organizations in name order. <c>SqlOSAdminService.GetUserOrganizationsAsync</c> reads
    /// memberships without an <c>ORDER BY</c>, so the page lists them in index order of random IDs,
    /// which also differs between SQL Server and PostgreSQL. The document keeps the status, the
    /// view's heading and callouts, each organization's name, slug, role, and submitted value, and
    /// every hidden form field of the page.
    /// </summary>
    public static void ObserveOrganizationChooser(this Transcript t, HttpExchange exchange, string caption)
    {
        t.Discard(exchange);
        using var document = new HtmlParser().ParseDocument(exchange.ResponseBody);
        var options = document.QuerySelectorAll("button.organization-option")
            .Select(button => new
            {
                Name = button.QuerySelector("strong")?.TextContent.Trim() ?? string.Empty,
                Detail = button.QuerySelector("small")?.TextContent.Trim() ?? string.Empty,
                Field = button.GetAttribute("name"),
                Value = button.GetAttribute("value")
            })
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ToList();
        if (options.Count == 0)
        {
            throw new InvalidOperationException($"Exchange {exchange.Describe()} lists no organizations: {exchange.Preview()}");
        }

        var lines = new List<string>
        {
            $"{exchange.StatusCode} {exchange.ResponseContentType}",
            $"heading: {document.QuerySelector("h1")?.TextContent.Trim()}"
        };
        foreach (var callout in document.QuerySelectorAll(".callout"))
        {
            lines.Add($"callout ({callout.ClassName}): {string.Join(' ', callout.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))}");
        }

        foreach (var form in document.Forms)
        {
            lines.Add($"form {form.Method.ToUpperInvariant()} {form.GetAttribute("action")}");
            foreach (var input in form.QuerySelectorAll("input[type=hidden]"))
            {
                var name = input.GetAttribute("name") ?? string.Empty;
                var value = input.GetAttribute("value") ?? string.Empty;
                if (ValueRoles.KindFor(name) is { } kind)
                {
                    t.Scrub(value, kind);
                }

                lines.Add($"  hidden {name}={value}");
            }
        }

        lines.Add("organizations (name order):");
        lines.AddRange(options.Select(option => $"  {option.Name} ({option.Detail}) submits {option.Field}={option.Value}"));
        t.ObserveDocument($"{caption} ({exchange.Method} {exchange.Target})", string.Join('\n', lines));
    }

    /// <summary>Registers the <c>at_hash</c> of every ID token in a token response (see <see cref="ObserveTokens"/>).</summary>
    private static void NameAccessTokenHashes(Transcript t, HttpExchange exchange)
    {
        var json = exchange.Json;
        foreach (var idToken in new[] { json?["id_token"], json?["idToken"], json?["tokens"]?["idToken"] })
        {
            if (idToken is JsonValue value && value.TryGetValue<string>(out var jwt) && jwt.Split('.') is { Length: 3 } parts)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                if (JsonNode.Parse(Convert.FromBase64String(payload))?["at_hash"]?.GetValue<string>() is { } atHash)
                {
                    t.Scrub(atHash, "hash", "at_hash");
                }
            }
        }
    }
}
