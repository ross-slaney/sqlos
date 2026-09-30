using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.IntegrationTests.Infrastructure;

namespace SqlOS.IntegrationTests;

/// <summary>
/// #422: an email key must never select another person's account because SQL collation or .NET
/// casing treats two different addresses as equal, and a login code or link for an existing
/// account is delivered only to the address stored on that account.
/// </summary>
[TestClass]
public sealed class EmailLookAlikeIntegrationTests
{
    private static EmailOwnershipServer _server = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext _)
    {
        _server = await EmailOwnershipServer.CreateAsync("EmailLookAlike");
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    public static IEnumerable<object[]> LookAlikes()
    {
        // SQL Server's default CI collation expands these to their ASCII pairs.
        yield return ["bob{0}@business.example", "bob{0}@busineß.example"];
        yield return ["bob{0}@github.example", "bob{0}@giþub.example"];
        yield return ["bob{0}@maeil.example", "bob{0}@mæil.example"];
        yield return ["bob{0}@oeil.example", "bob{0}@œil.example"];
        // .NET ToUpperInvariant folds the long s to S on every provider.
        yield return ["bos{0}@example.test", "boſ{0}@example.test"];
        // The Kelvin sign is canonically equivalent to K.
        yield return ["ken{0}@example.test", "Ken{0}@example.test"];
    }

    [DataTestMethod]
    [DynamicData(nameof(LookAlikes), DynamicDataSourceType.Method)]
    public async Task EmailOtpStart_ForLookAlikeAddress_NeverBindsOrDeliversTheVictimsCode(string victimFormat, string lookAlikeFormat)
    {
        var (victim, victimEmail, lookAlike) = await CreateVictimAsync(victimFormat, lookAlikeFormat);

        await using (var scope = _server.CreateScope())
        {
            SqlOSEmailOtpStartResult? login = null;
            try
            {
                login = await scope.Auth.RequestEmailOtpAsync(
                    new SqlOSEmailOtpStartRequest(lookAlike, EmailOwnershipServer.ClientId, null),
                    EmailOwnershipServer.HttpContext());
            }
            catch (InvalidOperationException)
            {
            }

            SqlOSEmailOtpSignupStartResult? signup = null;
            try
            {
                signup = await scope.Auth.RequestEmailOtpSignupAsync(
                    new SqlOSEmailOtpSignupStartRequest("Look-alike", lookAlike, EmailOwnershipServer.ClientId, null, null, null),
                    EmailOwnershipServer.HttpContext());
            }
            catch (InvalidOperationException)
            {
            }

            foreach (var message in _server.MessagesTo(lookAlike))
            {
                var code = EmailOwnershipServer.ExtractCode(message);
                foreach (var challengeToken in new[] { login?.ChallengeToken, signup?.ChallengeToken }.Where(x => x != null))
                {
                    try
                    {
                        var result = await scope.Auth.VerifyEmailOtpAsync(
                            new SqlOSEmailOtpVerifyRequest(challengeToken!, code),
                            EmailOwnershipServer.HttpContext());
                        (await _server.SessionUserIdAsync(result.Tokens!))
                            .Should().NotBe(victim.Id, "a code delivered to a look-alike mailbox must never sign in as the victim");
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSEmailOtpChallenge>().CountAsync(x => x.UserId == victim.Id))
            .Should().Be(0, "a look-alike address must not produce a challenge bound to {0}", victimEmail);
        // The look-alike is a different mailbox: at most it may receive a sign-up code for itself,
        // never a sign-in code for the victim's account.
        _server.MessagesTo(lookAlike)
            .Where(x => !x.Subject.Contains("sign-up", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("the victim's sign-in code must never go to a look-alike mailbox");
    }

    [DataTestMethod]
    [DynamicData(nameof(LookAlikes), DynamicDataSourceType.Method)]
    public async Task MagicLinkStart_ForLookAlikeAddress_NeverBindsOrDeliversTheVictimsLink(string victimFormat, string lookAlikeFormat)
    {
        var (victim, victimEmail, lookAlike) = await CreateVictimAsync(victimFormat, lookAlikeFormat);

        await using (var scope = _server.CreateScope())
        {
            try
            {
                await scope.Auth.RequestMagicLinkAsync(
                    new SqlOSMagicLinkStartRequest(lookAlike, EmailOwnershipServer.ClientId, null),
                    EmailOwnershipServer.HttpContext());
            }
            catch (InvalidOperationException)
            {
            }

            foreach (var message in _server.MessagesTo(lookAlike))
            {
                var result = await scope.Auth.CompleteMagicLinkAsync(
                    new SqlOSMagicLinkCompleteRequest(EmailOwnershipServer.ExtractToken(message)),
                    EmailOwnershipServer.HttpContext());
                (await _server.SessionUserIdAsync(result.Tokens!))
                    .Should().NotBe(victim.Id, "a link delivered to a look-alike mailbox must never sign in as the victim");
            }
        }

        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSTemporaryToken>()
                .CountAsync(x => x.Purpose == SqlOSMagicLinkService.TokenPurpose && x.UserId == victim.Id))
            .Should().Be(0, "a look-alike address must not produce a link bound to {0}", victimEmail);
        _server.MessagesTo(lookAlike).Should().BeEmpty("the victim's link must never go to a look-alike mailbox");
    }

    [DataTestMethod]
    [DynamicData(nameof(LookAlikes), DynamicDataSourceType.Method)]
    public async Task VerifiedOidcEmail_ForLookAlikeAddress_DoesNotAutoLinkTheVictim(string victimFormat, string lookAlikeFormat)
    {
        var (victim, _, lookAlike) = await CreateVictimAsync(victimFormat, lookAlikeFormat);

        try
        {
            var result = await _server.CompleteGoogleLoginAsync(lookAlike);
            result.UserId.Should().NotBe(victim.Id, "an upstream-verified look-alike address must not auto-link the victim");
        }
        catch (InvalidOperationException)
        {
        }

        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSExternalIdentity>().CountAsync(x => x.UserId == victim.Id)).Should().Be(0);
    }

    [DataTestMethod]
    [DynamicData(nameof(LookAlikes), DynamicDataSourceType.Method)]
    public async Task PasswordLogin_ForLookAlikeAddress_DoesNotResolveTheVictim(string victimFormat, string lookAlikeFormat)
    {
        var (victim, _, lookAlike) = await CreateVictimAsync(victimFormat, lookAlikeFormat);
        await using var scope = _server.CreateScope();

        try
        {
            var result = await scope.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(lookAlike, EmailOwnershipServer.Password, EmailOwnershipServer.ClientId, null),
                EmailOwnershipServer.HttpContext());
            (await _server.SessionUserIdAsync(result.Tokens!))
                .Should().NotBe(victim.Id, "a look-alike address must not resolve the victim's password account");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task ScimCreate_ForLookAlikeAddress_DoesNotLinkTheVictim()
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        var victimEmail = $"bob{unique}@business.example";
        var lookAlike = $"bob{unique}@busineß.example";
        var victim = await _server.CreateUserAsync(victimEmail);
        var organization = await _server.CreateOrganizationAsync($"Look-alike tenant {unique}", null, "xn--busine-gta.example");
        var (_, token) = await _server.CreateScimConnectionAsync(organization.Id);

        using var response = await _server.SendScimAsync(
            token,
            HttpMethod.Post,
            "/Users",
            EmailOwnershipServer.ScimUser($"ext-{unique}", lookAlike, lookAlike, "Look-alike"));

        await using var verification = _server.CreateVerificationContext();
        (await verification.Set<SqlOSMembership>().CountAsync(x => x.UserId == victim.Id && x.OrganizationId == organization.Id))
            .Should().Be(0, "a directory user at a look-alike domain must not be linked onto the victim");
        (await verification.Set<SqlOSScimExternalId>().CountAsync(x => x.EntityId == victim.Id)).Should().Be(0);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
            created["id"]!.GetValue<string>().Should().NotBe(victim.Id);
        }
    }

    [TestMethod]
    public async Task LoginCodeAndLink_ForCaseAndWhitespaceVariants_ReachTheStoredAddressOnly()
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        var storedEmail = $"mixed{unique}@case.example";
        var typedEmail = $"  MIXED{unique.ToUpperInvariant()}@CASE.EXAMPLE  ";
        var owner = await _server.CreateUserAsync(storedEmail);

        await using var scope = _server.CreateScope();
        var started = await scope.Auth.RequestEmailOtpAsync(
            new SqlOSEmailOtpStartRequest(typedEmail, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var codeMessage = _server.MessagesTo(storedEmail).Should().ContainSingle(
            "the code for an existing account goes to the stored address, not the typed spelling").Subject;
        _server.Emails.Messages.Should().NotContain(x => x.To == typedEmail.Trim());
        var login = await scope.Auth.VerifyEmailOtpAsync(
            new SqlOSEmailOtpVerifyRequest(started.ChallengeToken, EmailOwnershipServer.ExtractCode(codeMessage)),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(login.Tokens!)).Should().Be(owner.Id);

        await scope.Auth.RequestMagicLinkAsync(
            new SqlOSMagicLinkStartRequest(typedEmail, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var linkMessage = _server.MessagesTo(storedEmail).Should().HaveCount(2).And.Subject.Last();
        var completed = await scope.Auth.CompleteMagicLinkAsync(
            new SqlOSMagicLinkCompleteRequest(EmailOwnershipServer.ExtractToken(linkMessage)),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(completed.Tokens!)).Should().Be(owner.Id);
    }

    [TestMethod]
    public async Task InternationalizedMailbox_CanSignUpAndSignInWithEitherDomainSpelling()
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        var unicodeEmail = $"ü{unique}@münchen.example";
        var punycodeEmail = $"ü{unique}@xn--mnchen-3ya.example";

        await using var scope = _server.CreateScope();
        var signup = await scope.Auth.SignUpAsync(
            new SqlOSSignupRequest("Umlaut", unicodeEmail, EmailOwnershipServer.Password, null, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var userId = await _server.SessionUserIdAsync(signup.Tokens!);

        foreach (var spelling in new[] { unicodeEmail, punycodeEmail })
        {
            var login = await scope.Auth.LoginWithPasswordAsync(
                new SqlOSPasswordLoginRequest(spelling, EmailOwnershipServer.Password, EmailOwnershipServer.ClientId, null),
                EmailOwnershipServer.HttpContext());
            (await _server.SessionUserIdAsync(login.Tokens!)).Should().Be(userId);
        }

        var started = await scope.Auth.RequestEmailOtpAsync(
            new SqlOSEmailOtpStartRequest(punycodeEmail, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        var message = _server.MessagesTo(unicodeEmail).Should().ContainSingle().Subject;
        var verified = await scope.Auth.VerifyEmailOtpAsync(
            new SqlOSEmailOtpVerifyRequest(started.ChallengeToken, EmailOwnershipServer.ExtractCode(message)),
            EmailOwnershipServer.HttpContext());
        (await _server.SessionUserIdAsync(verified.Tokens!)).Should().Be(userId);

        var duplicate = async () => await scope.Auth.SignUpAsync(
            new SqlOSSignupRequest("Duplicate", punycodeEmail, EmailOwnershipServer.Password, null, EmailOwnershipServer.ClientId, null),
            EmailOwnershipServer.HttpContext());
        await duplicate.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task<(SqlOSUser Victim, string VictimEmail, string LookAlike)> CreateVictimAsync(string victimFormat, string lookAlikeFormat)
    {
        var unique = Guid.NewGuid().ToString("N")[..12];
        var victimEmail = string.Format(victimFormat, unique);
        var lookAlike = string.Format(lookAlikeFormat, unique);
        var victim = await _server.CreateUserAsync(victimEmail);
        return (victim, victimEmail, lookAlike);
    }
}
