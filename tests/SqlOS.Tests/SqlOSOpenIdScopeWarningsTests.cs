using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSOpenIdScopeWarningsTests
{
    [TestMethod]
    public void UserFacingAllowlistWithoutOpenId_Warns()
    {
        var warning = SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
            ["profile", "email"],
            isFirstParty: true,
            allowNativeHeadlessAuth: false,
            allowDeviceAuthorization: false,
            redirectUris: ["https://app.example.test/callback"],
            grantTypes: [SqlOSOAuthGrantTypes.AuthorizationCode]);

        warning.Should().NotBeNull();
        warning!.Code.Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdCode);
        warning.Message.Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdMessage);
    }

    [TestMethod]
    public void AddingOpenIdToAllowlist_ClearsConfigurationWarning()
    {
        SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
                ["openid", "profile", "email"],
                isFirstParty: true,
                allowNativeHeadlessAuth: false,
                allowDeviceAuthorization: false,
                redirectUris: ["https://app.example.test/callback"],
                grantTypes: [SqlOSOAuthGrantTypes.AuthorizationCode])
            .Should().BeNull();
    }

    [TestMethod]
    public void EmptyUserFacingAllowlist_WarnsThatOpenIdIsMissing()
    {
        var warning = SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
            [],
            isFirstParty: false,
            allowNativeHeadlessAuth: false,
            allowDeviceAuthorization: false,
            redirectUris: ["https://app.example.test/callback"],
            grantTypes: []);

        warning.Should().NotBeNull();
        warning!.Code.Should().Be(SqlOSOpenIdScopeWarnings.MissingAllowlistedOpenIdCode);
    }

    [TestMethod]
    public void NativeHeadlessWithoutOpenId_Warns()
    {
        SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
                ["profile"],
                isFirstParty: false,
                allowNativeHeadlessAuth: true,
                allowDeviceAuthorization: false,
                redirectUris: [],
                grantTypes: [])
            .Should().NotBeNull();
    }

    [TestMethod]
    public void MachineOnlyWithoutOpenId_DoesNotWarn()
    {
        SqlOSOpenIdScopeWarnings.ForMissingAllowlistedOpenId(
                ["ledger.export"],
                isFirstParty: false,
                allowNativeHeadlessAuth: false,
                allowDeviceAuthorization: false,
                redirectUris: [],
                grantTypes: [SqlOSOAuthGrantTypes.ClientCredentials])
            .Should().BeNull();
    }

    [TestMethod]
    public void AllowlistWithOpenIdAndOmittedGrant_WarnsAtRequestTime()
    {
        var warning = SqlOSOpenIdScopeWarnings.ForOmittedGrantedOpenId(
            ["openid", "profile", "email"],
            grantedScope: "");

        warning.Should().NotBeNull();
        warning!.Code.Should().Be(SqlOSOpenIdScopeWarnings.OmittedGrantedOpenIdCode);
        warning.Message.Should().Be(SqlOSOpenIdScopeWarnings.OmittedGrantedOpenIdMessage);
    }

    [TestMethod]
    public void AllowlistWithOpenIdAndProfileOnlyGrant_WarnsAtRequestTime()
    {
        SqlOSOpenIdScopeWarnings.ForOmittedGrantedOpenId(
                ["openid", "profile", "email"],
                grantedScope: "profile")
            .Should().NotBeNull();
    }

    [TestMethod]
    public void GrantedOpenId_DoesNotWarnAtRequestTime()
    {
        SqlOSOpenIdScopeWarnings.ForOmittedGrantedOpenId(
                ["openid", "profile", "email"],
                grantedScope: "openid profile email")
            .Should().BeNull();
    }

    [TestMethod]
    public void OAuthOnlyAllowlist_DoesNotWarnWhenGrantOmitsOpenId()
    {
        SqlOSOpenIdScopeWarnings.ForOmittedGrantedOpenId(
                ["profile", "email"],
                grantedScope: "")
            .Should().BeNull();
    }

    [TestMethod]
    public void ApplyRequestWarning_PreservesExistingInfo()
    {
        var applied = SqlOSOpenIdScopeWarnings.ApplyRequestWarning(
            "CLI access was denied.",
            ["openid", "profile"],
            grantedScope: "profile");

        applied.OmittedOpenId.Should().BeTrue();
        applied.Info.Should().StartWith("CLI access was denied.");
        applied.Info.Should().Contain(SqlOSOpenIdScopeWarnings.OmittedGrantedOpenIdMessage);
    }
}
