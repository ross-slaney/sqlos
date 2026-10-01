using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Gates;

/// <summary>
/// The scrubber decides what every approved transcript looks like, so its rules are pinned
/// here: stable per-transcript placeholders, format-class timestamps, relative lifetimes, and
/// values that must stay readable.
/// </summary>
[TestClass]
[TestCategory("unit")]
public sealed class ScrubberTests
{
    [TestMethod]
    public void Same_value_maps_to_the_same_placeholder_and_numbers_follow_first_appearance()
    {
        var scrubber = new Scrubber();
        scrubber.Register("Zq3vN8xYpL2mK7tR4wB9", "code");
        scrubber.Register("Hd5fG2jK8lM1nB4vC7xZ", "code");

        var scrubbed = scrubber.Scrub("b=Hd5fG2jK8lM1nB4vC7xZ a=Zq3vN8xYpL2mK7tR4wB9 again=Hd5fG2jK8lM1nB4vC7xZ");

        Assert.AreEqual("b={code#1} a={code#2} again={code#1}", scrubbed);
    }

    [TestMethod]
    public void Counters_are_per_kind_and_continue_across_calls()
    {
        var scrubber = new Scrubber();
        scrubber.Register("state-Q1w2E3r4T5y6", "state");
        scrubber.Register("code-A1s2D3f4G5h6", "code");

        Assert.AreEqual("{state#1} {code#1}", scrubber.Scrub("state-Q1w2E3r4T5y6 code-A1s2D3f4G5h6"));
        Assert.AreEqual("{code#1}", scrubber.Scrub("code-A1s2D3f4G5h6"));
    }

    [TestMethod]
    public void Named_registrations_read_as_names_and_cover_encoded_forms()
    {
        var scrubber = new Scrubber();
        scrubber.RegisterNamed("alice-1a2b3c4d@example.test", "email", "alice");
        scrubber.RegisterNamed("ALICE-1A2B3C4D@EXAMPLE.TEST", "email", "ALICE");

        var scrubbed = scrubber.Scrub(
            "to alice-1a2b3c4d@example.test login_hint=alice-1a2b3c4d%40example.test normalized ALICE-1A2B3C4D@EXAMPLE.TEST");

        Assert.AreEqual("to {email:alice} login_hint={email:alice} normalized {email:ALICE}", scrubbed);
    }

    [TestMethod]
    public void Registered_values_do_not_match_inside_longer_alphanumeric_runs_but_do_next_to_punctuation()
    {
        var scrubber = new Scrubber();
        scrubber.Register("R4nd0mNonceV4lue", "csp-nonce");

        Assert.AreEqual(
            "'nonce-{csp-nonce#1}' xR4nd0mNonceV4lue suffix_{csp-nonce#1}",
            scrubber.Scrub("'nonce-R4nd0mNonceV4lue' xR4nd0mNonceV4lue suffix_R4nd0mNonceV4lue"));
    }

    [TestMethod]
    public void SqlOS_prefixed_ids_use_their_prefix_as_the_kind()
    {
        var scrubber = new Scrubber();

        var scrubbed = scrubber.Scrub(
            "usr_765489504f5d455186cf1e01 org_0c03954bda3c4833a7977090 usr_111111111111111111111aaa usr_765489504f5d455186cf1e01 grant_0123456789abcdef01234567");

        Assert.AreEqual("{usr#1} {org#1} {usr#2} {usr#1} {grant#1}", scrubbed);
    }

    [TestMethod]
    public void Guids_and_jwts_are_detected()
    {
        var scrubber = new Scrubber();
        const string jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c3IifQ.c2lnbmF0dXJl";

        var scrubbed = scrubber.Scrub(
            $"id=3f2504e0-4f89-11d3-9a0c-0305e82c3301 token={jwt} other=3F2504E0-4F89-11D3-9A0C-0305E82C3302");

        Assert.AreEqual("id={guid#1} token={jwt#1} other={guid#2}", scrubbed);
    }

    [TestMethod]
    public void Jwts_that_differ_only_in_when_they_were_minted_share_a_placeholder()
    {
        static string Jwt(string claims)
            => $"{Encode("""{"alg":"RS256","typ":"at+jwt"}""")}.{Encode(claims)}.c2ln";
        static string Encode(string json)
            => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var first = Jwt("""{"sub":"usr_1","iat":1790000000,"exp":1790000600,"at_hash":"aaaa"}""");
        var secondLater = Jwt("""{"sub":"usr_1","iat":1790000001,"exp":1790000601,"at_hash":"bbbb"}""");
        var otherSubject = Jwt("""{"sub":"usr_2","iat":1790000000,"exp":1790000600}""");
        var scrubber = new Scrubber();
        scrubber.Register(first, "access-token");
        scrubber.Register(secondLater, "access-token");

        Assert.AreEqual("{access-token#1} {access-token#1} {jwt#1}", scrubber.Scrub($"{first} {secondLater} {otherSubject}"));
    }

    [TestMethod]
    public void Timestamps_are_scrubbed_by_format_class_so_format_changes_stay_visible()
    {
        var scrubber = new Scrubber();

        var scrubbed = scrubber.Scrub(string.Join('\n',
            "2026-09-30T18:02:05.032651Z",
            "2026-09-30T18:02:00.297558",
            "2026-09-30T18:02:00+00:00",
            "2026-09-30 18:00:08.56",
            "Wed, 30 Sep 2026 18:17:05 GMT",
            "export-2026-09-30.csv"));

        Assert.AreEqual(string.Join('\n',
            "{datetime:utc-z}",
            "{datetime:unspecified}",
            "{datetime:offset}",
            "{datetime:unspecified}",
            "{datetime:rfc1123}",
            "export-{date}.csv"), scrubbed);
    }

    [TestMethod]
    public void Url_encoded_timestamps_are_scrubbed_by_format_class_and_keep_their_encoding_visible()
    {
        var scrubber = new Scrubber();

        var scrubbed = scrubber.Scrub(
            "timeMin=2026-08-31T21%3A57%3A54Z&timeMax=2026-09-30T21%3A57%3A54.1234567%2B02%3A00" +
            "&at=2026-09-30%2018%3A00&until=2026-09-30T18%3a00%3a00-07%3a00&day=2026-09-30");

        Assert.AreEqual(
            "timeMin={datetime:utc-z,url-encoded}&timeMax={datetime:offset,url-encoded}" +
            "&at={datetime:unspecified,url-encoded}&until={datetime:offset,url-encoded}&day={date}",
            scrubbed);
    }

    [TestMethod]
    public void Clock_text_hosted_pages_print_is_scrubbed_by_format_class()
    {
        var scrubber = new Scrubber();

        Assert.AreEqual(
            "Expires {datetime:MM/dd/yyyy_HH:mm} UTC. Code 12/34 and 1/2/2026 stay.",
            scrubber.Scrub("Expires 10/07/2026 20:52 UTC. Code 12/34 and 1/2/2026 stay."));
    }

    [TestMethod]
    public void Links_in_email_text_do_not_swallow_the_sentence_punctuation_after_them()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        const string text = "Reset it: https://sqlos.example.test/sqlos/auth/password/reset?token=Qx7vB2nM9kL4pR8s. This link expires in 1 hour(s)!";
        const string html = """<a href="https://sqlos.example.test/sqlos/auth/password/reset?token=Qx7vB2nM9kL4pR8s">Reset</a>""";

        var rendered = string.Join('\n', TranscriptRenderer.RenderBody("text/plain", text, sink, isRequest: false))
                       + '\n' + HtmlCanonicalizer.Render(html, sink);
        var scrubbed = sink.Scrubber.Scrub(rendered);

        StringAssert.Contains(scrubbed, "reset?token={link-token#1}. This link");
        StringAssert.Contains(scrubbed, "href=\"https://sqlos.example.test/sqlos/auth/password/reset?token={link-token#1}\"");
        Assert.IsFalse(scrubbed.Contains("{link-token#2}", StringComparison.Ordinal), "The text and the HTML link carry one token.\n" + scrubbed);
    }

    [TestMethod]
    public void Urls_printed_as_page_text_do_not_swallow_the_sentence_punctuation_after_them()
    {
        var sink = new TranscriptValueSink(new Scrubber());

        var rendered = HtmlCanonicalizer.Render(
            "<html><body><p>Open https://sqlos.example.test/sqlos/auth/invitations/accept?token=Zr4kP9wQ2mX7vN3b, then sign in.</p></body></html>",
            sink);

        StringAssert.Contains(sink.Scrubber.Scrub(rendered), "accept?token={link-token#1}, then sign in.");
    }

    [TestMethod]
    public void Epoch_claims_a_host_echoes_as_strings_or_numbers_render_as_epoch_and_keep_their_lifetimes()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        using var document = JsonDocument.Parse(
            """
            {"claims":[
              {"type":"amr","value":"password"},
              {"type":"exp","value":"1790000600"},
              {"type":"iat","value":"1790000000"},
              {"type":"nbf","value":1790000000},
              {"type":"auth_time","value":"1789999990"},
              {"type":"sub","value":"1790000000"}
            ]}
            """);

        var rendered = CanonicalJson.Render(document.RootElement, sink);

        StringAssert.Contains(rendered, "\"type\": \"exp\",\n      \"value\": \"{epoch}\"");
        StringAssert.Contains(rendered, "\"type\": \"iat\",\n      \"value\": \"{epoch}\"");
        StringAssert.Contains(rendered, "\"type\": \"nbf\",\n      \"value\": {epoch}");
        StringAssert.Contains(rendered, "\"type\": \"auth_time\",\n      \"value\": \"{epoch}\"");
        StringAssert.Contains(rendered, "\"type\": \"sub\",\n      \"value\": \"1790000000\"", "Only claims that hold a time are clock readings.");
        CollectionAssert.AreEqual(new[] { "exp-iat=10m exp-nbf=10m nbf-iat=0s" }, sink.DrainClaimLifetimes().ToArray());
    }

    [TestMethod]
    public void A_redaction_marker_under_a_password_field_is_not_a_password()
    {
        var scrubber = new Scrubber();
        var sink = new TranscriptValueSink(scrubber);

        sink.RegisterRole("password", "[redacted]");
        sink.RegisterRole("password", "Lock-Alice-2468!");

        Assert.AreEqual(
            "\"password\": \"[redacted]\", \"api_key\": \"[redacted]\", typed {password#1}",
            scrubber.Scrub("\"password\": \"[redacted]\", \"api_key\": \"[redacted]\", typed Lock-Alice-2468!"));
    }

    [TestMethod]
    public void Masking_hides_per_run_values_without_numbering_them()
    {
        var scrubber = new Scrubber();
        scrubber.Register("Kd8sLp2QwE5rT7yU", "code");
        scrubber.RegisterNamed("alice-1a2b3c4d@example.test", "email", "alice");
        const string text = "usr_765489504f5d455186cf1e01 code=Kd8sLp2QwE5rT7yU to alice-1a2b3c4d@example.test at 2026-09-30T18:02:05Z";

        Assert.AreEqual("{usr} code={code} to {email:alice} at {datetime:utc-z}", scrubber.Mask(text));
        Assert.AreEqual("{usr#1} code={code#1} to {email:alice} at {datetime:utc-z}", scrubber.Scrub(text), "Masking allocated no placeholders.");
    }

    [TestMethod]
    public void Values_registered_from_parallel_requests_are_all_scrubbed()
    {
        var scrubber = new Scrubber();
        var values = Enumerable.Range(0, 400).Select(index => $"Tr4ce{index:D4}Q9wE7rT2yU").ToList();

        Parallel.ForEach(values, new ParallelOptions { MaxDegreeOfParallelism = 8 }, value => scrubber.Register(value, "trace"));

        var scrubbed = scrubber.Scrub(string.Join(' ', values));
        Assert.AreEqual(string.Join(' ', values.Select((_, index) => $"{{trace#{index + 1}}}")), scrubbed);
    }

    [TestMethod]
    public void High_entropy_tokens_and_hex_digests_are_scrubbed_but_identifiers_and_codes_stay_readable()
    {
        var scrubber = new Scrubber();

        var scrubbed = scrubber.Scrub(
            "CfDJ8AAAAAAAAAAAAAAAAAAAACeTwIBKQE62oaU1pkOsqIYdrOZg e578e95e61a2f4f30363848096b6990aad0a5fcf " +
            "sqlos-auth-page-primary-button invalid_grant authorization_pending sqlos-dashboard-v2 " +
            "sqlos_auth_page_csrf_626a5acebf261179");

        Assert.AreEqual(
            "{token#1} {hex#1} sqlos-auth-page-primary-button invalid_grant authorization_pending sqlos-dashboard-v2 " +
            "sqlos_auth_page_csrf_{hex#2}",
            scrubbed);
    }

    [TestMethod]
    public void Existing_placeholders_and_short_numbers_are_left_alone()
    {
        var scrubber = new Scrubber();

        Assert.AreEqual(
            "{epoch} {script-sha256:0123456789abcdef} ~600 900 {email:alice}",
            scrubber.Scrub("{epoch} {script-sha256:0123456789abcdef} ~600 900 {email:alice}"));
    }

    [TestMethod]
    public void Scrubbing_the_same_text_twice_with_fresh_scrubbers_is_identical()
    {
        const string text = "usr_765489504f5d455186cf1e01 CfDJ8AAAAAAAAAAAAAAAAAAAACeTwIBKQE62oaU1pkOsqIYdrOZg 2026-09-30T18:02:05Z";

        Assert.AreEqual(new Scrubber().Scrub(text), new Scrubber().Scrub(text));
    }

    [TestMethod]
    public void Role_values_are_registered_only_when_they_look_opaque()
    {
        var scrubber = new Scrubber();
        var sink = new TranscriptValueSink(scrubber);

        sink.RegisterRole("code", "invalid_grant");
        sink.RegisterRole("code", "Kd8sLp2QwE5rT7yU");
        sink.RegisterRole("state", "xyz");

        Assert.AreEqual("invalid_grant {code#1} xyz", scrubber.Scrub("invalid_grant Kd8sLp2QwE5rT7yU xyz"));
    }

    [TestMethod]
    public void Canonical_json_sorts_members_keeps_array_order_and_scrubs_clock_values()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        using var document = JsonDocument.Parse(
            """{"z":1,"a":[3,1,2],"iat":1790000000,"expires_in":599,"resetScopes":["user","email"],"m":{"b":true,"a":null},"logo":"data:image/png;base64,iVBORw0KGgo="}""");

        var rendered = CanonicalJson.Render(document.RootElement, sink);

        Assert.AreEqual(
            """
            {
              "a": [
                3,
                1,
                2
              ],
              "expires_in": ~600,
              "iat": {epoch},
              "logo": "{data-uri:image/png}",
              "m": {
                "a": null,
                "b": true
              },
              "resetScopes": [
                "email",
                "user"
              ],
              "z": 1
            }
            """.ReplaceLineEndings("\n"),
            rendered);
    }

    [TestMethod]
    public void Canonical_json_sorts_the_bucket_scopes_a_password_lockout_lists_in_load_order()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        using var document = JsonDocument.Parse("""{"failureReason":"invalid_password","lockedScopes":["user","email"]}""");

        var rendered = CanonicalJson.Render(document.RootElement, sink);

        StringAssert.Contains(rendered, "\"lockedScopes\": [\n    \"email\",\n    \"user\"\n  ]");
    }

    [TestMethod]
    public void Canonical_json_sorts_unordered_arrays_by_content_not_by_generated_ids()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        using var document = JsonDocument.Parse(
            """{"organizationSelection":[{"id":"org_00000000000000000000000000000000","name":"Globex"},{"id":"org_ffffffffffffffffffffffffffffffff","name":"Acme"}]}""");

        var rendered = CanonicalJson.Render(document.RootElement, sink);

        Assert.IsTrue(
            rendered.IndexOf("Acme", StringComparison.Ordinal) < rendered.IndexOf("Globex", StringComparison.Ordinal),
            $"Unordered arrays must sort by their stable content, not by random IDs:\n{rendered}");
    }

    [TestMethod]
    public void Jwt_lifetimes_are_exact_relative_seconds()
    {
        using var claims = JsonDocument.Parse("""{"iat":1790000000,"nbf":1790000000,"exp":1790000600}""");

        Assert.AreEqual("exp-iat=10m exp-nbf=10m nbf-iat=0s", JwtRendering.DescribeLifetimes(claims.RootElement));
    }

    [TestMethod]
    public void Cookie_expiry_is_a_relative_lifetime_and_deletion_is_marked()
    {
        var started = new DateTimeOffset(2026, 9, 30, 18, 2, 5, 400, TimeSpan.Zero);
        var completed = started.AddMilliseconds(900);
        var sink = new TranscriptValueSink(new Scrubber());

        var lifetime = SetCookieRendering.Render(
            "sqlos_auth_page=abc; expires=Wed, 30 Sep 2026 18:17:05 GMT; max-age=900; path=/sqlos/auth; secure; samesite=strict; httponly",
            started,
            completed,
            sink);
        var deletion = SetCookieRendering.Render(
            "sqlos_auth_page=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/",
            started,
            completed,
            sink);

        Assert.AreEqual("sqlos_auth_page=abc; expires=+15m; max-age=900; path=/sqlos/auth; secure; samesite=strict; httponly", lifetime);
        Assert.AreEqual("sqlos_auth_page=; expires={unix-epoch}; path=/", deletion);
    }

    [TestMethod]
    public void Secrets_in_urls_printed_as_page_text_are_registered()
    {
        var sink = new TranscriptValueSink(new Scrubber());

        var rendered = HtmlCanonicalizer.Render(
            "<html><body><code>otpauth://totp/Lock:alice?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&amp;issuer=Lock</code><p>JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP</p></body></html>",
            sink);

        var scrubbed = sink.Scrubber.Scrub(rendered);
        Assert.IsFalse(scrubbed.Contains("JBSWY3DPEHPK3PXP", StringComparison.Ordinal), scrubbed);
        StringAssert.Contains(scrubbed, "{totp-secret#1}");
    }

    [TestMethod]
    public void Html_is_normalized_with_digests_for_inline_code_and_data_uris()
    {
        var sink = new TranscriptValueSink(new Scrubber());

        var rendered = HtmlCanonicalizer.Render(
            """
            <!DOCTYPE html><html><head><style nonce="N0nce12345678">body { color: red }</style></head>
            <body>  <h1>  Sign   in </h1><img src="data:image/png;base64,iVBORw0KGgo=" />
            <!-- dropped --><script nonce="N0nce12345678">run()</script></body></html>
            """,
            sink);

        StringAssert.Contains(rendered, "<style nonce=\"N0nce12345678\">{style-sha256:");
        StringAssert.Contains(rendered, "<h1>Sign in</h1>");
        StringAssert.Contains(rendered, "<img src=\"{data-uri:image/png}\" />");
        Assert.IsFalse(rendered.Contains("dropped", StringComparison.Ordinal));
        Assert.AreEqual("{csp-nonce#1}", sink.Scrubber.Scrub("N0nce12345678"));
    }

    [TestMethod]
    public void Stylesheet_and_script_responses_render_as_the_same_digest_as_inline_code()
    {
        var sink = new TranscriptValueSink(new Scrubber());
        const string css = ":root {\r\n  --accent: #1d4ed8;\r\n}\n";

        var stylesheet = TranscriptRenderer.RenderBody("text/css; charset=utf-8", css, sink, isRequest: false).Single();
        var script = TranscriptRenderer.RenderBody("application/javascript", "(function () { run(); })();", sink, isRequest: false).Single();
        var inline = HtmlCanonicalizer.Render($"<html><head><style>{css}</style></head><body></body></html>", sink);

        StringAssert.StartsWith(stylesheet, "{style-sha256:");
        StringAssert.StartsWith(script, "{script-sha256:");
        StringAssert.Contains(inline, stylesheet, "A stylesheet response and the same inline <style> share one digest.");
        Assert.AreEqual(stylesheet, sink.Scrubber.Scrub(stylesheet), "Digests are placeholders and are never rescrubbed.");
    }
}
