using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Services;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSClientAllowlistWarningsTests
{
    [TestMethod]
    public void PopulatedAllowlist_DoesNotWarn()
    {
        SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
                ["openid", "profile", "email"],
                isFirstParty: true,
                allowNativeHeadlessAuth: false,
                allowDeviceAuthorization: false,
                redirectUris: ["https://app.example.test/callback"],
                grantTypes: [SqlOSOAuthGrantTypes.AuthorizationCode])
            .Should().BeNull();
    }

    [TestMethod]
    public void FirstPartyEmptyAllowlist_WarnsWithUserFacingCopy()
    {
        var warning = SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
            [],
            isFirstParty: true,
            allowNativeHeadlessAuth: false,
            allowDeviceAuthorization: false,
            redirectUris: [],
            grantTypes: []);

        warning.Should().NotBeNull();
        warning!.Code.Should().Be(SqlOSClientAllowlistWarnings.EmptyAllowlistCode);
        warning.Message.Should().Be(SqlOSClientAllowlistWarnings.UserFacingEmptyAllowlistMessage);
    }

    [TestMethod]
    public void RedirectClientEmptyAllowlist_WarnsAsUserFacing()
    {
        var warning = SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
            [],
            isFirstParty: false,
            allowNativeHeadlessAuth: false,
            allowDeviceAuthorization: false,
            redirectUris: ["https://rp.example.test/callback"],
            grantTypes: [SqlOSOAuthGrantTypes.AuthorizationCode]);

        warning.Should().NotBeNull();
        warning!.Message.Should().Be(SqlOSClientAllowlistWarnings.UserFacingEmptyAllowlistMessage);
    }

    [TestMethod]
    public void NativeHeadlessEmptyAllowlist_WarnsAsUserFacing()
    {
        var warning = SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
            [],
            isFirstParty: false,
            allowNativeHeadlessAuth: true,
            allowDeviceAuthorization: false,
            redirectUris: [],
            grantTypes: []);

        warning.Should().NotBeNull();
        warning!.Message.Should().Be(SqlOSClientAllowlistWarnings.UserFacingEmptyAllowlistMessage);
    }

    [TestMethod]
    public void MachineOnlyEmptyAllowlist_UsesShorterCopy()
    {
        var warning = SqlOSClientAllowlistWarnings.ForEmptyAllowlist(
            [],
            isFirstParty: false,
            allowNativeHeadlessAuth: false,
            allowDeviceAuthorization: false,
            redirectUris: [],
            grantTypes: [SqlOSOAuthGrantTypes.ClientCredentials]);

        warning.Should().NotBeNull();
        warning!.Code.Should().Be(SqlOSClientAllowlistWarnings.EmptyAllowlistCode);
        warning.Message.Should().Be(SqlOSClientAllowlistWarnings.MachineEmptyAllowlistMessage);
    }
}
