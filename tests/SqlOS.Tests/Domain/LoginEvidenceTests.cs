using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Models;
using SqlOS.Domain;
using static SqlOS.Tests.Domain.DomainTime;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class LoginEvidenceTests
{
    [TestMethod]
    public void Evidence_carries_the_user_its_methods_its_proofs_and_when_the_person_authenticated()
    {
        var user = Account();
        var proof = new OwnershipProof(EmailAddress.Parse("alice@example.test"), OwnershipProofMethod.EmailOtp);

        var evidence = new LoginEvidence(user, [AuthenticationMethods.EmailOtp], [proof], Now);

        evidence.User.Should().BeSameAs(user);
        evidence.UserId.Should().Be(user.Id);
        evidence.Methods.Should().Equal("email_otp");
        evidence.AuthenticationMethod.Should().Be("email_otp");
        evidence.Assurance.Should().Be(LoginAssurance.SingleFactor);
        evidence.Proofs.Should().Equal(proof);
        evidence.AuthenticatedAt.Should().Be(Now);
        evidence.ToString().Should().Be("LoginEvidence(email_otp)").And.NotContain("alice", "evidence never logs its user");
    }

    [TestMethod]
    public void A_second_factor_makes_the_login_multi_factor_and_joins_the_method_names_as_7x_records_them()
    {
        var evidence = new LoginEvidence(Account(), [AuthenticationMethods.Password, "totp"], [], Now);

        evidence.Methods.Should().Equal("password", "totp");
        evidence.AuthenticationMethod.Should().Be("password+totp");
        evidence.Assurance.Should().Be(LoginAssurance.MultiFactor);
        evidence.Proofs.Should().BeEmpty();
    }

    [TestMethod]
    public void The_method_names_are_the_ones_sessions_codes_and_login_audits_record()
    {
        // Sessions, authorization codes and user.login.* audit rows record these names; the 7.2.1 ones never change.
        AuthenticationMethods.Password.Should().Be("password");
        AuthenticationMethods.EmailOtp.Should().Be("email_otp");
        AuthenticationMethods.MagicLink.Should().Be("magic_link");
        AuthenticationMethods.PhoneOtp.Should().Be("phone_otp");
        AuthenticationMethods.Invitation.Should().Be("invitation");
    }

    [TestMethod]
    public void Evidence_names_at_least_one_method_each_once_and_each_a_single_name()
    {
        var user = Account();

        FluentActions.Invoking(() => new LoginEvidence(user, [], [], Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LoginEvidence(user, ["password", "PASSWORD"], [], Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LoginEvidence(user, ["password+totp"], [], Now)).Should().Throw<ArgumentException>(
            "a joined string would pass a second factor off as part of the first");
        FluentActions.Invoking(() => new LoginEvidence(user, ["password,totp"], [], Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LoginEvidence(user, ["email otp"], [], Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LoginEvidence(user, [" "], [], Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new LoginEvidence(user, [null!], [], Now)).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Evidence_has_a_user_methods_and_no_missing_proof()
    {
        FluentActions.Invoking(() => new LoginEvidence(null!, ["password"], [], Now)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LoginEvidence(Account(), null!, [], Now)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LoginEvidence(Account(), ["password"], null!, Now)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LoginEvidence(Account(), ["password"], [null!], Now)).Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Evidence_copies_its_methods_and_proofs_so_the_producer_cannot_change_them_later()
    {
        var methods = new List<string> { "password" };
        var proofs = new List<OwnershipProof>();

        var evidence = new LoginEvidence(Account(), methods, proofs, Now);
        methods.Add("totp");
        proofs.Add(new OwnershipProof(EmailAddress.Parse("alice@example.test"), OwnershipProofMethod.EmailOtp));

        evidence.Methods.Should().Equal("password");
        evidence.Assurance.Should().Be(LoginAssurance.SingleFactor);
        evidence.Proofs.Should().BeEmpty();
    }

    private static SqlOSUser Account() => SqlOSUser.Register("Alice", EmailAddress.Parse("alice@example.test"), Now);
}
