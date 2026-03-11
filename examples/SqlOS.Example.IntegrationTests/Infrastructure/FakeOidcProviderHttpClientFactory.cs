using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SqlOS.Example.IntegrationTests.Infrastructure;

internal sealed class FakeOidcProviderHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => new(new FakeOidcProviderHttpMessageHandler())
        {
            BaseAddress = new Uri("https://localhost")
        };

    private sealed class FakeOidcProviderHttpMessageHandler : HttpMessageHandler
    {
        private static readonly RSA Rsa = RSA.Create(2048);
        private static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "fake-oidc-key" };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (request.Method == HttpMethod.Get && uri.Contains(".well-known/openid-configuration", StringComparison.OrdinalIgnoreCase))
            {
                return Json(HttpStatusCode.OK, BuildDiscoveryDocument(uri));
            }

            if (request.Method == HttpMethod.Get && (uri.Contains("/keys", StringComparison.OrdinalIgnoreCase) || uri.Contains("/certs", StringComparison.OrdinalIgnoreCase) || uri.Contains("/jwks", StringComparison.OrdinalIgnoreCase)))
            {
                return Json(HttpStatusCode.OK, new { keys = new[] { BuildJwk() } });
            }

            if (request.Method == HttpMethod.Post && uri.Contains("/token", StringComparison.OrdinalIgnoreCase))
            {
                var payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                var form = ParseForm(payload);
                var code = form.GetValueOrDefault("code") ?? string.Empty;
                if (code.StartsWith("bad", StringComparison.OrdinalIgnoreCase))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "The authorization code is invalid." });
                }

                var provider = ResolveProvider(uri);
                if (provider == "apple" && string.IsNullOrWhiteSpace(form.GetValueOrDefault("client_secret")))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_client", error_description = "Apple requires a client secret." });
                }

                var clientId = form.GetValueOrDefault("client_id") ?? "client";
                var parsed = ParseCode(code);
                return Json(HttpStatusCode.OK, new
                {
                    access_token = $"{provider}|{code}",
                    token_type = "Bearer",
                    expires_in = 3600,
                    id_token = CreateIdToken(provider, clientId, parsed.email, parsed.nonce, parsed.isVerified)
                });
            }

            if (request.Method == HttpMethod.Get && uri.Contains("userinfo", StringComparison.OrdinalIgnoreCase))
            {
                var token = request.Headers.Authorization?.Parameter ?? string.Empty;
                var parts = token.Split('|', 2, StringSplitOptions.None);
                var provider = parts.Length > 0 ? parts[0] : "google";
                var parsed = ParseCode(parts.Length > 1 ? parts[1] : "success:user@example.com:nonce");

                return provider switch
                {
                    "google" => Json(HttpStatusCode.OK, new
                    {
                        sub = $"google-{parsed.email}",
                        email = parsed.email,
                        email_verified = parsed.isVerified,
                        name = $"Google {parsed.email}"
                    }),
                    "microsoft" => Json(HttpStatusCode.OK, new
                    {
                        sub = $"microsoft-{parsed.email}",
                        preferred_username = parsed.email,
                        name = $"Microsoft {parsed.email}"
                    }),
                    "custom" => Json(HttpStatusCode.OK, new
                    {
                        custom_sub = $"custom-{parsed.email}",
                        email_address = parsed.email,
                        email_verified_flag = parsed.isVerified,
                        full_name = $"Custom {parsed.email}"
                    }),
                    _ => Json(HttpStatusCode.NotFound, new { error = "userinfo_not_supported" })
                };
            }

            return Json(HttpStatusCode.NotFound, new { error = "not_found", url = uri });
        }

        private static object BuildDiscoveryDocument(string uri)
        {
            if (uri.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    issuer = "https://accounts.google.com",
                    authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                    token_endpoint = "https://oauth2.googleapis.com/token",
                    userinfo_endpoint = "https://openidconnect.googleapis.com/v1/userinfo",
                    jwks_uri = "https://www.googleapis.com/oauth2/v3/certs"
                };
            }

            if (uri.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
            {
                var tenant = uri.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(part => !string.Equals(part, "login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                    .Skip(1)
                    .FirstOrDefault() ?? "common";

                return new
                {
                    issuer = $"https://login.microsoftonline.com/{tenant}/v2.0",
                    authorization_endpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                    token_endpoint = $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                    userinfo_endpoint = "https://graph.microsoft.com/oidc/userinfo",
                    jwks_uri = $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys"
                };
            }

            if (uri.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase))
            {
                return new
                {
                    issuer = "https://appleid.apple.com",
                    authorization_endpoint = "https://appleid.apple.com/auth/authorize",
                    token_endpoint = "https://appleid.apple.com/auth/token",
                    jwks_uri = "https://appleid.apple.com/auth/keys"
                };
            }

            return new
            {
                issuer = "https://oidc.example.local",
                authorization_endpoint = "https://oidc.example.local/authorize",
                token_endpoint = "https://oidc.example.local/token",
                userinfo_endpoint = "https://oidc.example.local/userinfo",
                jwks_uri = "https://oidc.example.local/jwks"
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

            return "custom";
        }

        private static (string email, string nonce, bool isVerified) ParseCode(string code)
        {
            var trimmed = Uri.UnescapeDataString(code);
            if (trimmed.StartsWith("missing-email", StringComparison.OrdinalIgnoreCase))
            {
                return (string.Empty, "nonce", true);
            }

            var parts = trimmed.Split(':', StringSplitOptions.None);
            if (parts.Length >= 3)
            {
                return (parts[1], parts[2], !parts[0].StartsWith("unverified", StringComparison.OrdinalIgnoreCase));
            }

            if (parts.Length == 2)
            {
                return (parts[1], "nonce", !parts[0].StartsWith("unverified", StringComparison.OrdinalIgnoreCase));
            }

            return ("user@example.com", "nonce", true);
        }

        private static string CreateIdToken(string provider, string clientId, string email, string nonce, bool isVerified)
        {
            var now = DateTime.UtcNow;
            var issuer = provider switch
            {
                "google" => "https://accounts.google.com",
                "microsoft" => "https://login.microsoftonline.com/common/v2.0",
                "apple" => "https://appleid.apple.com",
                _ => "https://oidc.example.local"
            };

            Claim[] claims = provider switch
            {
                "google" =>
                [
                    new Claim("sub", $"google-{email}"),
                    new Claim("email", email),
                    new Claim("email_verified", isVerified ? "true" : "false"),
                    new Claim("name", $"Google {email}"),
                    new Claim("nonce", nonce)
                ],
                "microsoft" =>
                [
                    new Claim("sub", $"microsoft-{email}"),
                    new Claim("preferred_username", email),
                    new Claim("name", $"Microsoft {email}"),
                    new Claim("nonce", nonce)
                ],
                "apple" =>
                [
                    new Claim("sub", $"apple-{email}"),
                    new Claim("email", email),
                    new Claim("email_verified", "true"),
                    new Claim("nonce", nonce)
                ],
                _ =>
                [
                    new Claim("custom_sub", $"custom-{email}"),
                    new Claim("email_address", email),
                    new Claim("email_verified_flag", isVerified ? "true" : "false"),
                    new Claim("full_name", $"Custom {email}"),
                    new Claim("nonce", nonce)
                ]
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: clientId,
                claims: claims,
                notBefore: now,
                expires: now.AddHours(1),
                signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
            token.Header["kid"] = SigningKey.KeyId;
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static object BuildJwk()
        {
            var parameters = Rsa.ExportParameters(false);
            return new
            {
                kty = "RSA",
                use = "sig",
                kid = SigningKey.KeyId,
                alg = "RS256",
                n = Base64UrlEncoder.Encode(parameters.Modulus),
                e = Base64UrlEncoder.Encode(parameters.Exponent)
            };
        }

        private static HttpResponseMessage Json(HttpStatusCode statusCode, object payload)
            => new(statusCode)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload))
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };

        private static Dictionary<string, string> ParseForm(string payload)
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
    }
}
