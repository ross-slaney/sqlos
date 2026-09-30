using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlOS.BehaviorLock.Host.Fakes;

/// <summary>
/// Fake upstream identity providers for social and custom OIDC sign-in: Google, Microsoft,
/// Apple, GitHub, and a custom OIDC provider at <c>https://oidc.example.local</c>. Ported from
/// <c>tests/SqlOS.IntegrationTests/Infrastructure/FakeOidcProviderHttpClientFactory.cs</c> and
/// keeps its authorization-code conventions so integration-test cases port directly:
/// <list type="bullet">
/// <item><c>success:{email}:{nonce}</c> signs in <c>{email}</c> with a verified email;</item>
/// <item><c>unverified:{email}:{nonce}</c> returns an unverified email;</item>
/// <item><c>split-claims:{idTokenEmail}:{userInfoEmail}:{nonce}</c> returns different emails from the ID token and UserInfo;</item>
/// <item><c>amr-mfa:…</c>, <c>acr-loa2:…</c>, <c>stale-auth-time:…</c>, <c>tampered-amr:…</c>, <c>userinfo-sub-mismatch:…</c> vary the upstream assurance claims;</item>
/// <item><c>missing-email</c> omits the email; any code starting with <c>bad</c> fails with <c>invalid_grant</c>.</item>
/// </list>
/// The signing key is generated per host, so ID tokens and JWKS values are scrubbed in transcripts.
/// </summary>
public sealed class FakeOidcUpstream
{
    public const string KeyId = "fake-oidc-key";
    private readonly RSA _rsa = RSA.Create(2048);

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

        if (request.Method == HttpMethod.Get && uri.Contains(".well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
        {
            return Json(HttpStatusCode.OK, BuildDiscoveryDocument(uri));
        }

        if (request.Method == HttpMethod.Get
            && (uri.Contains("/keys", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("/certs", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("/jwks", StringComparison.OrdinalIgnoreCase)))
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["keys"] = new JsonArray(BuildJwk()) });
        }

        if (request.Method == HttpMethod.Post
            && (uri.Contains("/token", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("github.com/login/oauth/access_token", StringComparison.OrdinalIgnoreCase)))
        {
            var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
            var code = form.GetValueOrDefault("code") ?? string.Empty;
            if (code.StartsWith("bad", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.BadRequest, new JsonObject
                {
                    ["error"] = "invalid_grant",
                    ["error_description"] = "The authorization code is invalid."
                });
            }

            var provider = ResolveProvider(uri);
            if (provider == "apple" && string.IsNullOrWhiteSpace(form.GetValueOrDefault("client_secret")))
            {
                return Json(HttpStatusCode.BadRequest, new JsonObject
                {
                    ["error"] = "invalid_client",
                    ["error_description"] = "Apple requires a client secret."
                });
            }

            var clientId = form.GetValueOrDefault("client_id") ?? "client";
            var parsed = ParseCode(code);
            var payload = new JsonObject
            {
                ["access_token"] = $"{provider}|{code}",
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600
            };
            if (provider != "github")
            {
                payload["id_token"] = CreateIdToken(provider, clientId, parsed);
            }

            return Json(HttpStatusCode.OK, payload);
        }

        if (request.Method == HttpMethod.Get && uri.StartsWith("https://api.github.com/user", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ParseCode(ReadBearerCode(request));
            if (uri.EndsWith("/user/emails", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK, new JsonArray(new JsonObject
                {
                    ["email"] = parsed.UserInfoEmail,
                    ["primary"] = true,
                    ["verified"] = parsed.IsVerified
                }));
            }

            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["id"] = StableNumericId(parsed.Email),
                ["login"] = parsed.Email.Split('@')[0],
                ["name"] = $"GitHub {parsed.Email}"
            });
        }

        if (request.Method == HttpMethod.Get && uri.Contains("userinfo", StringComparison.OrdinalIgnoreCase))
        {
            var token = request.Headers.Authorization?.Parameter ?? string.Empty;
            var parts = token.Split('|', 2, StringSplitOptions.None);
            var provider = parts.Length > 0 ? parts[0] : "google";
            var parsed = ParseCode(parts.Length > 1 ? parts[1] : "success:user@example.com:nonce");
            var subjectMismatch = string.Equals(parsed.Mode, "userinfo-sub-mismatch", StringComparison.Ordinal);
            var userInfoVerified = !string.Equals(parsed.Mode, "split-claims", StringComparison.Ordinal) && parsed.IsVerified;

            return provider switch
            {
                "google" => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["sub"] = subjectMismatch ? $"mismatched-google-{parsed.Email}" : $"google-{parsed.Email}",
                    ["email"] = parsed.UserInfoEmail,
                    ["email_verified"] = userInfoVerified,
                    ["name"] = $"Google {parsed.UserInfoEmail}"
                }),
                "microsoft" => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["sub"] = subjectMismatch ? $"mismatched-microsoft-{parsed.Email}" : $"microsoft-{parsed.Email}",
                    ["preferred_username"] = parsed.UserInfoEmail,
                    ["name"] = $"Microsoft {parsed.UserInfoEmail}"
                }),
                "custom" => Json(HttpStatusCode.OK, new JsonObject
                {
                    ["sub"] = subjectMismatch ? $"mismatched-custom-standard-{parsed.Email}" : $"custom-standard-{parsed.Email}",
                    ["custom_sub"] = $"custom-{parsed.UserInfoEmail}",
                    ["email_address"] = parsed.UserInfoEmail,
                    ["email_verified_flag"] = userInfoVerified,
                    ["full_name"] = $"Custom {parsed.UserInfoEmail}"
                }),
                _ => Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "userinfo_not_supported" })
            };
        }

        return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "not_found", ["url"] = uri });
    }

    private static JsonObject BuildDiscoveryDocument(string uri)
    {
        if (uri.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["issuer"] = "https://accounts.google.com",
                ["authorization_endpoint"] = "https://accounts.google.com/o/oauth2/v2/auth",
                ["token_endpoint"] = "https://oauth2.googleapis.com/token",
                ["userinfo_endpoint"] = "https://openidconnect.googleapis.com/v1/userinfo",
                ["jwks_uri"] = "https://www.googleapis.com/oauth2/v3/certs"
            };
        }

        if (uri.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            var tenant = uri.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(part => !string.Equals(part, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault() ?? "common";
            return new JsonObject
            {
                ["issuer"] = $"https://login.microsoftonline.com/{tenant}/v2.0",
                ["authorization_endpoint"] = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                ["token_endpoint"] = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                ["userinfo_endpoint"] = "https://graph.microsoft.com/oidc/userinfo",
                ["jwks_uri"] = $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys"
            };
        }

        if (uri.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["issuer"] = "https://appleid.apple.com",
                ["authorization_endpoint"] = "https://appleid.apple.com/auth/authorize",
                ["token_endpoint"] = "https://appleid.apple.com/auth/token",
                ["jwks_uri"] = "https://appleid.apple.com/auth/keys"
            };
        }

        return new JsonObject
        {
            ["issuer"] = "https://oidc.example.local",
            ["authorization_endpoint"] = "https://oidc.example.local/authorize",
            ["token_endpoint"] = "https://oidc.example.local/token",
            ["userinfo_endpoint"] = "https://oidc.example.local/userinfo",
            ["jwks_uri"] = "https://oidc.example.local/jwks"
        };
    }

    private static string ResolveProvider(string uri)
    {
        if (uri.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase) || uri.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return "google";
        }

        if (uri.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase) || uri.Contains("graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return "microsoft";
        }

        if (uri.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase))
        {
            return "apple";
        }

        if (uri.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "github";
        }

        return "custom";
    }

    private static string ReadBearerCode(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter ?? string.Empty;
        var parts = token.Split('|', 2, StringSplitOptions.None);
        return parts.Length > 1 ? parts[1] : "success:user@example.com:nonce";
    }

    private static long StableNumericId(string email)
        => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(email)), 0) & 0x3FFFFFFFFFFF;

    private static ParsedCode ParseCode(string code)
    {
        var trimmed = Uri.UnescapeDataString(code);
        if (trimmed.StartsWith("missing-email", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedCode(string.Empty, string.Empty, "nonce", true, "missing-email");
        }

        var parts = trimmed.Split(':', StringSplitOptions.None);
        if (parts.Length >= 4 && string.Equals(parts[0], "split-claims", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedCode(parts[1], parts[2], parts[3], true, "split-claims");
        }

        if (parts.Length >= 3)
        {
            return new ParsedCode(parts[1], parts[1], parts[2], !parts[0].StartsWith("unverified", StringComparison.OrdinalIgnoreCase), parts[0]);
        }

        if (parts.Length == 2)
        {
            return new ParsedCode(parts[1], parts[1], "nonce", !parts[0].StartsWith("unverified", StringComparison.OrdinalIgnoreCase), parts[0]);
        }

        return new ParsedCode("user@example.com", "user@example.com", "nonce", true, "success");
    }

    private string CreateIdToken(string provider, string clientId, ParsedCode parsed)
    {
        var now = DateTimeOffset.UtcNow;
        var issuer = provider switch
        {
            "google" => "https://accounts.google.com",
            "microsoft" => "https://login.microsoftonline.com/common/v2.0",
            "apple" => "https://appleid.apple.com",
            _ => "https://oidc.example.local"
        };

        var claims = new JsonObject { ["iss"] = issuer, ["aud"] = clientId };
        switch (provider)
        {
            case "google":
                claims["sub"] = $"google-{parsed.Email}";
                claims["email"] = parsed.Email;
                claims["email_verified"] = parsed.IsVerified ? "true" : "false";
                claims["name"] = $"Google {parsed.Email}";
                break;
            case "microsoft":
                claims["sub"] = $"microsoft-{parsed.Email}";
                claims["preferred_username"] = parsed.Email;
                claims["name"] = $"Microsoft {parsed.Email}";
                break;
            case "apple":
                claims["sub"] = $"apple-{parsed.Email}";
                claims["email"] = parsed.Email;
                claims["email_verified"] = "true";
                break;
            default:
                claims["sub"] = $"custom-standard-{parsed.Email}";
                claims["custom_sub"] = $"custom-{parsed.Email}";
                claims["email_address"] = parsed.Email;
                claims["email_verified_flag"] = parsed.IsVerified ? "true" : "false";
                claims["full_name"] = $"Custom {parsed.Email}";
                break;
        }

        claims["nonce"] = parsed.Nonce;
        claims["nbf"] = now.ToUnixTimeSeconds();
        claims["iat"] = now.ToUnixTimeSeconds();
        claims["exp"] = now.AddHours(1).ToUnixTimeSeconds();
        if (string.Equals(parsed.Mode, "amr-mfa", StringComparison.Ordinal))
        {
            claims["amr"] = new JsonArray("pwd", "mfa");
        }
        else if (string.Equals(parsed.Mode, "acr-loa2", StringComparison.Ordinal))
        {
            claims["acr"] = "urn:example:loa:2";
        }
        else if (string.Equals(parsed.Mode, "stale-auth-time", StringComparison.Ordinal))
        {
            // A silently reused upstream session: a fresh ID token whose auth_time is the
            // original sign-in, 45 minutes ago.
            claims["auth_time"] = now.AddMinutes(-45).ToUnixTimeSeconds();
        }

        var header = new JsonObject { ["alg"] = "RS256", ["kid"] = KeyId, ["typ"] = "JWT" };
        var signingInput = $"{Base64Url(header.ToJsonString())}.{Base64Url(claims.ToJsonString())}";
        var signature = _rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var token = $"{signingInput}.{Base64Url(signature)}";
        if (!string.Equals(parsed.Mode, "tampered-amr", StringComparison.Ordinal))
        {
            return token;
        }

        // Rewrites the payload after signing, so the signature no longer matches.
        claims["amr"] = new JsonArray("pwd", "mfa");
        var parts = token.Split('.');
        parts[1] = Base64Url(claims.ToJsonString());
        return string.Join('.', parts);
    }

    private JsonObject BuildJwk()
    {
        var parameters = _rsa.ExportParameters(false);
        return new JsonObject
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["kid"] = KeyId,
            ["alg"] = "RS256",
            ["n"] = Base64Url(parameters.Modulus!),
            ["e"] = Base64Url(parameters.Exponent!)
        };
    }

    internal static HttpResponseMessage Json(HttpStatusCode statusCode, JsonNode payload)
        => new(statusCode)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

    internal static Dictionary<string, string> ParseForm(string payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            result[key] = value;
        }

        return result;
    }

    internal static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    internal static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record ParsedCode(string Email, string UserInfoEmail, string Nonce, bool IsVerified, string Mode);
}
