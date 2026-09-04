using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Example.E2eTests;

/// <summary>
/// Browser end-to-end coverage for the product-owned (headless) login UIs that
/// consume <c>@sqlos/headless</c>. Boots the real Example app host (SQL
/// container + Example API + Next.js + Angular) on alternate ports so a
/// manually running demo on 5062/3010/4200 is left untouched, then drives
/// Chromium through the journeys the HTTP integration tests cannot see: the
/// package resuming a request in the browser, posting each step with the
/// AuthPage cookie, recovering the authorization code from the redirect, and
/// the host OIDC library (Auth.js, angular-oauth2-oidc) finishing <c>/token</c>.
/// </summary>
[TestClass]
public sealed class HeadlessExamplesE2eTests
{
    // Deliberately different from the demo's 5062/3010/4200/1434.
    private const int ApiPort = 5162;
    private const int WebPort = 3110;
    private const int AngularPort = 4300;
    private const int SqlPort = 1439;

    // Seeded by RetailSeedService. The retail-demo organization requires MFA for
    // every member, so a first sign-in must enroll an authenticator.
    private const string DemoPassword = "RetailDemo1!";
    private const string AliceEmail = "alice@retail.demo";
    private const string BobEmail = "bob@retail.demo";

    private static readonly string ApiOrigin = $"http://localhost:{ApiPort}";
    private static readonly string WebOrigin = $"http://localhost:{WebPort}";
    private static readonly string AngularOrigin = $"http://localhost:{AngularPort}";
    // SQL Server under amd64 emulation on an ARM laptop can take several
    // minutes to accept connections; CI is faster. Override locally with
    // HEADLESS_E2E_STARTUP_MINUTES.
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("HEADLESS_E2E_STARTUP_MINUTES"), out var minutes) ? minutes : 8);

    private static DistributedApplication? _app;
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SqlOS_Example_AppHost>(
        [
            $"--Example:ApiPort={ApiPort}",
            $"--Example:WebPort={WebPort}",
            $"--Example:AngularPort={AngularPort}",
            $"--Example:SqlPort={SqlPort}",
            "--Example:EphemeralSql=true",
            "--Example:IncludeTodoStack=false"
        ]);
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        try
        {
            // SQL container start, EF migrations, the retail seed, and both dev
            // servers' cold compiles are slow; poll until everything answers
            // before any browser gets involved.
            using var startup = new CancellationTokenSource(StartupBudget);
            await _app.ResourceNotifications.WaitForResourceAsync("api", KnownResourceStates.Running, startup.Token);
            await WaitForOkAsync($"{ApiOrigin}/sqlos/auth/.well-known/openid-configuration", StartupBudget);
            await WaitForOkAsync($"{ApiOrigin}/api/demo/users", StartupBudget, body => body.Contains(AliceEmail, StringComparison.OrdinalIgnoreCase));
            await WaitForOkAsync($"{WebOrigin}/api/auth/providers", StartupBudget);
            await WaitForOkAsync($"{AngularOrigin}/", StartupBudget);
            // Warm the Next.js pages the journeys hit so the first browser
            // navigation is not the first page compile.
            await WaitForOkAsync($"{WebOrigin}/auth/authorize", TimeSpan.FromMinutes(2));
            await WaitForOkAsync($"{WebOrigin}/retail", TimeSpan.FromMinutes(2));
        }
        catch (Exception ex)
        {
            // Surface each resource's tail so a CI failure explains itself.
            throw new InvalidOperationException(
                $"The Example app host did not become ready within {StartupBudget}.\n{await DumpResourceLogsAsync(_app)}", ex);
        }

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
    public Task NextJs_HeadlessSignup_CreatesAccountAndLandsSignedIn() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();

        // Auth.js starts /authorize; SqlOS redirects into the Next.js headless
        // page with ?request= and the package resumes it.
        await StartHeadlessAsync(page, $"{WebOrigin}/auth/authorize?view=signup", "Start signup flow");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Start your free trial" })).ToBeVisibleAsync();

        await page.GetByLabel("First name").FillAsync("Ada");
        await page.GetByLabel("Last name").FillAsync("Lovelace");
        await page.GetByLabel("Organization").FillAsync($"E2E Org {Guid.NewGuid():N}");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(DemoPassword);
        await page.GetByLabel("How did you hear about us?").SelectOptionAsync("docs");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        // The package hands the code to Auth.js, which finishes /token and
        // lands on the retail dashboard.
        await ExpectSignedInAsync(page, WebOrigin, "Ada");
    });

    [TestMethod]
    public Task NextJs_HeadlessPasswordLogin_ShowsWrongPasswordThenEnrollsMfa_AndLandsSignedIn() => RunWithBrowserAsync(async page =>
    {
        await StartHeadlessAsync(page, $"{WebOrigin}/auth/authorize", "Start sign in flow");

        await page.GetByLabel("Email address").FillAsync(AliceEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        var password = await ChoosePasswordAsync(page);

        // A bad password resolves to status "error" on the flow; the page
        // shows it and stays on the password form (no try/catch involved).
        await password.FillAsync("not-the-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page.Locator(".ha-error, .ha-field-error").First).ToBeVisibleAsync();
        StringAssert.Contains(page.Url, "request=", "a failed password must keep the same authorization request");

        await password.FillAsync(DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        // retail-demo requires MFA: first sign-in enrolls an authenticator.
        // The Next.js panel starts enrollment itself and renders the secret.
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add authenticator app" })).ToBeVisibleAsync();
        var secret = await ReadTotpSecretAsync(page, ".ha-manual-setup code");
        await page.GetByLabel("Verification code").FillAsync(Totp.Now(secret));
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and continue" }).ClickAsync();

        await ExpectSignedInAsync(page, WebOrigin, "Alice");
    });

    [TestMethod]
    public Task NextJs_ErrorOnlyBounce_ShowsTheError() => RunWithBrowserAsync(async page =>
    {
        // SqlOS can bounce back with only ?error= once a request is gone; the
        // page must surface it rather than render a pristine starter card.
        await page.GotoAsync($"{WebOrigin}/auth/authorize?error=access_denied");
        await Assertions.Expect(page.Locator(".ha-error")).ToContainTextAsync("access_denied");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start sign in flow" })).ToBeVisibleAsync();
    });

    [TestMethod]
    public Task Angular_HeadlessPasswordLogin_EnrollsMfa_AndLandsSignedIn() => RunWithBrowserAsync(async page =>
    {
        // angular-oauth2-oidc starts /authorize; SqlOS redirects into the
        // Angular headless route and createHeadlessFlow resumes it.
        await StartHeadlessAsync(page, $"{AngularOrigin}/auth/authorize", "Start sign in flow");

        await page.GetByLabel("Email address").FillAsync(BobEmail);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        var password = await ChoosePasswordAsync(page);
        await password.FillAsync(DemoPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Add authenticator app" })).ToBeVisibleAsync();
        // SqlOS may deliver the enrollment view with the TOTP secret already
        // started; otherwise the Angular page offers a button to start it.
        var startEnrollment = page.GetByRole(AriaRole.Button, new() { Name = "Add authenticator app" });
        await Assertions.Expect(startEnrollment.Or(page.Locator(".ha-mfa-setup code")).First).ToBeVisibleAsync();
        if (await startEnrollment.IsVisibleAsync())
        {
            await startEnrollment.ClickAsync();
        }

        var secret = await ReadTotpSecretAsync(page, ".ha-mfa-setup code");
        await page.GetByLabel("Verification code").FillAsync(Totp.Now(secret));
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify and continue" }).ClickAsync();

        // The package leaves via the registered callback; angular-oauth2-oidc
        // exchanges the code there and the app routes to the dashboard.
        // Angular greets by the ID-token name claim and falls back to the
        // email; Next.js reads userinfo. Either identity proves the session.
        await ExpectSignedInAsync(page, AngularOrigin, "Bob", BobEmail);
    });

    // ---- journey helpers -------------------------------------------------

    private static string NewEmail() => $"e2e-{Guid.NewGuid():N}@example.test";

    /// <summary>
    /// Opens a headless page with no request loaded, clicks its starter button
    /// (which asks the host OIDC library to begin /authorize), and waits for
    /// SqlOS to redirect back with a request id.
    /// </summary>
    private static async Task StartHeadlessAsync(IPage page, string url, string starterButton)
    {
        await page.GotoAsync(url);
        // Buttons are client-side handlers; wait for hydration / discovery to settle.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.GetByRole(AriaRole.Button, new() { Name = starterButton }).ClickAsync();
        await page.WaitForURLAsync(u => u.Contains("/auth/authorize?") && u.Contains("request="), new() { Timeout = 60_000 });
    }

    /// <summary>
    /// After identify, SqlOS routes to the preferred local credential. The
    /// example enables email OTP, so the email-code view comes first and the
    /// UI offers "Use password instead"; take it when present, otherwise the
    /// password view is already showing.
    /// </summary>
    private static async Task<ILocator> ChoosePasswordAsync(IPage page)
    {
        var password = page.GetByLabel("Password", new() { Exact = true });
        var usePassword = page.GetByRole(AriaRole.Button, new() { Name = "Use password instead" });
        await Assertions.Expect(password.Or(usePassword).First).ToBeVisibleAsync();
        if (await usePassword.IsVisibleAsync())
        {
            await usePassword.ClickAsync();
            await Assertions.Expect(password).ToBeVisibleAsync();
        }

        return password;
    }

    /// <summary>
    /// The manual-setup secret sits inside a collapsed &lt;details&gt; on Next.js,
    /// so read textContent rather than innerText.
    /// </summary>
    private static async Task<string> ReadTotpSecretAsync(IPage page, string selector)
    {
        var code = page.Locator(selector).First;
        await Assertions.Expect(code).ToBeAttachedAsync(new() { Timeout = 30_000 });
        var secret = (await code.TextContentAsync())?.Trim();
        Assert.IsFalse(string.IsNullOrWhiteSpace(secret), "expected the TOTP secret to be rendered for manual setup");
        return secret!;
    }

    private static async Task ExpectSignedInAsync(IPage page, string origin, params string[] identities)
    {
        await page.WaitForURLAsync(u => u.StartsWith($"{origin}/retail", StringComparison.Ordinal), new() { Timeout = 90_000 });
        var who = string.Join("|", identities.Select(Regex.Escape));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new()
        {
            NameRegex = new Regex($"^Good (morning|afternoon|evening), ({who})$")
        })).ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToBeVisibleAsync();
    }

    // ---- infrastructure --------------------------------------------------

    private async Task RunWithBrowserAsync(Func<IPage, Task> test)
    {
        Assert.IsNotNull(_browser, "browser fixture was not initialized");
        // Fresh context per test: isolated cookies, so each test owns its
        // AuthPage session and its Auth.js / angular-oauth2-oidc session.
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

    private static async Task<string> DumpResourceLogsAsync(DistributedApplication app)
    {
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        var report = new System.Text.StringBuilder();
        foreach (var resource in new[] { "sql", "api", "web", "angular-web" })
        {
            var lines = new List<string>();
            try
            {
                // GetAllAsync replays the backlog and then waits for more; stop
                // once the replay has drained.
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await foreach (var batch in logs.GetAllAsync(resource).WithCancellation(drain.Token))
                {
                    lines.AddRange(batch.Select(line => line.Content));
                }
            }
            catch (OperationCanceledException)
            {
                // drained
            }
            catch (Exception ex)
            {
                lines.Add($"(could not read logs: {ex.Message})");
            }

            report.AppendLine($"===== {resource} (last {Math.Min(lines.Count, 80)} of {lines.Count} lines) =====");
            foreach (var line in lines.TakeLast(80))
            {
                report.AppendLine(line);
            }
        }

        return report.ToString();
    }

    private static async Task WaitForOkAsync(string url, TimeSpan timeout, Func<string, bool>? bodyPredicate = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastFailure = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    if (bodyPredicate is null || bodyPredicate(await response.Content.ReadAsStringAsync()))
                    {
                        return;
                    }

                    lastFailure = new InvalidOperationException($"{url} answered but the body did not satisfy the readiness check yet");
                }
                else
                {
                    lastFailure = new InvalidOperationException($"{url} returned {(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastFailure = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"{url} did not become ready within {timeout}.", lastFailure);
    }
}
