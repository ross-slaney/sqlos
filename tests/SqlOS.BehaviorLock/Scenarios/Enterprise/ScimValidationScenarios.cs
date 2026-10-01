using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;
using SqlOS.BehaviorLock.Infrastructure.Transcripts;

namespace SqlOS.BehaviorLock.Scenarios.Enterprise;

/// <summary>
/// How SCIM refuses malformed or conflicting requests: media type, body size, JSON syntax, schema
/// URNs, attribute shapes, uniqueness, and every PatchOp rule, each with its SCIM error type.
/// </summary>
[TestClass]
public sealed class ScimValidationScenarios
{
    [Scenario]
    [Covers("POST /scim/v2/Users")]
    [Covers("PUT /scim/v2/Users/{id}")]
    public async Task User_writes_with_malformed_or_conflicting_bodies_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var judy = t.Unique.Email("judy", domain);
        var ann = t.Unique.Email("ann", domain);
        var users = $"{Scim.Root}/Users";

        Task<HttpExchange> Raw(string body, string contentType = "application/scim+json")
            => directory.SendAsync(HttpMethod.Post, users, new StringContent(body, Encoding.UTF8, contentType), options => options.Bearer(connection.Token));

        var id = t.Observe(
            await directory.PostAsync(users, Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "provision Judy").JsonString("id");
        t.Scrub(id, "usr", "judy");

        t.Observe(await Raw(Scim.User(ann).ToJsonString(), "text/plain"), "a body that is not JSON by media type");
        t.Observe(await Raw(string.Empty), "an empty body");
        t.Observe(await Raw("[]"), "a JSON array instead of an object");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": }"), "JSON with a syntax error");
        t.Observe(await Raw("{\"userName\": \"" + ann + "\"}"), "no schemas");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\", \"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\"}"),
            "a repeated schema URN");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\", \"urn:ietf:params:scim:schemas:core:2.0:Group\"], \"userName\": \"" + ann + "\"}"),
            "the User and Group core schemas together");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:Group\"], \"userName\": \"" + ann + "\"}"), "the Group schema on a user");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"externalId\": \"directory-ann\"}"), "no userName");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": 42}"), "a numeric userName");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + new string('u', 451) + "\"}"),
            "a userName longer than 450 characters");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"emails\": {\"value\": \"" + ann + "\"}}"),
            "emails as an object instead of an array");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"emails\": [\"" + ann + "\"]}"),
            "emails as strings instead of objects");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"emails\": [{\"value\": \"" + ann + "\", \"primary\": true}, {\"value\": \"second@" + domain + "\", \"primary\": true}]}"),
            "two primary emails");
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"emails\": [{\"type\": \"work\"}]}"),
            "an email without a value");
        t.Observe(await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"active\": \"yes\"}"), "active as a string");
        t.Observe(
            await directory.PostAsync(users, Scim.User(ann, "directory-ann", "Ann", "Archer", "not-an-email-address"), connection.Token),
            "an email value that is not an address");
        t.Observe(
            await directory.PostAsync(users, Scim.User(judy, "directory-judy-2", "Judy", "Hopps", judy), connection.Token),
            "a second user with Judy's userName");
        t.Observe(
            await directory.PostAsync(users, Scim.User(ann, "directory-judy", "Ann", "Archer", ann), connection.Token),
            "a second user with Judy's externalId");
        t.Observe(
            await directory.PostAsync($"{users}?attributes=userName&excludedAttributes=emails", Scim.User(ann, "directory-ann", "Ann", "Archer", ann), connection.Token),
            "attributes and excludedAttributes together on a write");
        // A 300,000-character value made of "aB3" renders as one detected {token#n} placeholder.
        t.Observe(
            await Raw("{\"schemas\": [\"urn:ietf:params:scim:schemas:core:2.0:User\"], \"userName\": \"" + ann + "\", \"displayName\": \"" + string.Concat(Enumerable.Repeat("aB3", 100_000)) + "\"}"),
            "a body larger than 256 KiB");
        t.Observe(
            await directory.PutAsync($"{users}/{id}", Scim.User(ann, "directory-judy", "Judy", "Hopps", judy), connection.Token),
            "a replace that keeps Judy's externalId but takes a new userName is accepted");
        t.Observe(
            await directory.PostAsync(users, Scim.User(judy, "directory-judy-3", "Judy", "Again", judy), connection.Token),
            "Judy's email now belongs to a user this directory already links");

        await t.ObserveAuditAsync("only the accepted writes are audited");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PATCH /scim/v2/Users/{id}")]
    public async Task User_patch_operations_that_break_patch_rules_are_refused()
    {
        await using var t = await Transcript.StartAsync(HostProfiles.EnterpriseScimPath);
        var acme = await t.Setup.CreateOrganizationAsync("acme");
        var domain = await t.VerifyDomainAsync(acme, "acme");
        var connection = await t.CreateDirectoryAsync(acme);
        var directory = t.NewClient("directory");
        var judy = t.Unique.Email("judy", domain);
        var id = t.Discard(await directory.PostAsync($"{Scim.Root}/Users", Scim.User(judy, "directory-judy", "Judy", "Hopps", judy), connection.Token)).JsonString("id");
        t.Scrub(id, "usr", "judy");
        var ann = t.Unique.Email("ann", domain);
        t.Scrub(t.Discard(await directory.PostAsync($"{Scim.Root}/Users", Scim.User(ann, "directory-ann", "Ann", "Archer", ann), connection.Token)).JsonString("id"), "usr", "ann");
        await t.SkipAuditAsync();
        t.Note("Judy and Ann were provisioned at Acme's verified domain.");
        var user = $"{Scim.Root}/Users/{id}";

        Task<HttpExchange> Patch(JsonObject body) => directory.PatchAsync(user, body, connection.Token);

        t.Observe(await Patch(new JsonObject { ["schemas"] = new JsonArray(Scim.PatchOpSchema) }), "no Operations");
        t.Observe(await Patch(new JsonObject { ["schemas"] = new JsonArray(Scim.UserSchema), ["Operations"] = new JsonArray() }), "the User schema instead of PatchOp");
        t.Observe(await Patch(Scim.Patch(("move", "displayName", JsonValue.Create("Judy")))), "an unknown op");
        t.Observe(await Patch(Scim.Patch(("replace", "displayName", null))), "replace without a value");
        t.Observe(await Patch(Scim.Patch(("remove", null, null))), "remove without a path");
        t.Observe(await Patch(Scim.Patch(("replace", null, JsonValue.Create("Judy")))), "a pathless operation whose value is not an object");
        t.Observe(await Patch(Scim.Patch(("replace", "id", JsonValue.Create("usr_ffffffffffffffffffffffffffffffff")))), "changing the resource id");
        t.Observe(await Patch(Scim.Patch(("replace", "id", JsonValue.Create(id)))), "re-sending the same id is accepted");
        t.Observe(await Patch(Scim.Patch(("add", "groups", Scim.Members("grp_ffffffffffffffffffffffffffffffff")))), "writing groups on a user");
        t.Observe(await Patch(Scim.Patch(("replace", "password", JsonValue.Create("Hunter2!")))), "writing a password");
        t.Observe(await Patch(Scim.Patch(("remove", "userName", null))), "removing userName");
        t.Observe(await Patch(Scim.Patch(("replace", "nickName", JsonValue.Create("J")))), "an attribute SqlOS does not support");
        t.Observe(await Patch(Scim.Patch(("replace", "emails[type eq \"home\"].value", JsonValue.Create(judy)))), "a filtered email path other than work");
        t.Observe(await Patch(Scim.Patch(("replace", "active", JsonValue.Create("false")))), "active as a string");
        t.Observe(await Patch(Scim.Patch(("replace", "name", JsonValue.Create("Judy Hopps")))), "name as a string");
        t.Observe(await Patch(Scim.Patch(("replace", "displayName", JsonValue.Create(" ")))), "a blank displayName");
        t.Observe(await Patch(Scim.Patch(("replace", "emails[type eq \"work\"].value", JsonValue.Create(ann)))), "Ann's address, which belongs to another account");
        t.Observe(
            await Patch(Scim.Patch(Enumerable.Range(0, 101).Select(index => ("replace", (string?)"displayName", (JsonNode?)JsonValue.Create($"Judy {index}"))).ToArray())),
            "more than 100 operations");
        t.Observe(
            await Patch(Scim.Patch(("replace", $"{Scim.UserSchema}:name.familyName", JsonValue.Create("Hopps-Wilde")))),
            "a schema-qualified path is accepted");

        await t.ObserveAuditAsync("only the accepted patches are audited");
        await t.ApproveAsync();
    }
}
