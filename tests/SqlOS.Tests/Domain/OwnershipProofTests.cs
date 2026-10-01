using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Domain;

namespace SqlOS.Tests.Domain;

[TestClass]
public sealed class OwnershipProofTests
{
    [TestMethod]
    public void Each_method_keeps_the_name_the_claim_audit_records()
    {
        // user.email.claimed records these names; the 7.2.1 ones never change.
        OwnershipProof.NameOf(OwnershipProofMethod.EmailOtp).Should().Be("email_otp");
        OwnershipProof.NameOf(OwnershipProofMethod.MagicLink).Should().Be("magic_link");
        OwnershipProof.NameOf(OwnershipProofMethod.PasswordReset).Should().Be("password_reset");
        OwnershipProof.NameOf(OwnershipProofMethod.Invitation).Should().Be("invitation");
        OwnershipProof.NameOf(OwnershipProofMethod.Oidc).Should().Be("oidc");
        OwnershipProof.NameOf(OwnershipProofMethod.Saml).Should().Be("saml");
        OwnershipProof.NameOf(OwnershipProofMethod.EmailVerification).Should().Be("email_verification");
        OwnershipProof.NameOf(OwnershipProofMethod.Directory).Should().Be("scim");
        Enum.GetValues<OwnershipProofMethod>().Should().HaveCount(8, "a new method needs its own line above");
        FluentActions.Invoking(() => OwnershipProof.NameOf((OwnershipProofMethod)99)).Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void A_proof_covers_every_spelling_of_its_mailbox_and_nothing_else()
    {
        var proof = new OwnershipProof(EmailAddress.Parse(" Alice@Example.TEST "), OwnershipProofMethod.MagicLink);

        proof.Covers("alice@example.test").Should().BeTrue();
        proof.Covers("ALICE@EXAMPLE.TEST").Should().BeTrue();
        proof.Covers("alice@example.test.evil").Should().BeFalse();
        proof.Covers("not an address").Should().BeFalse();
        proof.Covers(null).Should().BeFalse();
        proof.MethodName.Should().Be("magic_link");
        proof.ToString().Should().Be("OwnershipProof(magic_link)").And.NotContain("alice", "a proof never logs its mailbox");
        FluentActions.Invoking(() => new OwnershipProof(EmailAddress.Parse("a@b.test"), (OwnershipProofMethod)0)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
