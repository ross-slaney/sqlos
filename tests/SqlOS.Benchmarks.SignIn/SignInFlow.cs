using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using static SqlOS.BehaviorLock.Host.BehaviorLockConstants;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// One way to sign in, driven over HTTP the way its client would drive it. A flow fails the run
/// on any answer other than a successful sign-in, so no error path is ever timed.
/// </summary>
internal abstract class SignInFlow
{
    public static IReadOnlyList<string> Names { get; } =
        [PasswordSignIn.FlowName, EmailCodeSignIn.FlowName, EmailCodeSignIn.ReturningFlowName, HostedSignIn.FlowName];

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>The flow's requests, in order; each is timed on its own and the total is their sum.</summary>
    public abstract IReadOnlyList<string> Steps { get; }

    public abstract bool UsesPassword { get; }

    public static SignInFlow Create(string name, BenchmarkHost host)
        => name switch
        {
            PasswordSignIn.FlowName => new PasswordSignIn(),
            EmailCodeSignIn.FlowName => new EmailCodeSignIn(host, returning: false),
            EmailCodeSignIn.ReturningFlowName => new EmailCodeSignIn(host, returning: true),
            HostedSignIn.FlowName => new HostedSignIn(),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown sign-in flow.")
        };

    public abstract Task RunAsync(BenchmarkUser user, IterationRecorder recorder, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the flow's accounts to the state its sign-ins start from, after they are created and
    /// before anything is measured. <paramref name="signIn"/> runs one unmeasured sign-in of this
    /// flow. Most flows start from a new account and need nothing.
    /// </summary>
    public virtual Task PrepareAsync(
        IReadOnlyList<BenchmarkUser> users,
        Func<BenchmarkUser, Task> signIn,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    protected static HttpRequestMessage PostJson(string path, object body)
        => new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
}

/// <summary>A first-party client's direct password sign-in: tokens come back in the JSON body.</summary>
internal sealed class PasswordSignIn : SignInFlow
{
    public const string FlowName = "password";

    public override string Name => FlowName;

    public override string Description => "POST /sqlos/auth/password/login with a first-party client";

    public override IReadOnlyList<string> Steps { get; } = ["login"];

    public override bool UsesPassword => true;

    public override async Task RunAsync(BenchmarkUser user, IterationRecorder recorder, CancellationToken cancellationToken)
    {
        var login = await recorder.SendAsync(0, PostJson("/sqlos/auth/password/login", new
        {
            email = user.Email,
            password = user.Password,
            clientId = AppClientId
        }), cancellationToken);
        login.Expect(HttpStatusCode.OK).JsonString("tokens", "accessToken");
    }
}

/// <summary>
/// A first-party client's email-code sign-in: start emails a code and returns a challenge token,
/// and verify trades both for tokens. The code is read from the email the host's fake captured.
/// <c>email-code</c> is an account's first, whose code proves and claims the unverified address
/// an operator created it with. <c>email-code-returning</c> signs in an account whose address an
/// earlier email-code sign-in already verified, which is most sign-ins.
/// </summary>
internal sealed class EmailCodeSignIn(BenchmarkHost host, bool returning) : SignInFlow
{
    public const string FlowName = "email-code";

    public const string ReturningFlowName = "email-code-returning";

    public override string Name => returning ? ReturningFlowName : FlowName;

    public override string Description => returning
        ? "the email-code requests for an account an earlier email-code sign-in verified"
        : "POST /sqlos/auth/email-otp/start, then /email-otp/verify with the emailed code (the account's first)";

    public override IReadOnlyList<string> Steps { get; } = ["start", "verify"];

    public override bool UsesPassword => false;

    /// <summary>
    /// For returning accounts, signs each in once with a code, which verifies its address, then
    /// waits out the resend cooldown: until it passes, SqlOS refuses another code for the same
    /// address and client.
    /// </summary>
    public override async Task PrepareAsync(
        IReadOnlyList<BenchmarkUser> users,
        Func<BenchmarkUser, Task> signIn,
        CancellationToken cancellationToken)
    {
        if (!returning)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var user in users)
        {
            await signIn(user);
        }

        var wait = host.EmailCodeResendCooldown + TimeSpan.FromSeconds(1) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken);
        }
    }

    public override async Task RunAsync(BenchmarkUser user, IterationRecorder recorder, CancellationToken cancellationToken)
    {
        var emailsBefore = host.Fakes.Effects.Sequence;
        var started = await recorder.SendAsync(0, PostJson("/sqlos/auth/email-otp/start", new
        {
            email = user.Email,
            clientId = AppClientId
        }), cancellationToken);
        var challengeToken = started.Expect(HttpStatusCode.OK).JsonString("challengeToken");
        var code = host.EmailedCode(user.Email, emailsBefore);

        var verified = await recorder.SendAsync(1, PostJson("/sqlos/auth/email-otp/verify", new { challengeToken, code }), cancellationToken);
        verified.Expect(HttpStatusCode.OK).JsonString("tokens", "accessToken");
    }
}

/// <summary>
/// The hosted AuthPage round trip a browser and an OAuth client make together: open the
/// authorization request's password page, post the form (with the page's CSRF cookie and token,
/// from the page's origin), and redeem the code with PKCE at the token endpoint.
/// </summary>
internal sealed class HostedSignIn : SignInFlow
{
    public const string FlowName = "hosted";

    private static readonly HtmlParser Parser = new();

    public override string Name => FlowName;

    public override string Description => "GET /sqlos/auth/authorize (password view), POST /login/password, POST /token";

    public override IReadOnlyList<string> Steps { get; } = ["authorize", "login", "token"];

    public override bool UsesPassword => true;

    public override async Task RunAsync(BenchmarkUser user, IterationRecorder recorder, CancellationToken cancellationToken)
    {
        var verifier = RandomBase64Url(32);
        var state = RandomBase64Url(16);
        var authorize = QueryHelpers.AddQueryString(AuthBasePath + "/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = AppClientId,
            ["redirect_uri"] = AppRedirectUri,
            ["scope"] = "openid profile email offline_access",
            ["state"] = state,
            ["nonce"] = RandomBase64Url(16),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["view"] = "password"
        });
        var page = await recorder.SendAsync(0, new HttpRequestMessage(HttpMethod.Get, authorize), cancellationToken);
        page.Expect(HttpStatusCode.OK);

        var (action, fields) = PasswordForm(page);
        fields["email"] = user.Email;
        fields["password"] = user.Password!;
        var post = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(new Uri(PublicOrigin), authorize), action))
        {
            Content = new FormUrlEncodedContent(fields)
        };
        post.Headers.TryAddWithoutValidation("Origin", PublicOrigin);
        post.Headers.TryAddWithoutValidation("Cookie", page.CookieHeader());
        var login = await recorder.SendAsync(1, post, cancellationToken);
        var redirect = QueryHelpers.ParseQuery(new Uri(login.Expect(HttpStatusCode.OK, HttpStatusCode.Found).NextUrl()).Query);
        if (redirect["state"] != state || string.IsNullOrEmpty(redirect["code"]))
        {
            throw new InvalidOperationException($"The hosted sign-in did not return a code for this request: {login.NextUrl()}");
        }

        var token = await recorder.SendAsync(2, new HttpRequestMessage(HttpMethod.Post, AuthBasePath + "/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = redirect["code"]!,
                ["client_id"] = AppClientId,
                ["redirect_uri"] = AppRedirectUri,
                ["code_verifier"] = verifier
            })
        }, cancellationToken);
        token.Expect(HttpStatusCode.OK).JsonString("access_token");
    }

    /// <summary>The page's password form: its action and its fields as a browser would submit them.</summary>
    private static (string Action, Dictionary<string, string> Fields) PasswordForm(BenchmarkResponse page)
    {
        using var document = Parser.ParseDocument(page.Body);
        var form = document.Forms.FirstOrDefault(candidate => (candidate.GetAttribute("action") ?? string.Empty)
                .Contains("/login/password", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{page.Description} has no password form.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in form.Elements)
        {
            var name = element.GetAttribute("name");
            var type = element.GetAttribute("type")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)
                || type is "submit" or "button" or "image" or "reset"
                || (type is "checkbox" or "radio" && !element.HasAttribute("checked")))
            {
                continue;
            }

            fields[name] = element is IHtmlSelectElement select ? select.Value ?? string.Empty : element.GetAttribute("value") ?? string.Empty;
        }

        return (WebUtility.HtmlDecode(form.GetAttribute("action")!), fields);
    }

    private static string RandomBase64Url(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
