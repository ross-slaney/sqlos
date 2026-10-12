using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlOS.Todo.E2eTests;

/// <summary>
/// Browser end-to-end coverage for the Todo sample on PostgreSQL. Boots the
/// real Todo app host (Postgres container + Todo API + Razor client) on
/// alternate ports so a manually running demo on 5080/5090 is left untouched,
/// then drives Chromium through hosted AuthPage signup and the signed-in
/// ASP.NET Core session, and runs the real Todo CLI binary through the
/// device-approve journey. Success screenshots land in TestResults for the PR.
/// </summary>
[TestClass]
public sealed class TodoE2eTests
{
    // Deliberately different from the demo's 5080/5090.
    private const int ApiPort = 5180;
    private const int WebPort = 5190;
    private const string Password = "TodoE2ePassword123!";

    internal static readonly string ApiOrigin = $"http://localhost:{ApiPort}";
    private static readonly string WebOrigin = $"http://localhost:{WebPort}";
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("TODO_E2E_STARTUP_MINUTES"), out var minutes) ? minutes : 6);

    private static DistributedApplication? _app;
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext _)
    {
        Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SqlOS_Todo_AppHost>(
        [
            $"--Todo:ApiPort={ApiPort}",
            $"--Todo:WebPort={WebPort}",
            "--Todo:EphemeralSql=true",
            "--SqlOS:DatabaseProvider=PostgreSql"
        ]);
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        try
        {
            using var startup = new CancellationTokenSource(StartupBudget);
            await _app.ResourceNotifications.WaitForResourceAsync("todo-api", KnownResourceStates.Running, startup.Token);
            await _app.ResourceNotifications.WaitForResourceAsync("aspnet-web", KnownResourceStates.Running, startup.Token);
            await WaitForOkAsync($"{ApiOrigin}/sqlos/auth/.well-known/openid-configuration", StartupBudget);
            await WaitForOkAsync($"{ApiOrigin}/sample/config", StartupBudget);
            await WaitForOkAsync($"{WebOrigin}/", StartupBudget);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The Todo app host did not become ready within {StartupBudget}.\n{await DumpResourceLogsAsync(_app)}", ex);
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
    public Task HostedSignup_CreatesAccountAndLandsSignedInOnPostgres() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();

        await page.GotoAsync(WebOrigin);
        await page.GetByRole(AriaRole.Link, new() { Name = "Sign in with SqlOS" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/sqlos/auth/"), new() { Timeout = 60_000 });

        await page.GetByRole(AriaRole.Link, new() { Name = "Get started" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Ship the Todo app first." })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".panel-kicker")).ToHaveTextAsync("Create account");
        await Assertions.Expect(page.GetByLabel("Display name")).ToBeVisibleAsync();
        await CaptureScreenshotAsync(page, "authpage-signup");

        await SubmitSignupFormAsync(page, email);
        await ExpectSignedInAsync(page, email);
        await CaptureScreenshotAsync(page, "signed-in-razor");
    });

    /// <summary>
    /// The terminal journey, driven by the real CLI binary: `login` starts the
    /// device grant and prints the verification URL, the browser creates an
    /// account and approves the device, the CLI observes the approval and
    /// persists tokens, and later CLI commands use those tokens against the API.
    /// </summary>
    [TestMethod]
    public Task CliLogin_ApprovedInBrowser_ThenCliCommandsWork() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();
        var tokenHome = NewTokenHome();
        var todoTitle = $"From the CLI {Guid.NewGuid():N}"[..28];

        await using var login = CliProcess.Start(tokenHome, "login");
        var verificationUrl = await login.WaitForVerificationUrlAsync(TimeSpan.FromSeconds(60));
        StringAssert.StartsWith(verificationUrl, $"{ApiOrigin}/sqlos/auth/device", "the CLI must print the hosted device URL for the e2e API origin");
        Assert.IsFalse(File.Exists(Path.Combine(tokenHome, "tokens.json")), "no tokens may exist before approval");

        await page.GotoAsync(verificationUrl);
        await Assertions.Expect(page.GetByText("Sign in to approve CLI access for Todo CLI.")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Get started" }).ClickAsync();
        await SubmitSignupFormAsync(page, email);

        await Assertions.Expect(page.Locator(".panel-kicker")).ToHaveTextAsync("Approve CLI Access", new() { Timeout = 60_000 });
        await Assertions.Expect(page.GetByText("Todo CLI").First).ToBeVisibleAsync();
        await CaptureScreenshotAsync(page, "authpage-device-approve");
        await page.GetByRole(AriaRole.Button, new() { Name = "Approve CLI access" }).ClickAsync();
        await Assertions.Expect(page.GetByText("CLI access approved.")).ToBeVisibleAsync();
        await CaptureScreenshotAsync(page, "authpage-device-approved");

        var loginResult = await login.WaitForExitAsync(TimeSpan.FromSeconds(90));
        Assert.AreEqual(0, loginResult.ExitCode, $"login should exit 0 after approval.\n{loginResult}");
        StringAssert.Contains(loginResult.StandardOutput, "Signed in.", loginResult.ToString());
        Assert.IsTrue(File.Exists(Path.Combine(tokenHome, "tokens.json")), "login must persist tokens in the configured CLI home");

        var whoami = await CliProcess.RunAsync(tokenHome, "whoami");
        Assert.AreEqual(0, whoami.ExitCode, whoami.ToString());
        StringAssert.Contains(whoami.StandardOutput, email, "whoami must echo the signed-in email from /api/me");

        var add = await CliProcess.RunAsync(tokenHome, "add", todoTitle);
        Assert.AreEqual(0, add.ExitCode, add.ToString());
        StringAssert.Contains(add.StandardOutput, todoTitle, add.ToString());

        var list = await CliProcess.RunAsync(tokenHome, "list");
        Assert.AreEqual(0, list.ExitCode, list.ToString());
        StringAssert.Contains(list.StandardOutput, todoTitle, "list must show the todo created with the device-grant token");

        // Two more todos and a page size of 2: the CLI must follow nextCursor to the second page.
        var moreTitles = new[] { $"{todoTitle} two", $"{todoTitle} three" };
        foreach (var title in moreTitles)
        {
            var addMore = await CliProcess.RunAsync(tokenHome, "add", title);
            Assert.AreEqual(0, addMore.ExitCode, addMore.ToString());
        }

        var paged = await CliProcess.RunAsync(tokenHome, "list", "--page-size", "2");
        Assert.AreEqual(0, paged.ExitCode, paged.ToString());
        var lines = paged.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CollectionAssert.AreEqual(
            new[] { moreTitles[1], moreTitles[0], todoTitle },
            lines.Select(line => line.Split("  ", 2)[^1]).ToArray(),
            "a paged list must show every todo, newest first, across pages");

        var logout = await CliProcess.RunAsync(tokenHome, "logout");
        Assert.AreEqual(0, logout.ExitCode, logout.ToString());
        Assert.IsFalse(File.Exists(Path.Combine(tokenHome, "tokens.json")), "logout must delete the token file");

        var listAfterLogout = await CliProcess.RunAsync(tokenHome, "list");
        Assert.AreEqual(1, listAfterLogout.ExitCode, "commands after logout must fail");
        StringAssert.Contains(listAfterLogout.StandardError, "Not signed in", listAfterLogout.ToString());
    });

    [TestMethod]
    public Task CliLogin_DeniedInBrowser_FailsWithoutTokens() => RunWithBrowserAsync(async page =>
    {
        var email = NewEmail();
        var tokenHome = NewTokenHome();

        await using var login = CliProcess.Start(tokenHome, "login");
        var verificationUrl = await login.WaitForVerificationUrlAsync(TimeSpan.FromSeconds(60));

        await page.GotoAsync(verificationUrl);
        await page.GetByRole(AriaRole.Link, new() { Name = "Get started" }).ClickAsync();
        await SubmitSignupFormAsync(page, email);

        await Assertions.Expect(page.Locator(".panel-kicker")).ToHaveTextAsync("Approve CLI Access", new() { Timeout = 60_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Deny request" }).ClickAsync();
        await Assertions.Expect(page.GetByText("CLI access was denied.")).ToBeVisibleAsync();

        var loginResult = await login.WaitForExitAsync(TimeSpan.FromSeconds(90));
        Assert.AreEqual(1, loginResult.ExitCode, $"login should fail after denial.\n{loginResult}");
        StringAssert.Contains(loginResult.StandardError, "Sign-in was denied in the browser.", loginResult.ToString());
        Assert.IsFalse(File.Exists(Path.Combine(tokenHome, "tokens.json")), "a denied login must not persist tokens");
    });

    // ---- journey helpers -------------------------------------------------

    private static string NewEmail() => $"e2e-{Guid.NewGuid():N}@todo.test";

    private string NewTokenHome()
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlos-todo-cli-e2e", $"{TestContext.TestName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task SubmitSignupFormAsync(IPage page, string email)
    {
        await page.GetByLabel("Display name").FillAsync("Todo E2E");
        await page.GetByLabel("Email").FillAsync(email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("Organization name").FillAsync($"Todo Org {Guid.NewGuid():N}"[..24]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await CompleteOrganizationSelectionIfNeededAsync(page);
    }

    private static async Task CompleteOrganizationSelectionIfNeededAsync(IPage page)
    {
        var organization = page.Locator("button.organization-option").First;
        try
        {
            await organization.WaitForAsync(new() { Timeout = 5_000 });
            await organization.ClickAsync();
        }
        catch (TimeoutException)
        {
            // First-party clients often skip the picker when signup already
            // selected the new organization.
        }
    }

    private static async Task ExpectSignedInAsync(IPage page, string email)
    {
        await page.WaitForURLAsync(
            url => url.StartsWith(WebOrigin, StringComparison.Ordinal),
            new() { Timeout = 90_000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out and revoke" }))
            .ToBeVisibleAsync(new() { Timeout = 60_000 });
        await Assertions.Expect(page.Locator(".identity-card").GetByText(email, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".status-dot")).ToHaveTextAsync("200");
        await Assertions.Expect(page.Locator("pre code")).ToContainTextAsync("tenantResourceId");
        StringAssert.Contains(
            await page.Locator("pre code").InnerTextAsync(),
            email,
            "GET /api/me should echo the signed-in email from PostgreSQL-backed SqlOS.");
    }

    // ---- infrastructure --------------------------------------------------

    private async Task RunWithBrowserAsync(Func<IPage, Task> test)
    {
        Assert.IsNotNull(_browser, "browser fixture was not initialized");
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
            await CaptureScreenshotAsync(page, TestContext.TestName ?? "failure");
            throw;
        }
    }

    private async Task CaptureScreenshotAsync(IPage page, string name)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{name}.png");
            await page.ScreenshotAsync(new() { Path = path, FullPage = true });
            TestContext.WriteLine($"Screenshot: {path} (page URL: {page.Url})");
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"Could not capture screenshot: {ex.Message}");
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
        foreach (var resource in new[] { "sql", "todo-api", "aspnet-web" })
        {
            var lines = new List<string>();
            try
            {
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
