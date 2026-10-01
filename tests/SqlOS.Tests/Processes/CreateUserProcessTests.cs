using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Processes.Identity;

namespace SqlOS.Tests.Processes;

/// <summary>
/// <see cref="CreateUser"/>: the operator's and host's account creation (the admin API, the dashboard,
/// seeds and <c>SqlOSAdminService.CreateUserAsync</c>).
/// </summary>
[TestClass]
public sealed class CreateUserProcessTests
{
    [TestMethod]
    public async Task An_operator_creates_an_account_whose_address_is_unverified()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var email = $"Operator.{Guid.NewGuid():N}@Example.com";

        var user = await harness.Processes.CreateUser().ExecuteAsync(
            new CreateUserCommand("Operator Made", email, IdentityProcessHarness.Password),
            CancellationToken.None);

        harness.Context.ChangeTracker.Clear();
        var stored = await harness.Context.Set<SqlOSUser>().Include(x => x.Emails).Include(x => x.Credentials).SingleAsync(x => x.Id == user.Id);
        stored.DisplayName.Should().Be("Operator Made");
        stored.DefaultEmail.Should().Be(email);
        stored.IsActive.Should().BeTrue();
        stored.Emails.Should().ContainSingle().Which.IsVerified.Should().BeFalse("an operator never proves a mailbox; a later proof claims it");
        stored.Credentials.Should().ContainSingle(credential => credential.Type == "password" && credential.RevokedAt == null);
        (await harness.Context.Set<SqlOSAuditEvent>().CountAsync(x => x.UserId == user.Id || x.ActorId == user.Id))
            .Should().Be(0, "7.2.1 audits no user creation (#415)");
    }

    [TestMethod]
    public async Task An_account_without_a_password_has_no_credential()
    {
        var harness = await IdentityProcessHarness.CreateAsync();

        var user = await harness.Processes.CreateUser().ExecuteAsync(
            new CreateUserCommand("No Password", $"nopw-{Guid.NewGuid():N}@example.com", Password: " "),
            CancellationToken.None);

        (await harness.Context.Set<SqlOSCredential>().CountAsync(x => x.UserId == user.Id)).Should().Be(0);
    }

    [TestMethod]
    public async Task An_address_another_account_owns_in_any_spelling_or_no_address_fails_as_7x_did()
    {
        var harness = await IdentityProcessHarness.CreateAsync();
        var email = $"taken-{Guid.NewGuid():N}@example.com";
        await harness.Processes.CreateUser().ExecuteAsync(new CreateUserCommand("First", email, null), CancellationToken.None);

        var taken = async () => await harness.Processes.CreateUser().ExecuteAsync(
            new CreateUserCommand("Second", email.ToUpperInvariant(), null), CancellationToken.None);
        await taken.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Email '{email.ToUpperInvariant()}' already exists.");

        var invalid = async () => await harness.Processes.CreateUser().ExecuteAsync(
            new CreateUserCommand("Invalid", "not-an-address", null), CancellationToken.None);
        (await invalid.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotBeNullOrWhiteSpace();
        (await harness.Context.Set<SqlOSUser>().CountAsync()).Should().Be(1);
    }
}
