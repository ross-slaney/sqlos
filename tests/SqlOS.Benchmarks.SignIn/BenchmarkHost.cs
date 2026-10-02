using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Host.Fakes;
using SqlOS.BehaviorLock.Host.Profiles;

namespace SqlOS.Benchmarks.SignIn;

/// <summary>
/// The behavior-lock host in its <c>hosted</c> profile (a single application with the hosted
/// AuthPage and every first factor), served in-process by TestServer on the run's database, with
/// the behavior lock's fakes for email and every other outbound seam.
/// </summary>
internal sealed partial class BenchmarkHost : IAsyncDisposable
{
    /// <summary>Every benchmark account's password. The password policy only asks that it is not blank.</summary>
    public const string Password = "Sign-In-Benchmark-2468!";

    private readonly WebApplication _app;

    private BenchmarkHost(WebApplication app, string schema, TimeSpan emailCodeResendCooldown)
    {
        _app = app;
        Schema = schema;
        EmailCodeResendCooldown = emailCodeResendCooldown;
        Client = new HttpClient(app.GetTestServer().CreateHandler()) { BaseAddress = new Uri(BehaviorLockConstants.PublicOrigin) };
        Fakes = app.Services.GetRequiredService<BehaviorLockFakes>();
    }

    public HttpClient Client { get; }

    public BehaviorLockFakes Fakes { get; }

    /// <summary>The database schema SqlOS's tables live in.</summary>
    public string Schema { get; }

    /// <summary>How long SqlOS refuses a new code for the address and client it just sent one to.</summary>
    public TimeSpan EmailCodeResendCooldown { get; }

    public static async Task<BenchmarkHost> StartAsync(DatabaseProvider provider, string connectionString, CancellationToken cancellationToken)
    {
        string? schema = null;
        var resendCooldown = TimeSpan.Zero;
        var app = BehaviorLockHost.Build(new BehaviorLockHostOptions
        {
            Profile = HostProfiles.Hosted,
            Provider = provider,
            ConnectionString = connectionString,
            ConfigureSqlOS = sqlos =>
            {
                // Each iteration is a new account at a new client address, so the per-address and
                // per-IP send limits never bind. Every iteration shares the one client, whose hourly
                // email-code limit (300) would refuse a run partway through.
                sqlos.AuthServer.EmailOtp.MaxChallengesPerClientPerHour = 1_000_000;
                schema = sqlos.AuthServer.Schema;
                resendCooldown = sqlos.AuthServer.EmailOtp.ResendCooldown;
            }
        });
        try
        {
            await app.StartAsync(cancellationToken);
            return new BenchmarkHost(app, schema ?? throw new InvalidOperationException("SqlOS was never configured."), resendCooldown);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Creates <paramref name="count"/> accounts through the admin API, as an operator would, in
    /// parallel. Account creation is setup: it happens before any sign-in is measured.
    /// </summary>
    public async Task<IReadOnlyList<BenchmarkUser>> CreateUsersAsync(string pool, int count, bool withPassword, CancellationToken cancellationToken)
    {
        var users = new BenchmarkUser[count];
        var parallelism = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2),
            CancellationToken = cancellationToken
        };
        await Parallel.ForAsync(0, count, parallelism, async (index, token) =>
        {
            var email = $"{pool}-{index:D5}@signin.example.test";
            var displayName = $"Sign-in {pool} {index}";
            object body = withPassword ? new { displayName, email, password = Password } : new { displayName, email };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sqlos/admin/auth/api/users") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation(BehaviorLockConstants.OperatorHeader, BehaviorLockConstants.OperatorSecret);
            using var response = await Client.SendAsync(request, token);
            var text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Creating {email} failed: {(int)response.StatusCode} {text}");
            }

            var id = JsonNode.Parse(text)?["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Creating {email} returned no id: {text}");
            users[index] = new BenchmarkUser(id, email, withPassword ? Password : null);
        });
        return users;
    }

    /// <summary>The six-digit code in the latest email to <paramref name="email"/> after effect <paramref name="since"/>.</summary>
    public string EmailedCode(string email, long since)
    {
        var message = Fakes.Effects.Since(since)
            .OfType<EmailEffect>()
            .LastOrDefault(effect => string.Equals(effect.To, email, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No email reached {email}.");
        var code = SixDigits().Match(message.TextBody ?? string.Empty);
        return code.Success ? code.Value : throw new InvalidOperationException($"No code in the email to {email}: {message.TextBody}");
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }

    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)")]
    private static partial Regex SixDigits();
}

/// <param name="Id">The account's ID.</param>
/// <param name="Email">Its only address.</param>
/// <param name="Password">Its password, or null for an account that signs in with codes only.</param>
internal sealed record BenchmarkUser(string Id, string Email, string? Password);
