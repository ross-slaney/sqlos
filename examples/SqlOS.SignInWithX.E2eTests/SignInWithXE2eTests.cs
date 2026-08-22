using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.SignInWithX.E2eTests;

/// <summary>
/// Browser end-to-end coverage for the "Sign in with X" federation example.
/// Boots the real Aspire app host (SQL container + App X SqlOS OP + App Y
/// Next.js relying party) on alternate ports so a manually running demo on
/// 5100/3020 is left untouched, then drives headless Chromium through the
/// journeys that once broke silently: signup + consent, federated sign-out
/// with remembered consent, and silent SSO on a live X session.
/// </summary>
[TestClass]
public sealed class SignInWithXE2eTests
{
    // Deliberately different from the demo's 5100/3020/1436 so the tests can
    // run while someone is manually exercising the demo.
    private const int AppXPort = 5110;
    private const int AppYPort = 3030;
    private const int SqlPort = 1438;
    private const string Password = "E2ePassword123!";

    private static readonly string AppXOrigin = $"http://localhost:{AppXPort}";
    private static readonly string AppYOrigin = $"http://localhost:{AppYPort}";
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(4);

    private static DistributedApplication? _app;
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SqlOS_SignInWithX_AppHost>(
        [
            $"--SignInWithX:AppXPort={AppXPort}",
            $"--SignInWithX:AppYPort={AppYPort}",
            $"--SignInWithX:SqlPort={SqlPort}",
            "--SignInWithX:EphemeralSql=true"
        ]);
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        // SQL container start + Next.js dev cold compile are slow; poll until
        // both sides answer before any browser gets involved.
        await WaitForOkAsync($"{AppXOrigin}/sqlos/auth/.well-known/openid-configuration", StartupBudget);
        await WaitForOkAsync($"{AppYOrigin}/api/auth/providers", StartupBudget);
        // Warm App Y's home page so the first browser navigation is not the
        // first Next.js page compile.
        await WaitForOkAsync($"{AppYOrigin}/", TimeSpan.FromMinutes(2));

        (_playwright, _browser) = await LaunchChromiumAsync();
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static async Task ClassCleanup()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [TestMethod]
    public Task FirstSignIn_WalksSignupConsentAndLandsSignedIn() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();

        await page.GotoAsync(AppYOrigin);
        await ClickAppYButtonAsync(page, "Sign in with X");

        // X's hosted pages: the authorize request lands on the identify view;
        // follow the "Get started" link to the signup view.
        await page.WaitForURLAsync(url => url.Contains("/sqlos/auth/"));
        await page.GetByRole(AriaRole.Link, new() { Name = "Get started" }).ClickAsync();
        await page.GetByLabel("Display name").FillAsync("E2E User");
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        // App Y is a third-party client, so the consent screen must appear and
        // list every requested scope by its display name.
        await Assertions.Expect(page.GetByText("App Y is asking to access your account.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Sign you in")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("See your name")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("See your email address")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Allow access" }).ClickAsync();

        await ExpectSignedInOnAppYAsync(page, email);
    });

    [TestMethod]
    public Task SignOut_IsFederated_AndReauthenticationIsRequiredButConsentIsRemembered() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();
        await SignUpThroughConsentAsync(page, email);
        await ExpectSignedInOnAppYAsync(page, email);

        // Federated sign-out: App Y's session ends, then X's, and we land back
        // on App Y signed out.
        await ClickAppYButtonAsync(page, "Sign out");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in with X" }))
            .ToBeVisibleAsync(new() { Timeout = 60_000 });
        StringAssert.StartsWith(page.Url, AppYOrigin, "federated sign-out should land back on App Y");

        // Because X's session was ended too, the next sign-in must show a
        // credentials form again instead of silently bouncing back to App Y.
        await ClickAppYButtonAsync(page, "Sign in with X");
        await page.WaitForURLAsync(url => url.Contains("/sqlos/auth/"));
        await Assertions.Expect(page.Locator("input[name='email'], input[name='password']").First).ToBeVisibleAsync();

        // ...but the consent grant is remembered: after re-entering the same
        // credentials the next navigation lands straight on App Y — nothing
        // ever clicks "Allow access" on the way.
        await SignInWithPasswordAsync(page, email);
        await page.WaitForURLAsync(url => url.StartsWith(AppYOrigin), new() { Timeout = 60_000 });
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Allow access" }).CountAsync());
        await ExpectSignedInOnAppYAsync(page, email);
    });

    [TestMethod]
    public Task SilentSso_WithLiveXSession_SkipsLoginAndConsent() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();
        await SignUpThroughConsentAsync(page, email);
        await ExpectSignedInOnAppYAsync(page, email);

        // Drop only App Y's (Auth.js) cookies so App Y is signed out while X's
        // browser session stays alive. Both apps live on localhost, so filter
        // by cookie name rather than domain.
        var cookies = await page.Context.CookiesAsync();
        var xCookies = cookies
            .Where(c => !c.Name.Contains("next-auth", StringComparison.OrdinalIgnoreCase))
            .Select(c => new Cookie
            {
                Name = c.Name,
                Value = c.Value,
                Domain = c.Domain,
                Path = c.Path,
                Expires = c.Expires,
                HttpOnly = c.HttpOnly,
                Secure = c.Secure,
                SameSite = c.SameSite
            })
            .ToList();
        Assert.AreNotEqual(0, xCookies.Count, "expected App X session cookies to survive the filter");
        await page.Context.ClearCookiesAsync();
        await page.Context.AddCookiesAsync(xCookies);

        await page.GotoAsync(AppYOrigin);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in with X" })).ToBeVisibleAsync();

        // Silent SSO: with a live X session and a remembered grant, X's login
        // and consent pages must never be visited on the way back in.
        var visitedXInteractivePage = false;
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/sqlos/auth/login") ||
                request.Url.Contains("/sqlos/auth/signup") ||
                request.Url.Contains("/sqlos/auth/consent"))
            {
                visitedXInteractivePage = true;
            }
        };

        await ClickAppYButtonAsync(page, "Sign in with X");
        await ExpectSignedInOnAppYAsync(page, email);
        Assert.IsFalse(visitedXInteractivePage, "silent SSO must not route through X's login, signup, or consent pages");
        Assert.AreEqual(0, await page.Locator("input[type='password']").CountAsync(), "no password input may appear during silent SSO");
    });

    // ---- journey helpers -------------------------------------------------

    private static string NewEmail() => $"e2e-{Guid.NewGuid():N}@x.test";

    /// <summary>
    /// App Y's buttons are React onClick handlers, so a click that lands between
    /// server render and hydration is a silent no-op. Waiting for network idle
    /// (the post-hydration /api/auth/session fetch has settled by then) makes the
    /// click reliable.
    /// </summary>
    private static async Task ClickAppYButtonAsync(IPage page, string name)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByRole(AriaRole.Button, new() { Name = name }).ClickAsync();
    }

    private static async Task SignUpThroughConsentAsync(IPage page, string email)
    {
        await page.GotoAsync(AppYOrigin);
        await ClickAppYButtonAsync(page, "Sign in with X");
        await page.WaitForURLAsync(url => url.Contains("/sqlos/auth/"));
        await page.GetByRole(AriaRole.Link, new() { Name = "Get started" }).ClickAsync();
        await page.GetByLabel("Display name").FillAsync("E2E User");
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Allow access" }).ClickAsync();
    }

    private static async Task SignInWithPasswordAsync(IPage page, string email)
    {
        // X's identify-first flow: enter the email, continue to the password
        // view, then submit the password. Tolerate arriving directly on the
        // password view (email prefilled) as well.
        await page.GetByLabel("Email").FillAsync(email);
        var passwordInput = page.GetByLabel("Password", new() { Exact = true });
        if (await passwordInput.CountAsync() == 0)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        }

        await passwordInput.FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
    }

    private static async Task ExpectSignedInOnAppYAsync(IPage page, string email)
    {
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome to App Y" }))
            .ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Assertions.Expect(page.GetByText(email)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("validated")).ToBeVisibleAsync();

        // The subject row renders the OIDC sub inside the page's only <code>.
        var sub = await page.Locator("code").InnerTextAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(sub), "expected a non-empty subject claim on the page");
    }

    // ---- infrastructure --------------------------------------------------

    private async Task RunWithBrowserAsync(Func<IPage, Task> test)
    {
        Assert.IsNotNull(_browser, "browser fixture was not initialized");
        // Fresh context per test: isolated cookies, so each test owns its user
        // and its X/Y sessions.
        await using var context = await _browser.NewContextAsync();
        context.SetDefaultTimeout(30_000);
        context.SetDefaultNavigationTimeout(60_000);
        var page = await context.NewPageAsync();

        try
        {
            await test(page);
        }
        catch
        {
            await TryCaptureScreenshotAsync(page);
            throw;
        }
    }

    private async Task TryCaptureScreenshotAsync(IPage page)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{TestContext.TestName}.png");
            await page.ScreenshotAsync(new() { Path = path, FullPage = true });
            TestContext.WriteLine($"Failure screenshot: {path} (page URL: {page.Url})");
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"Could not capture failure screenshot: {ex.Message}");
        }
    }

    private static async Task<(IPlaywright Playwright, IBrowser Browser)> LaunchChromiumAsync()
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            return (playwright, await playwright.Chromium.LaunchAsync(new() { Headless = true }));
        }
        catch (PlaywrightException)
        {
            // Chromium is missing on this machine: install it once, then retry.
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                playwright.Dispose();
                throw new InvalidOperationException($"'playwright install chromium' failed with exit code {exitCode}.");
            }

            return (playwright, await playwright.Chromium.LaunchAsync(new() { Headless = true }));
        }
    }

    private static async Task WaitForOkAsync(string url, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                lastFailure = new InvalidOperationException($"{url} returned {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastFailure = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"{url} did not return a success status within {timeout}.", lastFailure);
    }
}
