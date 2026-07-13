using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Extensions;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSScimProviderCompatibilityTests
{
    private const string ScimBasePath = "/sqlos/scim/v2";
    private const string PublicOrigin = "https://scim.example.test";

    [TestMethod]
    public async Task DisabledByDefault_DoesNotExposeScimRoutes()
    {
        await using var host = await ScimTestHost.CreateAsync(enableScim: false);

        using var response = await host.AnonymousClient.GetAsync($"{ScimBasePath}/ServiceProviderConfig");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task EnabledScim_RequiresBearerAuthentication()
    {
        await using var host = await ScimTestHost.CreateAsync();

        using var response = await host.AnonymousClient.GetAsync($"{ScimBasePath}/ServiceProviderConfig");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsTrue(
            response.Headers.WwwAuthenticate.Any(value =>
                string.Equals(value.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)),
            "SCIM 401 responses should advertise Bearer authentication.");
        AssertScimContentType(response);
        var error = await ReadObjectAsync(response);
        Assert.AreEqual("401", error["status"]?.GetValue<string>());
        Assert.AreEqual(
            "urn:ietf:params:scim:api:messages:2.0:Error",
            error["schemas"]?[0]?.GetValue<string>());
    }

    [TestMethod]
    public async Task Discovery_LinksToCanonicalPublicDocumentation()
    {
        await using var host = await ScimTestHost.CreateAsync();

        using var response = await host.Client.GetAsync($"{ScimBasePath}/ServiceProviderConfig");
        var discovery = await ReadObjectAsync(response);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            "https://sqlos.dev/docs/authserver/scim-directory-sync",
            discovery["documentationUri"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task AdminTokenResponses_AreExplicitlyNonCacheable()
    {
        await using var host = await ScimTestHost.CreateAsync();
        using var createResponse = await host.SendAnonymousJsonAsync(
            HttpMethod.Post,
            "/sqlos/admin/auth/api/organizations/org_scim_http/scim-connections",
            new JsonObject { ["displayName"] = "Non-cacheable token", ["enabled"] = false });
        var created = await ReadObjectAsync(createResponse);

        Assert.AreEqual(HttpStatusCode.OK, createResponse.StatusCode, await createResponse.Content.ReadAsStringAsync());
        Assert.IsTrue(createResponse.Headers.CacheControl?.NoStore == true);
        CollectionAssert.Contains(createResponse.Headers.Pragma.Select(value => value.Name).ToArray(), "no-cache");
        Assert.IsFalse(string.IsNullOrWhiteSpace(created["token"]?.GetValue<string>()));

        var connectionId = RequiredString(created, "connectionId");
        using var rotateResponse = await host.SendAnonymousJsonAsync(
            HttpMethod.Post,
            $"/sqlos/admin/auth/api/scim-connections/{connectionId}/token/rotate",
            new JsonObject());

        Assert.AreEqual(HttpStatusCode.OK, rotateResponse.StatusCode, await rotateResponse.Content.ReadAsStringAsync());
        Assert.IsTrue(rotateResponse.Headers.CacheControl?.NoStore == true);
        CollectionAssert.Contains(rotateResponse.Headers.Pragma.Select(value => value.Name).ToArray(), "no-cache");
    }

    [TestMethod]
    public async Task EntraCreate_PreservesDistinctUserNameAndEmail_AndRejectsDuplicate()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var payload = UserPayload(
            externalId: "entra-object-1",
            userName: "Test_User_00aa00aa-bb11-cc22-dd33-44ee44ee44ee",
            email: "different.primary@example.test",
            displayName: "Ada Lovelace");

        using var createdResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);

        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        AssertScimContentType(createdResponse);
        var created = await ReadObjectAsync(createdResponse);
        var userId = RequiredString(created, "id");
        var expectedLocation = $"{PublicOrigin}{ScimBasePath}/Users/{userId}";
        Assert.AreEqual(expectedLocation, createdResponse.Headers.Location?.AbsoluteUri);
        Assert.AreEqual(expectedLocation, created["meta"]?["location"]?.GetValue<string>());
        Assert.AreEqual("Test_User_00aa00aa-bb11-cc22-dd33-44ee44ee44ee", RequiredString(created, "userName"));
        Assert.AreEqual("different.primary@example.test", created["emails"]?[0]?["value"]?.GetValue<string>());

        var filter = Uri.EscapeDataString("userName eq \"Test_User_00aa00aa-bb11-cc22-dd33-44ee44ee44ee\"");
        using var queryResponse = await host.Client.GetAsync($"{ScimBasePath}/Users?filter={filter}");
        Assert.AreEqual(HttpStatusCode.OK, queryResponse.StatusCode);
        var query = await ReadObjectAsync(queryResponse);
        Assert.AreEqual(1, query["totalResults"]?.GetValue<int>());
        Assert.AreEqual(userId, query["Resources"]?[0]?["id"]?.GetValue<string>());

        using var duplicateResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);
        Assert.AreEqual(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        var duplicate = await ReadObjectAsync(duplicateResponse);
        Assert.AreEqual("uniqueness", duplicate["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task SameEmailWithDifferentProviderIdentity_ReturnsConflictWithoutOverwritingExistingLink()
    {
        await using var host = await ScimTestHost.CreateAsync();
        const string sharedEmail = "shared-address@example.test";
        var firstPayload = UserPayload(
            "provider-object-first",
            "first-provider-identity@example.test",
            sharedEmail,
            "First provider identity");
        using var firstResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", firstPayload);
        Assert.AreEqual(HttpStatusCode.Created, firstResponse.StatusCode, await firstResponse.Content.ReadAsStringAsync());
        var first = await ReadObjectAsync(firstResponse);
        var firstId = RequiredString(first, "id");

        var secondPayload = UserPayload(
            "provider-object-second",
            "second-provider-identity@example.test",
            sharedEmail,
            "Second provider identity");
        using var secondResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", secondPayload);
        var second = await ReadObjectAsync(secondResponse);
        var persisted = await host.GetResourceAsync($"{ScimBasePath}/Users/{firstId}");
        var secondUserNameFilter = Uri.EscapeDataString("userName eq \"second-provider-identity@example.test\"");
        using var secondQueryResponse = await host.Client.GetAsync($"{ScimBasePath}/Users?filter={secondUserNameFilter}");
        var secondQuery = await ReadObjectAsync(secondQueryResponse);

        Assert.AreEqual(HttpStatusCode.Conflict, secondResponse.StatusCode);
        Assert.AreEqual("uniqueness", second["scimType"]?.GetValue<string>());
        Assert.AreEqual("provider-object-first", RequiredString(persisted, "externalId"));
        Assert.AreEqual("first-provider-identity@example.test", RequiredString(persisted, "userName"));
        Assert.AreEqual(0, secondQuery["totalResults"]?.GetValue<int>());
    }

    [TestMethod]
    public async Task ExternalId_IsOptionalAndCanBeRemoved_FromUsersAndGroups()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var userWithoutExternalId = UserPayload(
            "unused",
            "optional-external-user@example.test",
            "optional-external-user@example.test",
            "Optional external user");
        userWithoutExternalId.Remove("externalId");
        using var userWithoutExternalIdResponse = await host.SendJsonAsync(
            HttpMethod.Post,
            $"{ScimBasePath}/Users",
            userWithoutExternalId);
        var createdUserWithoutExternalId = await ReadObjectAsync(userWithoutExternalIdResponse);

        var groupWithoutExternalId = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
            ["displayName"] = "Optional external group",
            ["members"] = new JsonArray()
        };
        using var groupWithoutExternalIdResponse = await host.SendJsonAsync(
            HttpMethod.Post,
            $"{ScimBasePath}/Groups",
            groupWithoutExternalId);
        var createdGroupWithoutExternalId = await ReadObjectAsync(groupWithoutExternalIdResponse);

        var userWithExternalId = await host.CreateUserAsync("remove-external-user");
        var groupWithExternalId = await host.CreateGroupAsync("remove-external-group");
        var removeExternalId = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "remove",
                ["path"] = "externalId"
            })
        };
        using var removeUserResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Users/{userWithExternalId.Id}",
            removeExternalId.DeepClone().AsObject());
        var removedUser = await ReadObjectAsync(removeUserResponse);
        using var removeGroupResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Groups/{groupWithExternalId}?attributes=displayName,externalId",
            removeExternalId.DeepClone().AsObject());
        var removedGroup = await ReadObjectAsync(removeGroupResponse);

        var persistedUser = await host.GetResourceAsync($"{ScimBasePath}/Users/{userWithExternalId.Id}");
        var persistedGroup = await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupWithExternalId}");
        var failures = new List<string>();
        RecordExpectedStatus(userWithoutExternalIdResponse, HttpStatusCode.Created, "user create without externalId", failures);
        RecordExpectedStatus(groupWithoutExternalIdResponse, HttpStatusCode.Created, "group create without externalId", failures);
        RecordExpectedStatus(removeUserResponse, HttpStatusCode.OK, "user externalId removal", failures);
        RecordExpectedStatus(removeGroupResponse, HttpStatusCode.OK, "group externalId removal", failures);
        RecordAbsentProperty(createdUserWithoutExternalId, "externalId", "created user", failures);
        RecordAbsentProperty(createdGroupWithoutExternalId, "externalId", "created group", failures);
        RecordAbsentProperty(removedUser, "externalId", "user removal response", failures);
        RecordAbsentProperty(removedGroup, "externalId", "group removal response", failures);
        RecordAbsentProperty(persistedUser, "externalId", "persisted user", failures);
        RecordAbsentProperty(persistedGroup, "externalId", "persisted group", failures);
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task OktaGroupWithoutExternalId_CanBeRepushedAfterDelete()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var payload = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
            ["displayName"] = "Okta Repushed Group",
            ["members"] = new JsonArray()
        };
        using var firstResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Groups", payload.DeepClone().AsObject());
        var first = await ReadObjectAsync(firstResponse);
        var groupId = RequiredString(first, "id");
        Assert.AreEqual(HttpStatusCode.Created, firstResponse.StatusCode, await firstResponse.Content.ReadAsStringAsync());

        using var deleteResponse = await host.Client.DeleteAsync($"{ScimBasePath}/Groups/{groupId}");
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        var filter = Uri.EscapeDataString("displayName eq \"Okta Repushed Group\"");
        using var hiddenResponse = await host.Client.GetAsync($"{ScimBasePath}/Groups?filter={filter}");
        Assert.AreEqual(0, (await ReadObjectAsync(hiddenResponse))["totalResults"]?.GetValue<int>());

        using var repushResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Groups", payload.DeepClone().AsObject());
        var repushed = await ReadObjectAsync(repushResponse);

        Assert.AreEqual(HttpStatusCode.Created, repushResponse.StatusCode, await repushResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(groupId, RequiredString(repushed, "id"));
        Assert.AreEqual("Okta Repushed Group", RequiredString(repushed, "displayName"));
    }

    [TestMethod]
    public async Task EntraFilteredWorkEmailJoinQuery_ReturnsMatchingUser()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var created = await host.CreateUserAsync("entra-work-email-filter");
        var filter = Uri.EscapeDataString(
            "emails[type eq \"work\"].value eq \"entra-work-email-filter@mail.example.test\"");

        using var response = await host.Client.GetAsync($"{ScimBasePath}/Users?filter={filter}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await ReadObjectAsync(response);
        Assert.AreEqual(1, body["totalResults"]?.GetValue<int>());
        Assert.AreEqual(created.Id, body["Resources"]?[0]?["id"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task EntraMultiOperationUserPatch_UpdatesWorkEmailAndStructuredName()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var payload = UserPayload(
            "entra-multi-patch",
            "entra-multi-patch@example.test",
            "ada.old@example.test",
            "Independent Display Name");
        payload["name"] = new JsonObject
        {
            ["formatted"] = "Ada Lovelace",
            ["givenName"] = "Ada",
            ["familyName"] = "Lovelace"
        };
        using var createResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);
        var userId = RequiredString(await ReadObjectAsync(createResponse), "id");
        using var patchResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{userId}", new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(
                new JsonObject
                {
                    ["op"] = "Replace",
                    ["path"] = "emails[type eq \"work\"].value",
                    ["value"] = "ada.new@example.test"
                },
                new JsonObject
                {
                    ["op"] = "Replace",
                    ["path"] = "name.familyName",
                    ["value"] = "Byron"
                })
        });
        var patched = await ReadObjectAsync(patchResponse);

        Assert.AreEqual(HttpStatusCode.OK, patchResponse.StatusCode, await patchResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("ada.new@example.test", patched["emails"]?[0]?["value"]?.GetValue<string>());
        Assert.AreEqual("Ada", patched["name"]?["givenName"]?.GetValue<string>());
        Assert.AreEqual("Byron", patched["name"]?["familyName"]?.GetValue<string>());
        Assert.AreEqual("Ada Byron", patched["name"]?["formatted"]?.GetValue<string>());
        Assert.AreEqual("Independent Display Name", RequiredString(patched, "displayName"));

        var filter = Uri.EscapeDataString("emails[type eq \"work\"].value eq \"ada.new@example.test\"");
        using var filterResponse = await host.Client.GetAsync($"{ScimBasePath}/Users?filter={filter}");
        var filtered = await ReadObjectAsync(filterResponse);
        Assert.AreEqual(1, filtered["totalResults"]?.GetValue<int>());
        Assert.AreEqual(userId, filtered["Resources"]?[0]?["id"]?.GetValue<string>());

        using var customFormattedResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{userId}", new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(
                new JsonObject { ["op"] = "replace", ["path"] = "name.formatted", ["value"] = "Dr. Ada Byron" },
                new JsonObject { ["op"] = "replace", ["path"] = "name.givenName", ["value"] = "Augusta" })
        });
        var customFormatted = await ReadObjectAsync(customFormattedResponse);
        Assert.AreEqual("Augusta", customFormatted["name"]?["givenName"]?.GetValue<string>());
        Assert.AreEqual("Dr. Ada Byron", customFormatted["name"]?["formatted"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task EmailSelection_PrefersWorkAndRejectsMultiplePrimaryValues()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var noPrimary = UserPayload(
            "email-choice",
            "email-choice@example.test",
            "unused@example.test",
            "Email Choice");
        noPrimary["emails"] = new JsonArray(
            new JsonObject { ["value"] = "home@example.test", ["type"] = "home" },
            new JsonObject { ["value"] = "work@example.test", ["type"] = "work" });
        using var selectedResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", noPrimary);
        var selected = await ReadObjectAsync(selectedResponse);

        Assert.AreEqual(HttpStatusCode.Created, selectedResponse.StatusCode, selectedResponse.ToString());
        Assert.AreEqual("work@example.test", selected["emails"]?[0]?["value"]?.GetValue<string>());

        var multiplePrimary = UserPayload(
            "email-multiple-primary",
            "email-multiple-primary@example.test",
            "unused@example.test",
            "Multiple Primary");
        multiplePrimary["emails"] = new JsonArray(
            new JsonObject { ["value"] = "one@example.test", ["primary"] = true, ["type"] = "work" },
            new JsonObject { ["value"] = "two@example.test", ["primary"] = true, ["type"] = "home" });
        using var rejectedResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", multiplePrimary);

        Assert.AreEqual(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        Assert.AreEqual("invalidValue", (await ReadObjectAsync(rejectedResponse))["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task CreateResources_RequireTheirCoreSchema()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = UserPayload(
            "schema-user",
            "schema-user@example.test",
            "schema-user@example.test",
            "Schema user");
        user.Remove("schemas");
        var group = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["externalId"] = "schema-group",
            ["displayName"] = "Schema group",
            ["members"] = new JsonArray()
        };

        using var userResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", user);
        using var groupResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Groups", group);

        Assert.AreEqual(HttpStatusCode.BadRequest, userResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(userResponse))["scimType"]?.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, groupResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(groupResponse))["scimType"]?.GetValue<string>());

        var incompatibleUser = UserPayload(
            "incompatible-schema-user",
            "incompatible-schema-user@example.test",
            "incompatible-schema-user@example.test",
            "Incompatible schema user");
        incompatibleUser["schemas"] = new JsonArray(
            "urn:ietf:params:scim:schemas:core:2.0:User",
            "urn:ietf:params:scim:schemas:core:2.0:Group");
        using var incompatibleResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", incompatibleUser);
        Assert.AreEqual(HttpStatusCode.BadRequest, incompatibleResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(incompatibleResponse))["scimType"]?.GetValue<string>());

        var duplicateSchemaUser = UserPayload(
            "duplicate-schema-user",
            "duplicate-schema-user@example.test",
            "duplicate-schema-user@example.test",
            "Duplicate schema user");
        duplicateSchemaUser["schemas"] = new JsonArray(
            "urn:ietf:params:scim:schemas:core:2.0:User",
            "urn:ietf:params:scim:schemas:core:2.0:User");
        using var duplicateResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", duplicateSchemaUser);
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
    }

    [TestMethod]
    public async Task PatchRequiresPatchOpSchema()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var missingSchemaUser = await host.CreateUserAsync("patch-missing-schema");
        var wrongSchemaUser = await host.CreateUserAsync("patch-wrong-schema");
        var missingSchemaPatch = new JsonObject
        {
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = "Missing schema must fail"
            })
        };
        var wrongSchemaPatch = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = "Wrong schema must fail"
            })
        };

        using var missingResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Users/{missingSchemaUser.Id}",
            missingSchemaPatch);
        using var wrongResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Users/{wrongSchemaUser.Id}",
            wrongSchemaPatch);

        Assert.AreEqual(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(missingResponse))["scimType"]?.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, wrongResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(wrongResponse))["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task OversizedPatchAndGroupMembership_ReturnTooManyWithoutPartialMutation()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await host.CreateUserAsync("bounded-writes");
        var operations = new JsonArray();
        for (var index = 0; index < 101; index++)
        {
            operations.Add(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = $"Rejected name {index:D3}"
            });
        }
        var oversizedPatch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = operations
        };

        using var patchResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Users/{user.Id}",
            oversizedPatch);

        Assert.AreEqual((HttpStatusCode)413, patchResponse.StatusCode);
        AssertScimContentType(patchResponse);
        Assert.AreEqual("tooMany", (await ReadObjectAsync(patchResponse))["scimType"]?.GetValue<string>());
        var unchangedUser = await host.GetResourceAsync($"{ScimBasePath}/Users/{user.Id}");
        Assert.AreEqual("bounded-writes", unchangedUser["displayName"]?.GetValue<string>());

        var members = new JsonArray();
        for (var index = 0; index < 10_001; index++)
        {
            members.Add(new JsonObject { ["value"] = $"member-{index:D5}" });
        }
        var oversizedGroup = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
            ["externalId"] = "oversized-group",
            ["displayName"] = "Oversized group",
            ["members"] = members
        };

        using var groupResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Groups", oversizedGroup);

        Assert.AreEqual((HttpStatusCode)413, groupResponse.StatusCode);
        AssertScimContentType(groupResponse);
        Assert.AreEqual("tooMany", (await ReadObjectAsync(groupResponse))["scimType"]?.GetValue<string>());
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        Assert.IsFalse(await context.Set<SqlOSScimExternalId>()
            .AnyAsync(link => link.ResourceType == "Group" && link.ExternalId == "oversized-group"));
        Assert.IsFalse(await context.Set<SqlOSFgaUserGroup>().AnyAsync(group => group.Name == "Oversized group"));
    }

    [TestMethod]
    public async Task PathlessRemove_IsRejected()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await host.CreateUserAsync("pathless-remove");
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "remove",
                ["value"] = new JsonObject { ["displayName"] = "must not be accepted" }
            })
        };

        using var response = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{user.Id}", patch);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("noTarget", (await ReadObjectAsync(response))["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task AddWithoutValue_IsRejectedWithoutMutatingGroup()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var existing = await host.CreateUserAsync("missing-add-value-existing");
        var groupId = await host.CreateGroupAsync("missing-add-value-group", existing.Id);
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "members"
            })
        };

        using var response = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", patch);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalidValue", (await ReadObjectAsync(response))["scimType"]?.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { existing.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));
    }

    [TestMethod]
    public async Task MalformedEmailEntry_IsRejectedInsteadOfSilentlyDropped()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var payload = UserPayload(
            "malformed-email",
            "malformed-email@example.test",
            "malformed-email@example.test",
            "Malformed email");
        payload["emails"]!.AsArray().Add(JsonValue.Create("not-an-email-object"));

        using var response = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalidValue", (await ReadObjectAsync(response))["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task MalformedMemberEntry_IsRejectedWithoutMutatingGroup()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await host.CreateUserAsync("malformed-member");
        var groupId = await host.CreateGroupAsync("malformed-member-group");
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "members",
                ["value"] = new JsonArray(
                    new JsonObject { ["value"] = user.Id },
                    JsonValue.Create("not-a-member-object"))
            })
        };

        using var response = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", patch);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalidValue", (await ReadObjectAsync(response))["scimType"]?.GetValue<string>());
        Assert.AreEqual(0, MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")).Length);
    }

    [TestMethod]
    public async Task ResourceResponses_ContentLocationMatchesMetaLocation()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var failures = new List<string>();
        using var createResponse = await host.SendJsonAsync(
            HttpMethod.Post,
            $"{ScimBasePath}/Users",
            UserPayload(
                "content-location-user",
                "content-location-user@example.test",
                "content-location-user@example.test",
                "Content location user"));
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await RecordContentLocationAgreementAsync(createResponse, "create", failures);
        var userId = RequiredString(created, "id");

        using var getResponse = await host.Client.GetAsync($"{ScimBasePath}/Users/{userId}");
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);
        await RecordContentLocationAgreementAsync(getResponse, "get", failures);

        using var patchResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{userId}", new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "displayName",
                ["value"] = "Content location user renamed"
            })
        });
        Assert.AreEqual(HttpStatusCode.OK, patchResponse.StatusCode);
        await RecordContentLocationAgreementAsync(patchResponse, "patch", failures);
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task OktaPathlessPatch_DeactivatesUser()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var user = await host.CreateUserAsync("okta-pathless");
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = new JsonObject { ["active"] = false }
            })
        };

        using var patchResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{user.Id}", patch);
        Assert.IsTrue(patchResponse.IsSuccessStatusCode, await patchResponse.Content.ReadAsStringAsync());

        var persisted = await host.GetResourceAsync($"{ScimBasePath}/Users/{user.Id}");
        Assert.IsFalse(persisted["active"]?.GetValue<bool>() ?? true);
    }

    [TestMethod]
    public async Task OktaPathlessGroupRename_ToleratesMatchingReadOnlyId()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var groupId = await host.CreateGroupAsync("okta-group-before");
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = new JsonObject
                {
                    ["id"] = groupId,
                    ["displayName"] = "okta-group-after"
                }
            })
        };

        using var response = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", patch);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "okta-group-after",
            RequiredString(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}"), "displayName"));
    }

    [TestMethod]
    public async Task PutUser_UpdatesUriTarget_AndCannotCreateMissingResource()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var target = await host.CreateUserAsync("put-target");
        var other = await host.CreateUserAsync("put-other");
        var replacement = UserPayload(
            externalId: "put-target-new-external-id",
            userName: "put-target-renamed@example.test",
            email: "put-target-renamed@example.test",
            displayName: "Renamed target");

        using var putResponse = await host.SendJsonAsync(HttpMethod.Put, $"{ScimBasePath}/Users/{target.Id}", replacement);

        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);
        var updated = await ReadObjectAsync(putResponse);
        Assert.AreEqual(target.Id, RequiredString(updated, "id"));
        Assert.AreEqual("put-target-renamed@example.test", RequiredString(updated, "userName"));
        var untouched = await host.GetResourceAsync($"{ScimBasePath}/Users/{other.Id}");
        Assert.AreEqual(other.UserName, RequiredString(untouched, "userName"));

        using var missingResponse = await host.SendJsonAsync(
            HttpMethod.Put,
            $"{ScimBasePath}/Users/usr_missing",
            UserPayload("missing-external", "missing@example.test", "missing@example.test", "Missing"));
        Assert.AreEqual(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [TestMethod]
    public async Task SameOrganizationUser_NotOwnedByConnection_CannotBeReadPatchedOrDeleted()
    {
        await using var host = await ScimTestHost.CreateAsync();
        const string unmanagedUserId = "usr_unmanaged_same_org";
        await host.SeedUnmanagedUserAsync(unmanagedUserId);
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "active",
                ["value"] = false
            })
        };

        using var getResponse = await host.Client.GetAsync($"{ScimBasePath}/Users/{unmanagedUserId}");
        using var patchResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Users/{unmanagedUserId}", patch);
        using var deleteResponse = await host.Client.DeleteAsync($"{ScimBasePath}/Users/{unmanagedUserId}");

        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, patchResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, deleteResponse.StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        Assert.IsTrue((await context.Set<SqlOSUser>().SingleAsync(user => user.Id == unmanagedUserId)).IsActive);
        Assert.IsTrue((await context.Set<SqlOSMembership>().SingleAsync(membership =>
            membership.OrganizationId == "org_scim_http" && membership.UserId == unmanagedUserId)).IsActive);
    }

    [TestMethod]
    public async Task GroupPatch_ReplacesMembers_ThenRemovesFilteredMember()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var first = await host.CreateUserAsync("member-one");
        var second = await host.CreateUserAsync("member-two");
        var third = await host.CreateUserAsync("member-three");
        var groupId = await host.CreateGroupAsync("provider-group", first.Id, second.Id);
        var rename = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["value"] = new JsonObject { ["displayName"] = "Provider group renamed" }
            })
        };
        using var renameResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", rename);
        Assert.IsTrue(renameResponse.IsSuccessStatusCode, await renameResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(
            "Provider group renamed",
            RequiredString(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}"), "displayName"));

        var replace = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "members",
                ["value"] = MemberArray(second.Id, third.Id)
            })
        };

        using var replaceResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", replace);
        Assert.IsTrue(replaceResponse.IsSuccessStatusCode, await replaceResponse.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(
            new[] { second.Id, third.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));

        var remove = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "remove",
                ["path"] = $"members[value eq \"{second.Id}\"]"
            })
        };
        using var removeResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", remove);
        Assert.IsTrue(removeResponse.IsSuccessStatusCode, await removeResponse.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(
            new[] { third.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));
    }

    [TestMethod]
    public async Task OktaAndEntraGroupDeltaPayloads_ApplySequentiallyAndReturnNoContent()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var first = await host.CreateUserAsync("provider-delta-first");
        var second = await host.CreateUserAsync("provider-delta-second");
        var third = await host.CreateUserAsync("provider-delta-third");
        var groupId = await host.CreateGroupAsync("provider-delta-group", first.Id, second.Id);

        var oktaPatch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(
                new JsonObject
                {
                    ["op"] = "remove",
                    ["path"] = $"members[value eq \"{first.Id}\"]"
                },
                new JsonObject
                {
                    ["op"] = "add",
                    ["path"] = "members",
                    ["value"] = new JsonArray(new JsonObject
                    {
                        ["value"] = third.Id,
                        ["display"] = "Provider delta third"
                    })
                })
        };
        using var oktaResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", oktaPatch);
        Assert.AreEqual(HttpStatusCode.NoContent, oktaResponse.StatusCode, await oktaResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(0, (await oktaResponse.Content.ReadAsByteArrayAsync()).Length);
        CollectionAssert.AreEquivalent(
            new[] { second.Id, third.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));

        var entraPatch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "Remove",
                ["path"] = "members",
                ["value"] = new JsonArray(new JsonObject
                {
                    ["$ref"] = null,
                    ["value"] = second.Id
                })
            })
        };
        using var entraResponse = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", entraPatch);
        Assert.AreEqual(HttpStatusCode.NoContent, entraResponse.StatusCode, await entraResponse.Content.ReadAsStringAsync());
        CollectionAssert.AreEquivalent(
            new[] { third.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));
    }

    [TestMethod]
    public async Task PutGroup_UpdatesUriTarget_AndCannotCreateMissingResource()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var member = await host.CreateUserAsync("put-group-member");
        var targetId = await host.CreateGroupAsync("put-group-target");
        var otherId = await host.CreateGroupAsync("put-group-other");
        var replacement = new JsonObject
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
            ["externalId"] = "put-group-target-new-external-id",
            ["displayName"] = "PUT group renamed",
            ["members"] = MemberArray(member.Id)
        };

        using var putResponse = await host.SendJsonAsync(HttpMethod.Put, $"{ScimBasePath}/Groups/{targetId}", replacement);

        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);
        var updated = await ReadObjectAsync(putResponse);
        Assert.AreEqual(targetId, RequiredString(updated, "id"));
        Assert.AreEqual("PUT group renamed", RequiredString(updated, "displayName"));
        CollectionAssert.AreEquivalent(new[] { member.Id }, MemberIds(updated));
        Assert.AreEqual(
            "put-group-other",
            RequiredString(await host.GetResourceAsync($"{ScimBasePath}/Groups/{otherId}"), "displayName"));

        using var missingResponse = await host.SendJsonAsync(
            HttpMethod.Put,
            $"{ScimBasePath}/Groups/grp_missing",
            new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
                ["externalId"] = "missing-group-external",
                ["displayName"] = "Missing group",
                ["members"] = new JsonArray()
            });
        Assert.AreEqual(HttpStatusCode.NotFound, missingResponse.StatusCode);
    }

    [TestMethod]
    public async Task InvalidGroupMember_FailsWithoutApplyingAnyMembers()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var existing = await host.CreateUserAsync("atomic-existing");
        var validAddition = await host.CreateUserAsync("atomic-valid-addition");
        var groupId = await host.CreateGroupAsync("atomic-group", existing.Id);
        var patch = new JsonObject
        {
            ["schemas"] = PatchSchemas(),
            ["Operations"] = new JsonArray(new JsonObject
            {
                ["op"] = "add",
                ["path"] = "members",
                ["value"] = MemberArray(validAddition.Id, "usr_does_not_exist")
            })
        };

        using var response = await host.SendJsonAsync(HttpMethod.Patch, $"{ScimBasePath}/Groups/{groupId}", patch);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await ReadObjectAsync(response);
        Assert.AreEqual("invalidValue", error["scimType"]?.GetValue<string>());
        CollectionAssert.AreEquivalent(
            new[] { existing.Id },
            MemberIds(await host.GetResourceAsync($"{ScimBasePath}/Groups/{groupId}")));
    }

    [TestMethod]
    public async Task AttributeProjection_SupportsQualifiedNestedPathsAndNestedExclusions()
    {
        await using var host = await ScimTestHost.CreateAsync();
        var payload = UserPayload(
            "projection-user",
            "projection-user@login.example.test",
            "projection-user@mail.example.test",
            "Ada Lovelace");
        payload["name"] = new JsonObject
        {
            ["formatted"] = "Ada Lovelace",
            ["givenName"] = "Ada",
            ["familyName"] = "Lovelace"
        };
        using var createResponse = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode, await createResponse.Content.ReadAsStringAsync());
        var userId = RequiredString(await ReadObjectAsync(createResponse), "id");
        var qualifiedGivenName = Uri.EscapeDataString(
            "urn:ietf:params:scim:schemas:core:2.0:User:name.givenName");

        using var includedResponse = await host.Client.GetAsync(
            $"{ScimBasePath}/Users/{userId}?attributes=userName,{qualifiedGivenName},emails.value");
        var included = await ReadObjectAsync(includedResponse);

        Assert.AreEqual(HttpStatusCode.OK, includedResponse.StatusCode);
        Assert.AreEqual("projection-user@login.example.test", RequiredString(included, "userName"));
        Assert.AreEqual("Ada", included["name"]?["givenName"]?.GetValue<string>());
        Assert.AreEqual("projection-user@mail.example.test", included["emails"]?[0]?["value"]?.GetValue<string>());
        Assert.IsFalse(included.ContainsKey("meta"));
        Assert.IsFalse(included.ContainsKey("displayName"));
        Assert.IsNull(included["name"]?["formatted"]);
        Assert.IsNull(included["name"]?["familyName"]);
        Assert.IsNull(included["emails"]?[0]?["type"]);
        Assert.IsNull(included["emails"]?[0]?["primary"]);

        using var excludedResponse = await host.Client.GetAsync(
            $"{ScimBasePath}/Users/{userId}?excludedAttributes=meta,emails.type");
        var excluded = await ReadObjectAsync(excludedResponse);
        Assert.AreEqual(HttpStatusCode.OK, excludedResponse.StatusCode);
        Assert.IsFalse(excluded.ContainsKey("meta"));
        Assert.AreEqual("projection-user@mail.example.test", excluded["emails"]?[0]?["value"]?.GetValue<string>());
        Assert.IsNull(excluded["emails"]?[0]?["type"]);
        Assert.AreEqual(true, excluded["emails"]?[0]?["primary"]?.GetValue<bool>());
    }

    [TestMethod]
    public async Task AttributeProjection_RejectsAttributesAndExcludedAttributesTogether()
    {
        await using var host = await ScimTestHost.CreateAsync();
        using var emptyListResponse = await host.Client.GetAsync(
            $"{ScimBasePath}/Groups?attributes=displayName&excludedAttributes=members");
        Assert.AreEqual(HttpStatusCode.BadRequest, emptyListResponse.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(emptyListResponse))["scimType"]?.GetValue<string>());

        using var invalidCreate = await host.SendJsonAsync(
            HttpMethod.Post,
            $"{ScimBasePath}/Users?attributes=userName&excludedAttributes=meta",
            UserPayload(
                "invalid-projection-create",
                "invalid-projection-create@example.test",
                "invalid-projection-create@example.test",
                "Must not persist"));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        var missingFilter = Uri.EscapeDataString("userName eq \"invalid-projection-create@example.test\"");
        using var missingResponse = await host.Client.GetAsync($"{ScimBasePath}/Users?filter={missingFilter}");
        Assert.AreEqual(0, (await ReadObjectAsync(missingResponse))["totalResults"]?.GetValue<int>());

        var user = await host.CreateUserAsync("conflicting-projection");

        using var response = await host.Client.GetAsync(
            $"{ScimBasePath}/Users/{user.Id}?attributes=userName&excludedAttributes=meta");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalidSyntax", (await ReadObjectAsync(response))["scimType"]?.GetValue<string>());
    }

    [TestMethod]
    public async Task WriteResponses_ApplyProjectionWithoutLosingLocationHeaders()
    {
        await using var host = await ScimTestHost.CreateAsync();
        using var createResponse = await host.SendJsonAsync(
            HttpMethod.Post,
            $"{ScimBasePath}/Users?attributes=userName",
            UserPayload(
                "write-projection",
                "write-projection@example.test",
                "write-projection@example.test",
                "Write Projection"));
        var created = await ReadObjectAsync(createResponse);
        var userId = RequiredString(created, "id");

        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.IsTrue(created.ContainsKey("userName"));
        Assert.IsFalse(created.ContainsKey("meta"));
        Assert.IsFalse(created.ContainsKey("displayName"));
        Assert.AreEqual($"{PublicOrigin}{ScimBasePath}/Users/{userId}", createResponse.Headers.Location?.AbsoluteUri);
        Assert.AreEqual(createResponse.Headers.Location, createResponse.Content.Headers.ContentLocation);

        var replacement = UserPayload(
            "write-projection",
            "write-projection@example.test",
            "write-projection@example.test",
            "Projected PUT");
        using var putResponse = await host.SendJsonAsync(
            HttpMethod.Put,
            $"{ScimBasePath}/Users/{userId}?attributes=displayName",
            replacement);
        var put = await ReadObjectAsync(putResponse);
        Assert.AreEqual(HttpStatusCode.OK, putResponse.StatusCode);
        Assert.AreEqual("Projected PUT", RequiredString(put, "displayName"));
        Assert.IsFalse(put.ContainsKey("userName"));
        Assert.IsFalse(put.ContainsKey("meta"));
        Assert.AreEqual($"{PublicOrigin}{ScimBasePath}/Users/{userId}", putResponse.Content.Headers.ContentLocation?.AbsoluteUri);

        using var patchResponse = await host.SendJsonAsync(
            HttpMethod.Patch,
            $"{ScimBasePath}/Users/{userId}?excludedAttributes=meta,emails",
            new JsonObject
            {
                ["schemas"] = PatchSchemas(),
                ["Operations"] = new JsonArray(new JsonObject
                {
                    ["op"] = "replace",
                    ["path"] = "displayName",
                    ["value"] = "Projected PATCH"
                })
            });
        var patched = await ReadObjectAsync(patchResponse);
        Assert.AreEqual(HttpStatusCode.OK, patchResponse.StatusCode);
        Assert.AreEqual("Projected PATCH", RequiredString(patched, "displayName"));
        Assert.IsFalse(patched.ContainsKey("meta"));
        Assert.IsFalse(patched.ContainsKey("emails"));
        Assert.AreEqual($"{PublicOrigin}{ScimBasePath}/Users/{userId}", patchResponse.Content.Headers.ContentLocation?.AbsoluteUri);
    }

    [TestMethod]
    public async Task GroupDiscovery_AdvertisesOnlySupportedUserMemberReferences()
    {
        await using var host = await ScimTestHost.CreateAsync();

        using var response = await host.Client.GetAsync(
            $"{ScimBasePath}/Schemas/{Uri.EscapeDataString("urn:ietf:params:scim:schemas:core:2.0:Group")}");
        var schema = await ReadObjectAsync(response);
        var members = schema["attributes"]?.AsArray()
            .Select(attribute => attribute?.AsObject())
            .Single(attribute => attribute?["name"]?.GetValue<string>() == "members");
        var displayName = schema["attributes"]?.AsArray()
            .Select(attribute => attribute?.AsObject())
            .Single(attribute => attribute?["name"]?.GetValue<string>() == "displayName");
        var reference = members?["subAttributes"]?.AsArray()
            .Select(attribute => attribute?.AsObject())
            .Single(attribute => attribute?["name"]?.GetValue<string>() == "$ref");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            schema["meta"]?["location"]?.GetValue<string>(),
            response.Content.Headers.ContentLocation?.AbsoluteUri);
        Assert.AreEqual("server", displayName?["uniqueness"]?.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "User" },
            reference?["referenceTypes"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray());
    }

    [TestMethod]
    public async Task CountZero_ReturnsOnlyTheTotal()
    {
        await using var host = await ScimTestHost.CreateAsync();
        await host.CreateUserAsync("pagination-user");

        using var response = await host.Client.GetAsync($"{ScimBasePath}/Users?startIndex=1&count=0");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadObjectAsync(response);
        Assert.AreEqual(1, body["totalResults"]?.GetValue<int>());
        Assert.AreEqual(0, body["itemsPerPage"]?.GetValue<int>());
        Assert.AreEqual(0, body["Resources"]?.AsArray().Count);
    }

    [TestMethod]
    public async Task SyncHistory_DoesNotPersistScimPassword()
    {
        await using var host = await ScimTestHost.CreateAsync();
        const string secret = "never-persist-this-scim-password";
        var payload = UserPayload("secret-user", "secret-user@example.test", "secret-user@example.test", "Secret User");
        payload["password"] = secret;

        using var response = await host.SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Users", payload);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
        var syncEvents = await context.Set<SqlOSScimSyncEvent>()
            .Where(item => item.ResourceType == "User")
            .ToListAsync();
        Assert.IsTrue(syncEvents.Count > 0);
        Assert.IsFalse(
            syncEvents.Any(item => item.DataJson?.Contains(secret, StringComparison.Ordinal) == true),
            "SCIM sync history must not retain write-only password values.");
    }

    private static JsonObject UserPayload(
        string externalId,
        string userName,
        string email,
        string displayName)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["externalId"] = externalId,
            ["userName"] = userName,
            ["displayName"] = displayName,
            ["active"] = true,
            ["emails"] = new JsonArray(new JsonObject
            {
                ["value"] = email,
                ["type"] = "work",
                ["primary"] = true
            })
        };

    private static JsonArray PatchSchemas()
        => new("urn:ietf:params:scim:api:messages:2.0:PatchOp");

    private static JsonArray MemberArray(params string[] ids)
        => new(ids.Select(id => (JsonNode)new JsonObject { ["value"] = id }).ToArray());

    private static string[] MemberIds(JsonObject group)
        => group["members"]?.AsArray()
            .Select(member => member?["value"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray()
        ?? [];

    private static string RequiredString(JsonObject value, string propertyName)
        => value[propertyName]?.GetValue<string>()
            ?? throw new AssertFailedException($"Expected SCIM response property '{propertyName}'.");

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(json) as JsonObject
            ?? throw new AssertFailedException($"Expected a SCIM JSON object but received: {json}");
    }

    private static async Task<JsonObject> RecordContentLocationAgreementAsync(
        HttpResponseMessage response,
        string operation,
        ICollection<string> failures)
    {
        var body = await ReadObjectAsync(response);
        var metaLocation = body["meta"]?["location"]?.GetValue<string>();
        var contentLocation = response.Content.Headers.ContentLocation?.AbsoluteUri;
        if (metaLocation is null)
        {
            failures.Add($"{operation} response did not include meta.location.");
        }
        else if (!string.Equals(metaLocation, contentLocation, StringComparison.Ordinal))
        {
            failures.Add($"{operation} Content-Location '{contentLocation ?? "<missing>"}' did not match meta.location '{metaLocation}'.");
        }

        return body;
    }

    private static void RecordExpectedStatus(
        HttpResponseMessage response,
        HttpStatusCode expected,
        string operation,
        ICollection<string> failures)
    {
        if (response.StatusCode != expected)
        {
            failures.Add($"{operation} returned {(int)response.StatusCode}, expected {(int)expected}.");
        }
    }

    private static void RecordAbsentProperty(
        JsonObject resource,
        string propertyName,
        string resourceDescription,
        ICollection<string> failures)
    {
        if (resource.ContainsKey(propertyName))
        {
            failures.Add($"{resourceDescription} fabricated or retained '{propertyName}'.");
        }
    }

    private static void AssertScimContentType(HttpResponseMessage response)
        => Assert.AreEqual("application/scim+json", response.Content.Headers.ContentType?.MediaType);

    private sealed class ScimTestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScimTestHost(WebApplication app, HttpClient client, HttpClient anonymousClient)
        {
            _app = app;
            Client = client;
            AnonymousClient = anonymousClient;
        }

        public HttpClient Client { get; }
        public HttpClient AnonymousClient { get; }
        public IServiceProvider Services => _app.Services;

        public static async Task<ScimTestHost> CreateAsync(bool enableScim = true)
        {
            var databaseName = $"scim-http-{Guid.NewGuid():N}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<TestSqlOSInMemoryDbContext>(options =>
                options.UseInMemoryDatabase(databaseName));
            builder.Services.AddSqlOS<TestSqlOSInMemoryDbContext>(options =>
            {
                options.AuthServer.BasePath = "/sqlos/auth";
                options.AuthServer.Issuer = $"{PublicOrigin}/sqlos/auth";
                options.AuthServer.PublicOrigin = PublicOrigin;
                options.AuthServer.EnableScim = enableScim;
                options.AuthServer.ScimBasePath = ScimBasePath;
            });
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.RemoveAll<IStartupFilter>();

            var app = builder.Build();
            app.MapAuthServer("/sqlos/auth");
            await app.StartAsync();

            var anonymousClient = app.GetTestClient();
            anonymousClient.BaseAddress = new Uri(PublicOrigin);
            var client = app.GetTestClient();
            client.BaseAddress = new Uri(PublicOrigin);

            if (enableScim)
            {
                await using var scope = app.Services.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
                var organization = new SqlOSOrganization
                {
                    Id = "org_scim_http",
                    Slug = "scim-http",
                    Name = "SCIM HTTP Tests",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                };
                var token = $"scim_http_{Guid.NewGuid():N}";
                var crypto = scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>();
                context.Set<SqlOSOrganization>().Add(organization);
                context.Set<SqlOSScimConnection>().Add(new SqlOSScimConnection
                {
                    Id = "scim_http_connection",
                    OrganizationId = organization.Id,
                    Organization = organization,
                    DisplayName = "Provider compatibility",
                    IsEnabled = true,
                    TokenHash = crypto.HashToken(token),
                    TokenPrefix = token[..12],
                    TokenRotatedAt = DateTime.UtcNow,
                    Source = SqlOSScimSources.Dashboard,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                await context.SaveChangesAsync();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return new ScimTestHost(app, client, anonymousClient);
        }

        public async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, JsonObject payload)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/scim+json")
            };
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> SendAnonymousJsonAsync(HttpMethod method, string path, JsonObject payload)
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            return await AnonymousClient.SendAsync(request);
        }

        public async Task<JsonObject> GetResourceAsync(string path)
        {
            using var response = await Client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return await ReadObjectAsync(response);
        }

        public async Task<TestUser> CreateUserAsync(string key)
        {
            var userName = $"{key}@login.example.test";
            using var response = await SendJsonAsync(
                HttpMethod.Post,
                $"{ScimBasePath}/Users",
                UserPayload($"external-{key}", userName, $"{key}@mail.example.test", key));
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            var body = await ReadObjectAsync(response);
            return new TestUser(RequiredString(body, "id"), userName);
        }

        public async Task<string> CreateGroupAsync(string key, params string[] memberIds)
        {
            var payload = new JsonObject
            {
                ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Group"),
                ["externalId"] = $"external-{key}",
                ["displayName"] = key,
                ["members"] = MemberArray(memberIds)
            };
            using var response = await SendJsonAsync(HttpMethod.Post, $"{ScimBasePath}/Groups", payload);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            return RequiredString(await ReadObjectAsync(response), "id");
        }

        public async Task SeedUnmanagedUserAsync(string userId)
        {
            await using var scope = Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TestSqlOSInMemoryDbContext>();
            var now = DateTime.UtcNow;
            context.Set<SqlOSUser>().Add(new SqlOSUser
            {
                Id = userId,
                DisplayName = "Unmanaged same-organization user",
                DefaultEmail = "unmanaged@example.test",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.Set<SqlOSMembership>().Add(new SqlOSMembership
            {
                OrganizationId = "org_scim_http",
                UserId = userId,
                Role = "member",
                IsActive = true,
                CreatedAt = now
            });
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            AnonymousClient.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed record TestUser(string Id, string UserName);
}
