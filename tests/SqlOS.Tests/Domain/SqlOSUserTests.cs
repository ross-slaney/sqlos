using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Policies;
using SqlOS.Domain;
using SqlOS.Domain.Events;
using SqlOS.Tests.Infrastructure;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

/// <summary>
/// Every rule of the User aggregate (<c>docs/architecture/domain-model.md</c> §4), without a
/// database: registration, the one primary address, verification only with a proof, the claim,
/// the password choke point, phones, authenticators, recovery codes, identities and lifecycle.
/// </summary>
[TestClass]
public sealed class SqlOSUserTests
{
    private static readonly TotpParameters Totp = new("SHA1", 6, 30);
    private static readonly ExternalIdentityLink Google = ExternalIdentityLink.Oidc("oidc_google", "Google", "https://accounts.google.com", "google-sub", "alice@example.test");
    private static readonly ExternalIdentityLink Acme = ExternalIdentityLink.Saml("sso_acme", "urn:acme", "acme-sub", "alice@example.test");

    // ---- Registration ---------------------------------------------------------------------------

    [TestMethod]
    public void Registering_without_an_address_creates_an_active_account_with_nothing_attached()
    {
        var user = SqlOSUser.Register("Alice", Now);

        user.Id.Should().MatchRegex("^usr_[0-9a-f]{24}$");
        user.DisplayName.Should().Be("Alice");
        user.DefaultEmail.Should().BeNull();
        user.IsActive.Should().BeTrue();
        user.CreatedAt.Should().Be(Now);
        user.UpdatedAt.Should().Be(Now);
        user.Emails.Should().BeEmpty();
        user.LoadedParts.Should().Be(SqlOSUserParts.All, "a new account holds everything it has");
        Events(user).Should().Equal(new UserRegistered(user.Id));
    }

    [TestMethod]
    public void Registering_with_an_address_stores_it_unverified_primary_and_default()
    {
        var user = SqlOSUser.Register("Alice", EmailAddress.Parse("  Alice@Example.TEST "), Now);

        var email = user.Emails.Should().ContainSingle().Subject;
        email.Id.Should().MatchRegex("^eml_[0-9a-f]{24}$");
        email.UserId.Should().Be(user.Id);
        email.Email.Should().Be("Alice@Example.TEST", "the stored form is trimmed, as 7.x stored it");
        email.NormalizedEmail.Should().Be("ALICE@EXAMPLE.TEST");
        email.IsPrimary.Should().BeTrue();
        email.IsVerified.Should().BeFalse("nobody proved the mailbox");
        email.VerifiedAt.Should().BeNull();
        email.CreatedAt.Should().Be(Now);
        user.DefaultEmail.Should().Be("Alice@Example.TEST");
        Events(user).Should().Equal(new UserRegistered(user.Id), new UserEmailAdded(user.Id, email.Id, Verified: false));
    }

    [TestMethod]
    public void Registering_with_a_proof_verifies_the_address_from_the_start()
    {
        var user = SqlOSUser.Register("Alice", Proof("alice@example.test", OwnershipProofMethod.EmailOtp), Now);

        var email = user.Emails.Should().ContainSingle().Subject;
        email.IsVerified.Should().BeTrue();
        email.VerifiedAt.Should().Be(Now);
        email.IsPrimary.Should().BeTrue();
        user.DefaultEmail.Should().Be("alice@example.test");
        Events(user).Should().Equal(new UserRegistered(user.Id), new UserEmailAdded(user.Id, email.Id, Verified: true));
    }

    [TestMethod]
    public void An_upstream_identity_registers_its_account_and_an_openid_provider_audits_the_provisioning()
    {
        var unverified = SqlOSUser.RegisterFromExternalIdentity("Alice", EmailAddress.Parse("alice@example.test"), Google, Now);
        var identity = unverified.ExternalIdentities.Should().ContainSingle().Subject;
        identity.Id.Should().MatchRegex("^ext_[0-9a-f]{24}$");
        identity.UserId.Should().Be(unverified.Id);
        identity.OidcConnectionId.Should().Be("oidc_google");
        identity.SsoConnectionId.Should().BeNull();
        identity.Issuer.Should().Be("https://accounts.google.com");
        identity.Subject.Should().Be("google-sub");
        identity.Email.Should().Be("alice@example.test");
        identity.CreatedAt.Should().Be(Now);
        unverified.Emails.Single().IsVerified.Should().BeFalse("a custom provider may not verify the address");
        Events(unverified).Should().ContainSingle(@event => @event is UserProvisionedFromOidc)
            .Which.Should().Be(new UserProvisionedFromOidc(unverified.Id, "Google", "oidc_google"));

        var jit = SqlOSUser.RegisterFromExternalIdentity("Alice", Proof("alice@example.test", OwnershipProofMethod.Saml), Acme, Now);
        jit.ExternalIdentities.Single().SsoConnectionId.Should().Be("sso_acme");
        jit.Emails.Single().IsVerified.Should().BeTrue();
        Events(jit).Should().NotContain(@event => @event is UserProvisionedFromOidc, "7.2.1 audits no SAML provisioning (#415)");
    }

    [TestMethod]
    public void An_upstream_identity_registers_only_with_its_own_providers_proof()
    {
        FluentActions.Invoking(() => SqlOSUser.RegisterFromExternalIdentity("Alice", Proof("alice@example.test", OwnershipProofMethod.Saml), Google, Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
    }

    // ---- Loading -------------------------------------------------------------------------------

    [TestMethod]
    public void A_rule_refuses_to_decide_with_a_part_that_was_not_loaded()
    {
        var stored = TestRows.Create<SqlOSUser>(new { Id = "usr_stored", DisplayName = "Stored", IsActive = true });
        stored.LoadedParts.Should().Be(SqlOSUserParts.None);

        FluentActions.Invoking(() => stored.SetPassword("Secret-1", PasswordPolicy.Default, Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AggregatePartNotLoaded);
        FluentActions.Invoking(() => stored.IsClaimableWith(Proof("alice@example.test", OwnershipProofMethod.EmailOtp)))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.AggregatePartNotLoaded);

        stored.MarkLoaded(SqlOSUserParts.Emails);
        FluentActions.Invoking(() => stored.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.EmailOtp), PresentedCredentials.None, Evictions(), Now))
            .Should().Throw<SqlOSDomainException>().Which.Message.Should().Contain("PhoneNumbers", "a claim must see every member it evicts");
    }

    // ---- Addresses -----------------------------------------------------------------------------

    [TestMethod]
    public void A_proof_for_another_mailbox_never_touches_the_account()
    {
        var user = Account();

        FluentActions.Invoking(() => user.EmailProvenBy(Proof("bob@example.test", OwnershipProofMethod.EmailOtp)))
            .Should().Throw<InvalidOperationException>().WithMessage("The ownership proof is for another mailbox.");
        user.EmailProvenBy(Proof("  ALICE@example.TEST ", OwnershipProofMethod.EmailOtp)).Should().BeSameAs(user.Emails.Single(), "every spelling of the mailbox is the mailbox");
    }

    [TestMethod]
    public void The_verification_link_verifies_without_claiming_and_makes_the_address_the_default()
    {
        var user = Account();
        var password = user.SetPassword("Owner-Password-1", PasswordPolicy.Default, Now);
        Drain(user);

        user.VerifyEmail(Proof("alice@example.test", OwnershipProofMethod.EmailVerification), Now.AddMinutes(1));

        var email = user.Emails.Single();
        email.IsVerified.Should().BeTrue();
        email.VerifiedAt.Should().Be(Now.AddMinutes(1));
        password.RevokedAt.Should().BeNull("confirming the address an account signed up with evicts nothing");
        user.UpdatedAt.Should().Be(Now.AddMinutes(1));
        Events(user).Should().Equal(new UserEmailVerified(user.Id, email.Id));

        user.VerifyEmail(Proof("alice@example.test", OwnershipProofMethod.EmailVerification), Now.AddMinutes(2));
        email.VerifiedAt.Should().Be(Now.AddMinutes(1), "an address keeps the time it was first proven");
        Events(user).Should().Equal(new UserEmailVerified(user.Id, email.Id));
    }

    [DataTestMethod]
    [DataRow((int)OwnershipProofMethod.EmailOtp)]
    [DataRow((int)OwnershipProofMethod.MagicLink)]
    [DataRow((int)OwnershipProofMethod.Oidc)]
    [DataRow((int)OwnershipProofMethod.Directory)]
    public void Only_the_verification_link_verifies_without_a_claim(int proofMethod)
    {
        var method = (OwnershipProofMethod)proofMethod;
        var user = Account();

        FluentActions.Invoking(() => user.VerifyEmail(Proof("alice@example.test", method), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
        user.Emails.Single().IsVerified.Should().BeFalse();
    }

    [TestMethod]
    public void A_sign_in_proof_makes_its_address_the_default_email()
    {
        var user = SqlOSUser.Register("Alice", Now);
        user.SetPrimaryEmail(Proof("alice@example.test", OwnershipProofMethod.Directory), Now);
        Drain(user);

        user.MakeDefaultEmail(Proof("ALICE@example.test", OwnershipProofMethod.MagicLink), Now.AddMinutes(1));

        user.DefaultEmail.Should().Be("alice@example.test", "the stored address, never the spelling of the proof");
        user.UpdatedAt.Should().Be(Now.AddMinutes(1));
        Events(user).Should().Equal(new UserProfileChanged(user.Id));
        user.MakeDefaultEmail(Proof("alice@example.test", OwnershipProofMethod.MagicLink), Now.AddMinutes(2));
        Events(user).Should().BeEmpty("an unchanged default is no change");
    }

    [TestMethod]
    public void A_directory_sets_one_verified_primary_address()
    {
        var user = Account();
        var first = user.Emails.Single();
        Drain(user);

        var second = user.SetPrimaryEmail(Proof("alice.new@example.test", OwnershipProofMethod.Directory), Now.AddMinutes(1));

        second.Should().NotBeSameAs(first);
        second.IsPrimary.Should().BeTrue();
        second.IsVerified.Should().BeTrue();
        second.VerifiedAt.Should().Be(Now.AddMinutes(1));
        first.IsPrimary.Should().BeFalse("one address is primary");
        user.Emails.Count(email => email.IsPrimary).Should().Be(1);
        Events(user).Should().Equal(new UserEmailAdded(user.Id, second.Id, Verified: true), new UserPrimaryEmailSet(user.Id, second.Id));

        var again = user.SetPrimaryEmail(Proof("ALICE@example.test", OwnershipProofMethod.Directory), Now.AddMinutes(2));
        again.Should().BeSameAs(first, "the account already has that mailbox");
        first.Email.Should().Be("ALICE@example.test", "stored as the directory spelled it");
        first.NormalizedEmail.Should().Be("ALICE@EXAMPLE.TEST");
        first.IsPrimary.Should().BeTrue();
        first.IsVerified.Should().BeTrue();
        second.IsPrimary.Should().BeFalse();
        user.Emails.Count(email => email.IsPrimary).Should().Be(1);
    }

    [TestMethod]
    public void Only_a_directory_sets_the_primary_address()
    {
        FluentActions.Invoking(() => Account().SetPrimaryEmail(Proof("alice@example.test", OwnershipProofMethod.EmailOtp), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
    }

    // ---- The claim -----------------------------------------------------------------------------

    [TestMethod]
    public void The_first_sign_in_proof_of_an_unverified_address_evicts_everything_attached_before_it()
    {
        var squatted = Squatted();
        Drain(squatted.User);

        var claim = squatted.User.ClaimWithProof(
            Proof("alice@example.test", OwnershipProofMethod.EmailOtp),
            PresentedCredentials.None,
            Evictions(["cgr_1"], ["cal_1"]),
            Now.AddMinutes(5));

        var at = Now.AddMinutes(5);
        claim.Email.Should().BeSameAs(squatted.User.Emails.Single());
        claim.Email.IsVerified.Should().BeTrue();
        claim.Email.VerifiedAt.Should().Be(at);
        squatted.Password.RevokedAt.Should().Be(at);
        squatted.Authenticator.RevokedAt.Should().Be(at);
        squatted.Authenticator.RevocationReason.Should().Be("email_claimed");
        squatted.RecoveryCodes.Should().OnlyContain(code => code.RevokedAt == at);
        squatted.Phone.RemovedAt.Should().Be(at);
        squatted.Phone.RemovalReason.Should().Be("email_claimed");
        squatted.Phone.UpdatedAt.Should().Be(at);
        claim.UnlinkedIdentities.Should().BeEquivalentTo(squatted.User.ExternalIdentities, "every identity attached before the proof is unlinked");
        squatted.User.ExternalIdentities.Should().HaveCount(2, "their rows go when the claim process deletes them in the save");
        Events(squatted.User).Should().ContainSingle().Which.Should().BeOfType<UserEmailClaimed>()
            .Which.Should().BeEquivalentTo(
                new UserEmailClaimed(
                    squatted.User.Id,
                    claim.Email.Id,
                    "email_otp",
                    [squatted.Password.Id],
                    [squatted.Authenticator.Id],
                    squatted.RecoveryCodes.Count,
                    [squatted.Phone.Id],
                    [
                        new UnlinkedExternalIdentity(squatted.Oidc.Id, "oidc", "oidc_google"),
                        new UnlinkedExternalIdentity(squatted.Saml.Id, "saml", "sso_acme")
                    ],
                    ["cgr_1"],
                    ["cal_1"],
                    at),
                options => options.WithStrictOrdering());
    }

    [TestMethod]
    public void A_claim_keeps_exactly_what_the_proving_flow_presented()
    {
        var reset = Squatted();
        reset.User.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.PasswordReset), PresentedCredentials.PasswordBeingReset(reset.Password.Id), Evictions(), Now);
        reset.Password.RevokedAt.Should().BeNull("the password being reset is kept and replaced");
        reset.Authenticator.RevokedAt.Should().NotBeNull();

        var invitation = Squatted();
        var claim = invitation.User.ClaimWithProof(
            Proof("alice@example.test", OwnershipProofMethod.Invitation),
            PresentedCredentials.FromAuthenticationMethod("password+totp"),
            Evictions(),
            Now);
        invitation.Password.RevokedAt.Should().BeNull();
        invitation.Authenticator.RevokedAt.Should().BeNull();
        invitation.RecoveryCodes.Should().OnlyContain(code => code.RevokedAt == null, "recovery codes belong with the authenticator");
        invitation.Phone.RemovedAt.Should().NotBeNull();
        claim.UnlinkedIdentities.Should().HaveCount(2);

        var federated = Squatted();
        var federatedClaim = federated.User.ClaimWithProof(
            Proof("alice@example.test", OwnershipProofMethod.Saml),
            PresentedCredentials.FromAuthenticationMethod("saml+phone_otp"),
            Evictions(),
            Now);
        federatedClaim.UnlinkedIdentities.Should().ContainSingle().Which.Should().BeSameAs(federated.Oidc);
        federated.Phone.RemovedAt.Should().BeNull();
    }

    [TestMethod]
    public void A_claim_evicts_only_what_is_still_live()
    {
        var squatted = Squatted();
        var spent = squatted.RecoveryCodes[0];
        squatted.User.UseRecoveryCode(RawRecoveryCode(0), Now).Should().BeTrue();
        Drain(squatted.User);

        squatted.User.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.MagicLink), PresentedCredentials.None, Evictions(), Now.AddMinutes(1));

        spent.RevokedAt.Should().BeNull("a spent code is not revoked again");
        Events(squatted.User).OfType<UserEmailClaimed>().Single().RecoveryCodes.Should().Be(squatted.RecoveryCodes.Count - 1);
    }

    [TestMethod]
    public void A_verified_address_is_never_claimed_again()
    {
        var user = SqlOSUser.Register("Alice", Proof("alice@example.test", OwnershipProofMethod.EmailOtp), Now);

        user.IsClaimableWith(Proof("alice@example.test", OwnershipProofMethod.MagicLink)).Should().BeFalse();
        FluentActions.Invoking(() => user.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.MagicLink), PresentedCredentials.None, Evictions(), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.InvalidMemberState);
    }

    [DataTestMethod]
    [DataRow((int)OwnershipProofMethod.EmailVerification)]
    [DataRow((int)OwnershipProofMethod.Directory)]
    public void Confirmations_and_directories_never_claim(int proofMethod)
    {
        var method = (OwnershipProofMethod)proofMethod;
        var squatted = Squatted();

        FluentActions.Invoking(() => squatted.User.ClaimWithProof(Proof("alice@example.test", method), PresentedCredentials.None, Evictions(), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
        squatted.Password.RevokedAt.Should().BeNull();
    }

    [TestMethod]
    public void A_claim_with_a_proof_for_another_mailbox_evicts_nothing()
    {
        var squatted = Squatted();

        FluentActions.Invoking(() => squatted.User.ClaimWithProof(Proof("mallory@example.test", OwnershipProofMethod.EmailOtp), PresentedCredentials.None, Evictions(), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
        squatted.Password.RevokedAt.Should().BeNull();
        squatted.User.Emails.Single().IsVerified.Should().BeFalse();
    }

    // ---- Password ------------------------------------------------------------------------------

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\t\n")]
    public void A_blank_password_is_never_stored(string? password)
    {
        var user = Account();

        FluentActions.Invoking(() => user.SetPassword(password!, PasswordPolicy.Default, Now))
            .Should().Throw<SqlOSDomainException>()
            .Which.Should().Match<SqlOSDomainException>(exception => exception.Error == SqlOSDomainError.PasswordRejected
                && exception.Message == "Password is required.");
        user.Credentials.Should().BeEmpty();
    }

    [TestMethod]
    public void Setting_a_password_adds_one_then_replaces_it()
    {
        var user = Account();
        Drain(user);

        var first = user.SetPassword("First-Password-1", PasswordPolicy.Default, Now);

        first.Id.Should().MatchRegex("^cred_[0-9a-f]{24}$");
        first.Type.Should().Be("password");
        first.SecretVersion.Should().Be(1);
        first.CreatedAt.Should().Be(Now);
        Verifies(first.SecretHash, "First-Password-1").Should().BeTrue("the PBKDF2 hash 7.x stores");
        first.SecretHash.Should().NotContain("First-Password-1");
        Events(user).Should().Equal(new UserPasswordSet(user.Id, first.Id));

        user.RecordPasswordSignIn(first.Id, Now.AddMinutes(1));
        first.LastUsedAt.Should().Be(Now.AddMinutes(1));
        Events(user).Should().Equal(new UserPasswordUsed(user.Id, first.Id));

        var second = user.SetPassword("Second-Password-2", PasswordPolicy.Default, Now.AddMinutes(2));
        second.Should().BeSameAs(first, "a reset replaces the active password");
        Verifies(second.SecretHash, "Second-Password-2").Should().BeTrue();
        Verifies(second.SecretHash, "First-Password-1").Should().BeFalse();
        second.LastUsedAt.Should().BeNull("the new password has not signed in yet");
        user.Credentials.Should().ContainSingle();
        user.HasPassword.Should().BeTrue();
    }

    [TestMethod]
    public void A_sign_in_with_a_revoked_password_records_nothing()
    {
        var squatted = Squatted();
        squatted.User.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.EmailOtp), PresentedCredentials.None, Evictions(), Now);
        Drain(squatted.User);

        squatted.User.RecordPasswordSignIn(squatted.Password.Id, Now.AddMinutes(1));

        squatted.Password.LastUsedAt.Should().BeNull();
        Events(squatted.User).Should().BeEmpty();
        squatted.User.HasPassword.Should().BeFalse();
    }

    // ---- Phone numbers -------------------------------------------------------------------------

    [TestMethod]
    public void The_first_verified_phone_is_primary_and_a_number_proven_again_is_verified_again()
    {
        var user = SqlOSUser.Register("Alice", Now);
        Drain(user);

        var first = user.AddVerifiedPhone("+12025550110", "protected-1", Now);
        var second = user.AddVerifiedPhone("+12025550111", "protected-2", Now.AddMinutes(1));

        first.Id.Should().MatchRegex("^phn_[0-9a-f]{24}$");
        first.PhoneNumberHash.Should().Be(HashedSecret.Sha256("+12025550110").Hash);
        first.IsPrimary.Should().BeTrue();
        first.IsVerified.Should().BeTrue();
        first.VerifiedAt.Should().Be(Now);
        first.DisplayValueEncrypted.Should().Be("protected-1");
        second.IsPrimary.Should().BeFalse();
        Events(user).Should().Equal(new UserPhoneNumberVerified(user.Id, first.Id), new UserPhoneNumberVerified(user.Id, second.Id));

        var again = user.AddVerifiedPhone("+12025550110", "protected-again", Now.AddMinutes(2));
        again.Should().BeSameAs(first);
        first.VerifiedAt.Should().Be(Now, "a number keeps the time it was first proven");
        first.DisplayValueEncrypted.Should().Be("protected-again");
        first.UpdatedAt.Should().Be(Now.AddMinutes(2));
        user.PhoneNumbers.Should().HaveCount(2);
    }

    [TestMethod]
    public void A_number_added_after_the_claim_removed_the_others_is_primary_and_records_its_sign_ins()
    {
        var squatted = Squatted();
        squatted.User.ClaimWithProof(Proof("alice@example.test", OwnershipProofMethod.EmailOtp), PresentedCredentials.None, Evictions(), Now);

        var replacement = squatted.User.AddVerifiedPhone("+12025550199", "protected", Now.AddMinutes(1));

        replacement.IsPrimary.Should().BeTrue("removed numbers do not count");
        squatted.User.RecordPhoneSignIn(replacement.Id, Now.AddMinutes(2));
        replacement.LastUsedAt.Should().Be(Now.AddMinutes(2));
        squatted.User.UpdatedAt.Should().Be(Now.AddMinutes(2));
        squatted.User.RecordPhoneSignIn(null, Now.AddMinutes(3));
        squatted.User.UpdatedAt.Should().Be(Now.AddMinutes(3));
    }

    // ---- Authenticator apps and recovery codes -------------------------------------------------

    [TestMethod]
    public void A_new_enrollment_replaces_an_unconfirmed_one_and_keeps_confirmed_ones()
    {
        var user = SqlOSUser.Register("Alice", Now);
        var confirmed = user.EnrollTotp("secret-1", "  Phone  ", Totp, Now);
        user.ConfirmTotp(confirmed.Id, 100, Now);
        var abandoned = user.EnrollTotp("secret-2", null, Totp, Now.AddMinutes(1));
        Drain(user);

        var current = user.EnrollTotp("secret-3", null, Totp with { Digits = 8 }, Now.AddMinutes(2));

        confirmed.DisplayName.Should().Be("Phone");
        confirmed.RevokedAt.Should().BeNull();
        abandoned.DisplayName.Should().Be("Authenticator app");
        abandoned.RevokedAt.Should().Be(Now.AddMinutes(2));
        abandoned.RevocationReason.Should().Be("replaced_unconfirmed");
        current.Id.Should().MatchRegex("^mfa_[0-9a-f]{24}$");
        current.IsConfirmed.Should().BeFalse();
        current.SecretProtected.Should().Be("secret-3");
        current.Digits.Should().Be(8);
        Events(user).Should().Equal(
            new UserAuthenticatorRevoked(user.Id, abandoned.Id, "replaced_unconfirmed"),
            new UserTotpEnrollmentStarted(user.Id, current.Id));
    }

    [TestMethod]
    public void Confirming_an_authenticator_opts_the_account_into_mfa_unless_it_chose()
    {
        var user = SqlOSUser.Register("Alice", Now);
        var authenticator = user.EnrollTotp("secret", null, Totp, Now);
        Drain(user);

        user.ConfirmTotp(authenticator.Id, 1234, Now.AddMinutes(1));

        authenticator.IsConfirmed.Should().BeTrue();
        authenticator.ConfirmedAt.Should().Be(Now.AddMinutes(1));
        authenticator.LastUsedAt.Should().Be(Now.AddMinutes(1));
        authenticator.LastAcceptedTimeStep.Should().Be(1234);
        user.MfaPolicyOverride!.RequireMfa.Should().BeTrue();
        user.MfaPolicyOverride.UserId.Should().Be(user.Id);
        Events(user).Should().Equal(new UserTotpConfirmed(user.Id, authenticator.Id), new UserOptedIntoMfa(user.Id));

        FluentActions.Invoking(() => user.ConfirmTotp(authenticator.Id, 1235, Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.InvalidMemberState);
        FluentActions.Invoking(() => user.ConfirmTotp("mfa_unknown", 1, Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.UnknownMember);

        var optedOut = TestRows.Create<SqlOSUser>(new { Id = "usr_out", MfaPolicyOverride = TestRows.Create<SqlOSUserMfaPolicyOverride>(new { UserId = "usr_out", RequireMfa = (bool?)false }) });
        optedOut.MarkLoaded(SqlOSUserParts.All);
        var second = optedOut.EnrollTotp("secret", null, Totp, Now);
        optedOut.ConfirmTotp(second.Id, 1, Now);
        optedOut.MfaPolicyOverride!.RequireMfa.Should().BeFalse("an account that chose keeps its choice");
    }

    [TestMethod]
    public void A_code_is_accepted_once_per_time_step()
    {
        var user = SqlOSUser.Register("Alice", Now);
        var authenticator = user.EnrollTotp("secret", null, Totp, Now);
        FluentActions.Invoking(() => user.AcceptTotpCode(authenticator.Id, 1, Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.InvalidMemberState, "an unconfirmed authenticator accepts no codes");
        user.ConfirmTotp(authenticator.Id, 100, Now);
        Drain(user);

        user.AcceptTotpCode(authenticator.Id, 100, Now.AddMinutes(1)).Should().BeFalse("a replayed step");
        user.AcceptTotpCode(authenticator.Id, 99, Now.AddMinutes(1)).Should().BeFalse("an earlier step");
        Events(user).Should().BeEmpty();
        user.AcceptTotpCode(authenticator.Id, 101, Now.AddMinutes(2)).Should().BeTrue();

        authenticator.LastAcceptedTimeStep.Should().Be(101);
        authenticator.LastUsedAt.Should().Be(Now.AddMinutes(2));
        Events(user).Should().Equal(new UserTotpCodeAccepted(user.Id, authenticator.Id));
    }

    [TestMethod]
    public void A_revoked_authenticator_is_gone_from_every_rule()
    {
        var user = SqlOSUser.Register("Alice", Now);
        var authenticator = user.EnrollTotp("secret", null, Totp, Now);
        user.ConfirmTotp(authenticator.Id, 1, Now);
        Drain(user);

        user.RevokeAuthenticator(authenticator.Id, "user_removed", Now.AddMinutes(1));

        authenticator.RevokedAt.Should().Be(Now.AddMinutes(1));
        authenticator.RevocationReason.Should().Be("user_removed");
        user.FindAuthenticator(authenticator.Id).Should().BeNull();
        user.ConfirmedTotpAuthenticators.Should().BeEmpty();
        Events(user).Should().Equal(new UserAuthenticatorRevoked(user.Id, authenticator.Id, "user_removed"));
        FluentActions.Invoking(() => user.RevokeAuthenticator(authenticator.Id, "user_removed", Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.UnknownMember);
    }

    [TestMethod]
    public void Recovery_codes_are_stored_as_hashes_spent_once_and_replaced_as_a_set()
    {
        var user = SqlOSUser.Register("Alice", Now);
        Drain(user);

        user.IssueRecoveryCodes(["AAAA-BBBB", "CCCC-DDDD"], Now);

        user.RecoveryCodes.Should().HaveCount(2);
        user.RecoveryCodes.First().Id.Should().MatchRegex("^mrc_[0-9a-f]{24}$");
        user.RecoveryCodes.Select(code => code.CodeHash).Should().BeEquivalentTo([HashedSecret.Sha256("AAAA-BBBB").Hash, HashedSecret.Sha256("CCCC-DDDD").Hash]);
        Events(user).Should().Equal(new UserRecoveryCodesIssued(user.Id, 2));

        user.UseRecoveryCode("AAAA-BBBB", Now.AddMinutes(1)).Should().BeTrue();
        var used = user.RecoveryCodes.Single(code => code.ConsumedAt != null);
        used.ConsumedAt.Should().Be(Now.AddMinutes(1));
        Events(user).Should().Equal(new UserRecoveryCodeUsed(user.Id, used.Id));
        user.UseRecoveryCode("AAAA-BBBB", Now.AddMinutes(2)).Should().BeFalse("a code is spent once");
        user.UseRecoveryCode("EEEE-FFFF", Now.AddMinutes(2)).Should().BeFalse();

        user.IssueRecoveryCodes(["GGGG-HHHH"], Now.AddMinutes(3));
        var unused = user.RecoveryCodes.Single(code => code.CodeHash == HashedSecret.Sha256("CCCC-DDDD").Hash);
        unused.RevokedAt.Should().Be(Now.AddMinutes(3), "a new set replaces the unused codes");
        used.RevokedAt.Should().BeNull("a spent code stays spent, not revoked");
        user.UseRecoveryCode("CCCC-DDDD", Now.AddMinutes(4)).Should().BeFalse();
        user.UseRecoveryCode("GGGG-HHHH", Now.AddMinutes(4)).Should().BeTrue();
    }

    // ---- External identities -------------------------------------------------------------------

    [TestMethod]
    public void An_identity_joins_an_existing_account_only_with_its_providers_proof_of_a_verified_address()
    {
        var unverified = Account();
        FluentActions.Invoking(() => unverified.LinkExternalIdentity(Google, Proof("alice@example.test", OwnershipProofMethod.Oidc), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.InvalidMemberState, "an unverified address is claimed first (#423)");

        var verified = SqlOSUser.Register("Alice", Proof("alice@example.test", OwnershipProofMethod.EmailOtp), Now);
        FluentActions.Invoking(() => verified.LinkExternalIdentity(Google, Proof("alice@example.test", OwnershipProofMethod.Saml), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch, "a SAML assertion never links a Google identity");
        FluentActions.Invoking(() => verified.LinkExternalIdentity(Google, Proof("bob@example.test", OwnershipProofMethod.Oidc), Now))
            .Should().Throw<SqlOSDomainException>().Which.Error.Should().Be(SqlOSDomainError.OwnershipProofMismatch);
        Drain(verified);

        var linked = verified.LinkExternalIdentity(Google, Proof("ALICE@example.test", OwnershipProofMethod.Oidc), Now);

        linked.OidcConnectionId.Should().Be("oidc_google");
        verified.ExternalIdentities.Should().ContainSingle().Which.Should().BeSameAs(linked);
        Events(verified).Should().Equal(new UserExternalIdentityLinked(verified.Id, linked.Id, "oidc", "oidc_google"));
    }

    // ---- Lifecycle and profile -----------------------------------------------------------------

    [TestMethod]
    public void An_account_is_deactivated_and_reactivated_once_each()
    {
        var user = SqlOSUser.Register("Alice", Now);
        Drain(user);

        user.Deactivate("scim_deprovisioned", Now.AddMinutes(1));
        user.Deactivate("scim_deprovisioned", Now.AddMinutes(2));

        user.IsActive.Should().BeFalse();
        user.UpdatedAt.Should().Be(Now.AddMinutes(2));
        Events(user).Should().Equal(new UserDeactivated(user.Id, "scim_deprovisioned"));

        user.Reactivate(Now.AddMinutes(3));
        user.Reactivate(Now.AddMinutes(4));
        user.IsActive.Should().BeTrue();
        user.UpdatedAt.Should().Be(Now.AddMinutes(4));
        Events(user).Should().Equal(new UserReactivated(user.Id));
        FluentActions.Invoking(() => user.Deactivate(" ", Now)).Should().Throw<ArgumentException>("a deactivation names its reason");
    }

    [TestMethod]
    public void A_profile_change_is_an_event_only_when_something_changed()
    {
        var user = SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);
        Drain(user);

        user.UpdateProfile("Alice", "alice@example.test", Now.AddMinutes(1));
        Events(user).Should().BeEmpty();
        user.UpdatedAt.Should().Be(Now.AddMinutes(1));

        user.UpdateProfile("Alice Liddell", " Alice@Example.test", Now.AddMinutes(2));
        user.DisplayName.Should().Be("Alice Liddell");
        user.DefaultEmail.Should().Be(" Alice@Example.test", "a directory's default email is kept exactly as sent");
        Events(user).Should().Equal(new UserProfileChanged(user.Id));
    }

    [TestMethod]
    public void The_owned_collections_are_read_only_views()
    {
        var user = Account();

        FluentActions.Invoking(() => user.Emails.Add(user.Emails.Single())).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => user.Credentials.Clear()).Should().Throw<NotSupportedException>();
        user.Emails.IsReadOnly.Should().BeTrue();
    }

    [TestMethod]
    public void Recording_a_sign_up_raises_the_event_its_audit_row_is_projected_from()
    {
        var user = Account();
        Drain(user);

        user.RecordSignUp("email_otp", "org_1", "203.0.113.10");

        Events(user).Should().ContainSingle().Which.Should().Be(new UserSignedUp(user.Id, "email_otp", "org_1", "203.0.113.10"));
        new UserSignedUp(user.Id, "password", null, null).AuditEventType.Should().Be("user.signup");
        new UserSignedUp(user.Id, "phone_otp", null, null).AuditEventType.Should().Be("user.signup.phone_otp");
        FluentActions.Invoking(() => user.RecordSignUp(" ", null, null)).Should().Throw<ArgumentException>();
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static SqlOSUser Account() => SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);

    /// <summary>An unverified account a squatter registered and attached something of every kind to.</summary>
    private static SquattedAccount Squatted()
    {
        var user = SqlOSUser.RegisterFromExternalIdentity("Squatter", EmailAddress.Parse("alice@example.test"), Google, Now);
        var password = user.SetPassword("Squatter-Password-1", PasswordPolicy.Default, Now);
        var authenticator = user.EnrollTotp("secret", null, Totp, Now);
        user.ConfirmTotp(authenticator.Id, 1, Now);
        user.IssueRecoveryCodes([RawRecoveryCode(0), RawRecoveryCode(1), RawRecoveryCode(2)], Now);
        var phone = user.AddVerifiedPhone("+12025550100", "protected", Now);
        // A SAML identity linked before 7.2.1 required a verified address, as an upgraded row holds it.
        var saml = TestRows.Create<SqlOSExternalIdentity>(new { Id = "ext_saml", UserId = user.Id, SsoConnectionId = "sso_acme", Issuer = "urn:acme", Subject = "squatter", CreatedAt = Now });
        ((List<SqlOSExternalIdentity>)typeof(SqlOSUser).GetField("_externalIdentities", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(user)!).Add(saml);
        return new SquattedAccount(user, password, authenticator, user.RecoveryCodes.ToList(), phone, user.ExternalIdentities.First(), saml);
    }

    private static string RawRecoveryCode(int index) => $"CODE-{index:0000}";

    private static OwnershipProof Proof(string address, OwnershipProofMethod method) => new(EmailAddress.Parse(address), method);

    private static EmailClaimEvictions Evictions(string[]? consentGrantIds = null, string[]? calendarConnectionIds = null)
        => new(consentGrantIds ?? [], calendarConnectionIds ?? []);

    private static bool Verifies(string hash, string password)
        => new PasswordHasher<object>().VerifyHashedPassword(new object(), hash, password) != PasswordVerificationResult.Failed;

    private static IReadOnlyList<ISqlOSDomainEvent> Events(SqlOSUser user)
        => ((ISqlOSAggregate)user).Events.Drain().Select(raised => raised.Event).ToList();

    private static void Drain(SqlOSUser user) => ((ISqlOSAggregate)user).Events.Drain();

    private sealed record SquattedAccount(
        SqlOSUser User,
        SqlOSCredential Password,
        SqlOSUserAuthenticator Authenticator,
        IReadOnlyList<SqlOSRecoveryCode> RecoveryCodes,
        SqlOSUserPhoneNumber Phone,
        SqlOSExternalIdentity Oidc,
        SqlOSExternalIdentity Saml);
}
