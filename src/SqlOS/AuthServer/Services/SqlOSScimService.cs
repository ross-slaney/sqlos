using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuditLogs;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;

namespace SqlOS.AuthServer.Services;

internal sealed class SqlOSScimException : InvalidOperationException
{
    public SqlOSScimException(int statusCode, string message, string? scimType = null)
        : base(message)
    {
        StatusCode = statusCode;
        ScimType = scimType;
    }

    public int StatusCode { get; }
    public string? ScimType { get; }
}

internal sealed class SqlOSScimService
{
    private const string UserSchema = "urn:ietf:params:scim:schemas:core:2.0:User";
    private const string GroupSchema = "urn:ietf:params:scim:schemas:core:2.0:Group";
    private const string ListResponseSchema = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    private const string PatchOpSchema = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    private const string ErrorSchema = "urn:ietf:params:scim:api:messages:2.0:Error";
    private const int MaxAuditedMembershipSubjectIds = 100;
    private const int ScimOperationCommitCleanupBatchSize = 256;
    private static readonly TimeSpan ScimOperationCommitRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan TokenUsageWriteInterval = TimeSpan.FromMinutes(5);

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSScimService(
        ISqlOSAuthServerDbContext context,
        IOptions<SqlOSAuthServerOptions> options,
        SqlOSCryptoService cryptoService)
    {
        _context = context;
        _options = options.Value;
        _cryptoService = cryptoService;
    }

    public static JsonObject CreateError(int statusCode, string message, string? scimType = null)
    {
        var error = new JsonObject
        {
            ["schemas"] = new JsonArray(ErrorSchema),
            ["status"] = statusCode.ToString(),
            ["detail"] = message
        };
        if (!string.IsNullOrWhiteSpace(scimType))
        {
            error["scimType"] = scimType;
        }

        return error;
    }

    public async Task<SqlOSScimConnection> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableScim)
        {
            throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM is not enabled.");
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlOSScimException(StatusCodes.Status401Unauthorized, "SCIM bearer token is required.");
        }

        var token = header["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SqlOSScimException(StatusCodes.Status401Unauthorized, "SCIM bearer token is required.");
        }

        var tokenHash = _cryptoService.HashToken(token);
        var connection = await _context.Set<SqlOSScimConnection>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash && x.IsEnabled, cancellationToken);

        if (connection?.Organization == null || !connection.Organization.IsActive)
        {
            throw new SqlOSScimException(StatusCodes.Status401Unauthorized, "Invalid SCIM bearer token.");
        }

        var now = DateTime.UtcNow;
        if (connection.TokenLastUsedAt == null || connection.TokenLastUsedAt <= now - TokenUsageWriteInterval)
        {
            connection.TokenLastUsedAt = now;
            await _context.SaveChangesAsync(cancellationToken);
        }
        return connection;
    }

    public JsonObject GetServiceProviderConfig()
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig"),
            ["documentationUri"] = "https://sqlos.dev/docs/authserver/scim-directory-sync",
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject { ["supported"] = false, ["maxOperations"] = 0, ["maxPayloadSize"] = 0 },
            ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = 200 },
            ["changePassword"] = new JsonObject { ["supported"] = false },
            ["sort"] = new JsonObject { ["supported"] = false },
            ["etag"] = new JsonObject { ["supported"] = false },
            ["authenticationSchemes"] = new JsonArray(new JsonObject
            {
                ["name"] = "Bearer Token",
                ["description"] = "Opaque bearer token scoped to one SqlOS organization and directory connection.",
                ["type"] = "oauthbearertoken",
                ["primary"] = true
            }),
            ["meta"] = new JsonObject { ["location"] = BuildScimLocation("ServiceProviderConfig") }
        };

    public JsonObject GetResourceTypes()
    {
        var resources = new JsonArray(BuildResourceType("User", "/Users", UserSchema), BuildResourceType("Group", "/Groups", GroupSchema));
        return ListResponse(resources, resources.Count, 1);
    }

    public JsonObject GetResourceType(string id)
        => id.Equals("User", StringComparison.OrdinalIgnoreCase)
            ? BuildResourceType("User", "/Users", UserSchema)
            : id.Equals("Group", StringComparison.OrdinalIgnoreCase)
                ? BuildResourceType("Group", "/Groups", GroupSchema)
                : throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM resource type not found.");

    public JsonObject GetSchemas()
    {
        var resources = new JsonArray(BuildUserSchema(), BuildGroupSchema());
        return ListResponse(resources, resources.Count, 1);
    }

    public JsonObject GetSchema(string id)
        => id.Equals(UserSchema, StringComparison.OrdinalIgnoreCase)
            ? BuildUserSchema()
            : id.Equals(GroupSchema, StringComparison.OrdinalIgnoreCase)
                ? BuildGroupSchema()
                : throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM schema not found.");

    public string GetResourceLocation(string resourceType, JsonObject resource)
    {
        var id = ReadString(resource, "id");
        if (string.IsNullOrWhiteSpace(id) || resourceType is not ("Users" or "Groups"))
        {
            throw new InvalidOperationException("A SCIM resource location requires a supported resource type and id.");
        }
        return BuildScimLocation($"{resourceType}/{Uri.EscapeDataString(id)}");
    }

    public async Task<JsonObject> ListUsersAsync(
        SqlOSScimConnection connection,
        int? startIndex,
        int? count,
        string? filter,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
    {
        var selection = CreateAttributeSelection(attributes, excludedAttributes);
        var query = _context.Set<SqlOSScimExternalId>()
            .AsNoTracking()
            .Where(x => x.ConnectionId == connection.Id && x.ResourceType == "User" && x.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var (attribute, expected) = ReadEqFilter(filter, "id", "userName", "externalId", "emails.value");
            query = attribute switch
            {
                "id" => query.Where(x => x.EntityId == expected),
                "username" => query.Where(x => x.UserName == expected),
                "externalid" => query.Where(x => x.ExternalId == expected),
                "emails.value" => query.Where(x => x.PrimaryEmail == expected),
                _ => query.Where(_ => false)
            };
        }

        var total = await query.CountAsync(cancellationToken);
        var (skip, take, resolvedStart) = ResolveScimPaging(startIndex, count);
        var pageLinks = await query
            .OrderBy(x => x.UserName)
            .ThenBy(x => x.EntityId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        var userIds = pageLinks.Select(x => x.EntityId).ToList();
        var users = await _context.Set<SqlOSUser>()
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var membershipActivity = await _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Where(x => x.OrganizationId == connection.OrganizationId && userIds.Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, x => x.IsActive, cancellationToken);
        var groupsBySubject = new Dictionary<string, IReadOnlyList<SqlOSScimExternalId>>(StringComparer.Ordinal);
        if (selection.Includes("groups"))
        {
            var subjectIds = pageLinks
                .Select(link => link.FgaSubjectId)
                .Where(subjectId => !string.IsNullOrWhiteSpace(subjectId))
                .Select(subjectId => subjectId!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
                .AsNoTracking()
                .Where(item => subjectIds.Contains(item.SubjectId))
                .Select(item => new { item.SubjectId, item.UserGroupId })
                .ToListAsync(cancellationToken);
            var groupIds = memberships.Select(item => item.UserGroupId).Distinct().ToList();
            var groupLinks = await _context.Set<SqlOSScimExternalId>()
                .AsNoTracking()
                .Where(item => item.ConnectionId == connection.Id
                    && item.ResourceType == "Group"
                    && item.DeletedAt == null
                    && groupIds.Contains(item.EntityId))
                .ToDictionaryAsync(item => item.EntityId, cancellationToken);
            groupsBySubject = memberships
                .Where(item => groupLinks.ContainsKey(item.UserGroupId))
                .GroupBy(item => item.SubjectId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SqlOSScimExternalId>)group
                        .Select(item => groupLinks[item.UserGroupId])
                        .ToList(),
                    StringComparer.Ordinal);
        }
        var resources = new JsonArray();
        foreach (var link in pageLinks)
        {
            if (users.TryGetValue(link.EntityId, out var user))
            {
                membershipActivity.TryGetValue(link.EntityId, out var membershipIsActive);
                groupsBySubject.TryGetValue(link.FgaSubjectId ?? string.Empty, out var groupLinks);
                resources.Add(ToScimUser(
                    user,
                    link,
                    selection,
                    membershipIsActive,
                    groupLinks ?? []));
            }
        }

        return ListResponse(resources, total, resolvedStart);
    }

    public async Task<JsonObject> GetUserAsync(
        SqlOSScimConnection connection,
        string id,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
    {
        var link = await GetRequiredUserLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
        var user = await _context.Set<SqlOSUser>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == link.EntityId, cancellationToken)
            ?? throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM user not found.");
        return await ToScimUserAsync(connection, user, link, attributes, excludedAttributes, cancellationToken);
    }

    public Task<JsonObject> CreateUserAsync(
        SqlOSScimConnection connection,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var request = ParseUser(payload);
            var existing = await FindUserLinkAsync(connection.Id, request.UserName, request.ExternalId, cancellationToken);
            if (existing is { DeletedAt: null })
            {
                throw new SqlOSScimException(StatusCodes.Status409Conflict, "A SCIM user with the same userName or externalId already exists.", "uniqueness");
            }
            if (existing is { DeletedAt: not null }
                && (string.IsNullOrWhiteSpace(request.ExternalId)
                    || !string.Equals(existing.ExternalId, request.ExternalId, StringComparison.Ordinal)))
            {
                throw new SqlOSScimException(
                    StatusCodes.Status409Conflict,
                    "The SCIM userName belongs to a deleted resource with a different externalId and cannot be reassigned implicitly.",
                    "uniqueness");
            }

            return await WriteUserCoreAsync(connection, existing, request, payload, createdResource: true, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    public Task<JsonObject> ReplaceUserAsync(
        SqlOSScimConnection connection,
        string id,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredUserLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            return await WriteUserCoreAsync(connection, link, ParseUser(payload), payload, createdResource: false, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    // Kept as an internal reconciliation helper for code-first tests and seed flows. HTTP POST uses CreateUserAsync.
    public Task<JsonObject> UpsertUserAsync(SqlOSScimConnection connection, JsonObject payload, bool replace, CancellationToken cancellationToken = default)
        => RunAtomicAsync(async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var request = ParseUser(payload, validateSchemas: false);
            var routeId = ReadString(payload, "id");
            var link = !string.IsNullOrWhiteSpace(routeId)
                ? await GetRequiredUserLinkAsync(connection.Id, routeId, includeDeleted: false, cancellationToken)
                : await FindUserLinkAsync(connection.Id, request.UserName, request.ExternalId, cancellationToken);
            return await WriteUserCoreAsync(connection, link, request, payload, createdResource: link == null, null, null, cancellationToken);
        }, cancellationToken);

    public Task<JsonObject> PatchUserAsync(
        SqlOSScimConnection connection,
        string id,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredUserLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            var state = new ScimUserState
            {
                ResourceId = id,
                UserName = link.UserName ?? link.PrimaryEmail ?? link.EntityId,
                ExternalId = link.ExternalId,
                PrimaryEmail = link.PrimaryEmail,
                DisplayName = link.DisplayName ?? link.UserName ?? link.EntityId,
                FormattedName = link.FormattedName ?? BuildFormattedName(link.GivenName, link.FamilyName) ?? link.DisplayName,
                GivenName = link.GivenName,
                FamilyName = link.FamilyName,
                Active = link.IsActive
            };
            foreach (var operation in ReadOperations(payload))
            {
                ApplyUserPatchOperation(state, operation);
            }

            var request = state.ToRequest();
            return await WriteUserCoreAsync(connection, link, request, payload, createdResource: false, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    public Task DeleteUserAsync(SqlOSScimConnection connection, string id, CancellationToken cancellationToken = default)
        => RunAtomicAsync(async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredUserLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            var user = await _context.Set<SqlOSUser>().FirstOrDefaultAsync(x => x.Id == link.EntityId, cancellationToken)
                ?? throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM user not found.");
            var now = DateTime.UtcNow;
            link.IsActive = false;
            link.DeletedAt = now;
            link.UpdatedAt = now;
            link.LastSyncedAt = now;
            await DeprovisionUserAccessAsync(connection, user.Id, link.FgaSubjectId, cancellationToken);
            if (await IsScimManagedUserLifecycleAsync(user.Id, link.OwnsUserLifecycle, cancellationToken))
            {
                await RefreshGlobalUserActivityAsync(user, connection.OrganizationId, now, cancellationToken);
            }
            connection.LastSyncAt = now;
            await RecordSyncEventAsync(connection, "User", user.Id, link.ExternalId, "scim.user.deleted", "success", null, null, cancellationToken);
            await RecordAuditAsync("scim.user.deleted", connection.OrganizationId, "User", user.Id, link.ExternalId, null, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    public async Task<JsonObject> ListGroupsAsync(
        SqlOSScimConnection connection,
        int? startIndex,
        int? count,
        string? filter,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
    {
        var selection = CreateAttributeSelection(attributes, excludedAttributes);
        var query = _context.Set<SqlOSScimExternalId>()
            .AsNoTracking()
            .Where(x => x.ConnectionId == connection.Id && x.ResourceType == "Group" && x.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var (attribute, expected) = ReadEqFilter(filter, "id", "displayName", "externalId");
            query = attribute switch
            {
                "id" => query.Where(x => x.EntityId == expected),
                "displayname" => query.Where(x => x.DisplayName == expected),
                "externalid" => query.Where(x => x.ExternalId == expected),
                _ => query.Where(_ => false)
            };
        }

        var total = await query.CountAsync(cancellationToken);
        var (skip, take, resolvedStart) = ResolveScimPaging(startIndex, count);
        var pageLinks = await query
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.EntityId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        var groupIds = pageLinks.Select(link => link.EntityId).ToList();
        var groups = await _context.Set<SqlOSFgaUserGroup>()
            .AsNoTracking()
            .Where(group => groupIds.Contains(group.Id))
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        var membersByGroup = new Dictionary<string, IReadOnlyList<SqlOSScimExternalId>>(StringComparer.Ordinal);
        if (selection.Includes("members"))
        {
            var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
                .AsNoTracking()
                .Where(item => groupIds.Contains(item.UserGroupId))
                .Select(item => new { item.UserGroupId, item.SubjectId })
                .ToListAsync(cancellationToken);
            var subjectIds = memberships.Select(item => item.SubjectId).Distinct().ToList();
            var userLinks = await _context.Set<SqlOSScimExternalId>()
                .AsNoTracking()
                .Where(item => item.ConnectionId == connection.Id
                    && item.ResourceType == "User"
                    && item.DeletedAt == null
                    && item.FgaSubjectId != null
                    && subjectIds.Contains(item.FgaSubjectId))
                .ToDictionaryAsync(item => item.FgaSubjectId!, cancellationToken);
            membersByGroup = memberships
                .Where(item => userLinks.ContainsKey(item.SubjectId))
                .GroupBy(item => item.UserGroupId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<SqlOSScimExternalId>)group
                        .Select(item => userLinks[item.SubjectId])
                        .ToList(),
                    StringComparer.Ordinal);
        }
        var resources = new JsonArray();
        foreach (var link in pageLinks)
        {
            if (groups.TryGetValue(link.EntityId, out var group))
            {
                membersByGroup.TryGetValue(link.EntityId, out var members);
                resources.Add(ToScimGroup(group, link, selection, members ?? []));
            }
        }

        return ListResponse(resources, total, resolvedStart);
    }

    public async Task<JsonObject> GetGroupAsync(
        SqlOSScimConnection connection,
        string id,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => await ToScimGroupAsync(
            connection,
            await GetRequiredGroupLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken),
            attributes,
            excludedAttributes,
            cancellationToken);

    public Task<JsonObject> CreateGroupAsync(
        SqlOSScimConnection connection,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var request = ParseGroup(payload);
            var existing = await FindGroupLinkAsync(connection.Id, request.DisplayName, request.ExternalId, cancellationToken);
            if (existing is { DeletedAt: null })
            {
                throw new SqlOSScimException(StatusCodes.Status409Conflict, "A SCIM group with the same displayName or externalId already exists.", "uniqueness");
            }
            if (existing is { DeletedAt: not null }
                && !string.Equals(existing.ExternalId, request.ExternalId, StringComparison.Ordinal))
            {
                throw new SqlOSScimException(
                    StatusCodes.Status409Conflict,
                    "The SCIM group displayName belongs to a deleted resource with a different externalId and cannot be reassigned implicitly.",
                    "uniqueness");
            }

            return await WriteGroupCoreAsync(connection, existing, request, payload, createdResource: true, includeResponseResource: true, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    public Task<JsonObject> ReplaceGroupAsync(
        SqlOSScimConnection connection,
        string id,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredGroupLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            return await WriteGroupCoreAsync(connection, link, ParseGroup(payload), payload, createdResource: false, includeResponseResource: true, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    public async Task<JsonObject> UpsertGroupAsync(SqlOSScimConnection connection, JsonObject payload, bool replace, CancellationToken cancellationToken = default)
        => await RunAtomicAsync(async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var request = ParseGroup(payload, validateSchemas: false);
            var routeId = ReadString(payload, "id");
            var link = !string.IsNullOrWhiteSpace(routeId)
                ? await GetRequiredGroupLinkAsync(connection.Id, routeId, includeDeleted: false, cancellationToken)
                : await FindGroupLinkAsync(connection.Id, request.DisplayName, request.ExternalId, cancellationToken);
            return await WriteGroupCoreAsync(connection, link, request, payload, createdResource: link == null, includeResponseResource: true, null, null, cancellationToken);
        }, cancellationToken);

    public Task<JsonObject> PatchGroupAsync(
        SqlOSScimConnection connection,
        string id,
        JsonObject payload,
        string? attributes = null,
        string? excludedAttributes = null,
        CancellationToken cancellationToken = default)
        => RunProjectedAtomicAsync(attributes, excludedAttributes, async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredGroupLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            var state = new ScimGroupState
            {
                ResourceId = id,
                DisplayName = link.DisplayName ?? link.EntityId,
                ExternalId = link.ExternalId,
                Members = await GetCurrentScimGroupMembersAsync(connection.Id, link.EntityId, cancellationToken)
            };
            foreach (var operation in ReadOperations(payload))
            {
                ApplyGroupPatchOperation(state, operation);
            }

            var includeResponseResource = !string.IsNullOrWhiteSpace(attributes) || !string.IsNullOrWhiteSpace(excludedAttributes);
            return await WriteGroupCoreAsync(connection, link, state.ToRequest(), payload, createdResource: false, includeResponseResource, attributes, excludedAttributes, cancellationToken);
        }, cancellationToken);

    public Task DeleteGroupAsync(SqlOSScimConnection connection, string id, CancellationToken cancellationToken = default)
        => RunAtomicAsync(async () =>
        {
            await LockAndEnsureConnectionAuthorityAsync(connection, cancellationToken);
            var link = await GetRequiredGroupLinkAsync(connection.Id, id, includeDeleted: false, cancellationToken);
            var group = await _context.Set<SqlOSFgaUserGroup>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (group != null)
            {
                var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
                    .Where(x => x.UserGroupId == group.Id)
                    .ToListAsync(cancellationToken);
                _context.Set<SqlOSFgaUserGroupMembership>().RemoveRange(memberships);
                await RevokeManagedGrantsForGroupAsync(connection, group.Id, cancellationToken);
            }

            link.IsActive = false;
            link.DeletedAt = DateTime.UtcNow;
            link.UpdatedAt = DateTime.UtcNow;
            connection.LastSyncAt = DateTime.UtcNow;
            await RecordSyncEventAsync(connection, "Group", id, link.ExternalId, "scim.group.deleted", "success", null, null, cancellationToken);
            await RecordAuditAsync("scim.group.deleted", connection.OrganizationId, "Group", id, link.ExternalId, null, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private async Task<JsonObject> WriteUserCoreAsync(
        SqlOSScimConnection connection,
        SqlOSScimExternalId? link,
        ScimUserRequest request,
        JsonObject sourcePayload,
        bool createdResource,
        string? attributes,
        string? excludedAttributes,
        CancellationToken cancellationToken)
    {
        var duplicate = await FindUserLinkAsync(connection.Id, request.UserName, request.ExternalId, cancellationToken);
        if (duplicate != null && duplicate.Id != link?.Id && duplicate.DeletedAt == null)
        {
            throw new SqlOSScimException(StatusCodes.Status409Conflict, "A SCIM user with the same userName or externalId already exists.", "uniqueness");
        }

        var now = DateTime.UtcNow;
        SqlOSUser? user = null;
        if (link != null)
        {
            user = await _context.Set<SqlOSUser>().FirstOrDefaultAsync(x => x.Id == link.EntityId, cancellationToken);
        }

        if (user == null && !string.IsNullOrWhiteSpace(request.PrimaryEmail))
        {
            var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.PrimaryEmail);
            user = await _context.Set<SqlOSUserEmail>()
                .Include(x => x.User)
                .Where(x => x.NormalizedEmail == normalizedEmail)
                .Select(x => x.User)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (user == null && LooksLikeEmail(request.UserName))
        {
            var normalizedUserName = SqlOSAdminService.NormalizeEmail(request.UserName);
            user = await _context.Set<SqlOSUserEmail>()
                .Include(x => x.User)
                .Where(x => x.NormalizedEmail == normalizedUserName)
                .Select(x => x.User)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (user != null)
        {
            var existingEntityLink = await _context.Set<SqlOSScimExternalId>()
                .FirstOrDefaultAsync(item => item.ConnectionId == connection.Id
                    && item.ResourceType == "User"
                    && item.EntityId == user.Id,
                    cancellationToken);
            if (existingEntityLink != null && existingEntityLink.Id != link?.Id)
            {
                throw new SqlOSScimException(
                    StatusCodes.Status409Conflict,
                    "A SCIM user in this directory connection already owns that email identity.",
                    "uniqueness");
            }
        }

        var createdUser = user == null;
        if (user == null)
        {
            user = new SqlOSUser
            {
                Id = _cryptoService.GenerateId("usr"),
                DisplayName = request.DisplayName,
                DefaultEmail = request.PrimaryEmail,
                IsActive = request.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.Set<SqlOSUser>().Add(user);
        }

        var hasOtherActiveMembership = await _context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == user.Id && x.OrganizationId != connection.OrganizationId && x.IsActive, cancellationToken);
        var ownsUserLifecycle = createdUser || link?.OwnsUserLifecycle == true;
        var lifecycleIsScimManaged = await IsScimManagedUserLifecycleAsync(user.Id, ownsUserLifecycle, cancellationToken);
        if (ownsUserLifecycle)
        {
            user.DisplayName = request.DisplayName;
            user.DefaultEmail = request.PrimaryEmail ?? user.DefaultEmail;
            if (!string.IsNullOrWhiteSpace(request.PrimaryEmail))
            {
                await UpsertPrimaryEmailAsync(user, request.PrimaryEmail, now, cancellationToken);
            }
        }
        if (lifecycleIsScimManaged)
        {
            user.IsActive = request.Active || hasOtherActiveMembership;
            user.UpdatedAt = now;
        }

        await UpsertMembershipAsync(connection.OrganizationId, user.Id, request.Active, now, cancellationToken);
        var fgaSubjectId = await EnsureFgaUserAsync(
            connection,
            user,
            request.DisplayName,
            request.PrimaryEmail,
            request.Active && user.IsActive,
            now,
            cancellationToken);
        link = await UpsertExternalLinkAsync(
            connection.Id,
            "User",
            request.ExternalId,
            user.Id,
            fgaSubjectId,
            request.UserName,
            request.PrimaryEmail,
            request.DisplayName,
            request.FormattedName,
            request.GivenName,
            request.FamilyName,
            request.Active,
            now,
            cancellationToken,
            link);
        link.OwnsUserLifecycle = ownsUserLifecycle;
        link.DeletedAt = null;

        if (!request.Active)
        {
            await DeprovisionUserAccessAsync(connection, user.Id, fgaSubjectId, cancellationToken);
        }

        connection.LastSyncAt = now;
        var action = createdResource
            ? link.CreatedAt == now && createdUser ? "scim.user.created" : "scim.user.reactivated"
            : request.Active ? "scim.user.updated" : "scim.user.deactivated";
        await RecordSyncEventAsync(connection, "User", user.Id, link.ExternalId, action, "success", null, sourcePayload, cancellationToken);
        await RecordAuditAsync(action, connection.OrganizationId, "User", user.Id, link.ExternalId, sourcePayload, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await ToScimUserAsync(connection, user, link, attributes, excludedAttributes, cancellationToken);
    }

    private async Task<JsonObject> WriteGroupCoreAsync(
        SqlOSScimConnection connection,
        SqlOSScimExternalId? link,
        ScimGroupRequest request,
        JsonObject sourcePayload,
        bool createdResource,
        bool includeResponseResource,
        string? attributes,
        string? excludedAttributes,
        CancellationToken cancellationToken)
    {
        var duplicate = await FindGroupLinkAsync(connection.Id, request.DisplayName, request.ExternalId, cancellationToken);
        if (duplicate != null && duplicate.Id != link?.Id && duplicate.DeletedAt == null)
        {
            throw new SqlOSScimException(StatusCodes.Status409Conflict, "A SCIM group with the same displayName or externalId already exists.", "uniqueness");
        }

        if (request.Members.Count > 10_000)
        {
            throw new SqlOSScimException(StatusCodes.Status413PayloadTooLarge, "SCIM group contains too many members.", "tooMany");
        }

        var desiredMemberSubjectIds = await ResolveMemberSubjectIdsAsync(connection.Id, request.Members, cancellationToken);

        var now = DateTime.UtcNow;
        var externalId = request.ExternalId;
        SqlOSFgaUserGroup? group = null;
        if (link != null)
        {
            group = await _context.Set<SqlOSFgaUserGroup>().FirstOrDefaultAsync(x => x.Id == link.EntityId, cancellationToken);
        }

        if (group == null)
        {
            await EnsureFgaSubjectTypeAsync("group", "Group", cancellationToken);
            var subject = new SqlOSFgaSubject
            {
                Id = _cryptoService.GenerateId("subj"),
                SubjectTypeId = "group",
                DisplayName = request.DisplayName,
                OrganizationId = connection.OrganizationId,
                ExternalRef = externalId,
                CreatedAt = now,
                UpdatedAt = now
            };
            group = new SqlOSFgaUserGroup
            {
                Id = _cryptoService.GenerateId("grp"),
                SubjectId = subject.Id,
                Name = request.DisplayName,
                Description = "SCIM mirrored group",
                GroupType = "scim",
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.Set<SqlOSFgaSubject>().Add(subject);
            _context.Set<SqlOSFgaUserGroup>().Add(group);
        }
        else
        {
            group.Name = request.DisplayName;
            group.UpdatedAt = now;
            var subject = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(x => x.Id == group.SubjectId, cancellationToken);
            if (subject != null)
            {
                subject.DisplayName = request.DisplayName;
                subject.ExternalRef = externalId;
                subject.UpdatedAt = now;
            }
        }

        link = await UpsertExternalLinkAsync(
            connection.Id,
            "Group",
            externalId,
            group.Id,
            group.SubjectId,
            null,
            null,
            request.DisplayName,
            null,
            null,
            null,
            active: true,
            now,
            cancellationToken,
            link);
        link.DeletedAt = null;
        await ReplaceGroupMembersAsync(connection, group, externalId, desiredMemberSubjectIds, cancellationToken);
        await ApplyGroupMappingsAsync(connection, group, externalId, request.DisplayName, cancellationToken);

        connection.LastSyncAt = now;
        var action = createdResource ? "scim.group.created" : "scim.group.updated";
        await RecordSyncEventAsync(connection, "Group", group.Id, externalId, action, "success", null, sourcePayload, cancellationToken);
        await RecordAuditAsync(action, connection.OrganizationId, "Group", group.Id, externalId, sourcePayload, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return includeResponseResource
            ? await ToScimGroupAsync(connection, link, attributes, excludedAttributes, cancellationToken)
            : new JsonObject { ["schemas"] = new JsonArray(GroupSchema), ["id"] = group.Id };
    }

    private async Task LockAndEnsureConnectionAuthorityAsync(
        SqlOSScimConnection connection,
        CancellationToken cancellationToken)
    {
        var authenticatedTokenHash = connection.TokenHash;
        SqlOSScimConnection? current;
        if (_context.Database.IsRelational()
            && string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            var schema = string.IsNullOrWhiteSpace(_options.Schema) ? "dbo" : _options.Schema.Trim();
            var quotedSchema = schema.Replace("]", "]]", StringComparison.Ordinal);
#pragma warning disable EF1002 // The schema is an escaped identifier; the connection id remains a SQL parameter.
            current = await _context.Set<SqlOSScimConnection>()
                .FromSqlRaw(
                    $"SELECT * FROM [{quotedSchema}].[SqlOSScimConnections] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = @connectionId",
                    new SqlParameter("@connectionId", connection.Id))
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
#pragma warning restore EF1002
        }
        else
        {
            current = await _context.Set<SqlOSScimConnection>()
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == connection.Id, cancellationToken);
        }

        var organizationIsActive = current != null
            && await _context.Set<SqlOSOrganization>()
                .AsNoTracking()
                .AnyAsync(item => item.Id == current.OrganizationId && item.IsActive, cancellationToken);
        if (current is not { IsEnabled: true }
            || !organizationIsActive
            || string.IsNullOrWhiteSpace(authenticatedTokenHash)
            || !string.Equals(current.TokenHash, authenticatedTokenHash, StringComparison.Ordinal))
        {
            throw new SqlOSScimException(StatusCodes.Status401Unauthorized, "The SCIM directory connection or bearer token is no longer valid.");
        }

        if (_context is not DbContext dbContext)
        {
            throw new InvalidOperationException("The SqlOS SCIM service requires an Entity Framework DbContext implementation.");
        }
        if (dbContext.Entry(connection).State == EntityState.Detached)
        {
            dbContext.Attach(connection);
        }
        dbContext.Entry(connection).CurrentValues.SetValues(current);
    }

    private async Task<T> RunAtomicAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction != null)
        {
            var result = await action();
            if (await StageExpiredScimOperationCommitsAsync(
                DateTime.UtcNow - ScimOperationCommitRetention,
                ScimOperationCommitCleanupBatchSize,
                cancellationToken) > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            return result;
        }

        var executionStrategy = _context.Database.CreateExecutionStrategy();
        var commitMarkerId = _cryptoService.GenerateId("evt");
        var attempt = 0;
        return await executionStrategy.ExecuteInTransactionAsync(
            async _ =>
            {
                if (attempt++ > 0 && _context is DbContext retryContext)
                {
                    retryContext.ChangeTracker.Clear();
                }
                var result = await action();
                await StageExpiredScimOperationCommitsAsync(
                    DateTime.UtcNow - ScimOperationCommitRetention,
                    ScimOperationCommitCleanupBatchSize,
                    cancellationToken);
                _context.Set<SqlOSScimOperationCommit>().Add(CreateScimProtocolCommitMarker(commitMarkerId));
                await _context.SaveChangesAsync(cancellationToken);
                return result;
            },
            async _ =>
            {
                if (_context is DbContext verificationContext)
                {
                    verificationContext.ChangeTracker.Clear();
                }
                return await _context.Set<SqlOSScimOperationCommit>()
                    .AsNoTracking()
                    .AnyAsync(item => item.Id == commitMarkerId, cancellationToken);
            },
            IsolationLevel.Serializable,
            cancellationToken);
    }

    private async Task<int> StageExpiredScimOperationCommitsAsync(
        DateTime cutoff,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var expired = await _context.Set<SqlOSScimOperationCommit>()
            .Where(marker => marker.OccurredAt < cutoff)
            .OrderBy(marker => marker.OccurredAt)
            .ThenBy(marker => marker.Id)
            .Take(maxRows)
            .ToListAsync(cancellationToken);
        _context.Set<SqlOSScimOperationCommit>().RemoveRange(expired);
        return expired.Count;
    }

    private Task RunAtomicAsync(Func<Task> action, CancellationToken cancellationToken)
        => RunAtomicAsync(async () =>
        {
            await action();
            return true;
        }, cancellationToken);

    private Task<T> RunProjectedAtomicAsync<T>(
        string? attributes,
        string? excludedAttributes,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        _ = CreateAttributeSelection(attributes, excludedAttributes);
        return RunAtomicAsync(action, cancellationToken);
    }

    private static SqlOSScimOperationCommit CreateScimProtocolCommitMarker(string id)
        => new()
        {
            Id = id,
            OccurredAt = DateTime.UtcNow
        };

    private async Task UpsertPrimaryEmailAsync(SqlOSUser user, string email, DateTime now, CancellationToken cancellationToken)
    {
        var normalized = SqlOSAdminService.NormalizeEmail(email);
        var existing = await _context.Set<SqlOSUserEmail>()
            .FirstOrDefaultAsync(x => x.NormalizedEmail == normalized, cancellationToken);
        if (existing != null && existing.UserId != user.Id)
        {
            throw new SqlOSScimException(StatusCodes.Status409Conflict, $"Email '{email}' already belongs to another user.", "uniqueness");
        }

        if (existing == null)
        {
            existing = new SqlOSUserEmail
            {
                Id = _cryptoService.GenerateId("eml"),
                UserId = user.Id,
                CreatedAt = now
            };
            _context.Set<SqlOSUserEmail>().Add(existing);
        }

        var previousPrimaryEmails = await _context.Set<SqlOSUserEmail>()
            .Where(x => x.UserId == user.Id && x.IsPrimary && x.NormalizedEmail != normalized)
            .ToListAsync(cancellationToken);
        foreach (var previous in previousPrimaryEmails)
        {
            previous.IsPrimary = false;
        }

        existing.Email = email;
        existing.NormalizedEmail = normalized;
        existing.IsPrimary = true;
        existing.IsVerified = true;
        existing.VerifiedAt ??= now;
    }

    private async Task RefreshGlobalUserActivityAsync(SqlOSUser user, string currentOrganizationId, DateTime now, CancellationToken cancellationToken)
    {
        user.IsActive = await _context.Set<SqlOSMembership>()
            .AnyAsync(x => x.UserId == user.Id && x.OrganizationId != currentOrganizationId && x.IsActive, cancellationToken);
        user.UpdatedAt = now;
    }

    private async Task<bool> IsScimManagedUserLifecycleAsync(
        string userId,
        bool currentLinkOwnsLifecycle,
        CancellationToken cancellationToken)
        => currentLinkOwnsLifecycle
            || await _context.Set<SqlOSScimExternalId>()
                .AsNoTracking()
                .AnyAsync(link => link.ResourceType == "User"
                    && link.EntityId == userId
                    && link.OwnsUserLifecycle,
                    cancellationToken);

    private async Task UpsertMembershipAsync(string organizationId, string userId, bool active, DateTime now, CancellationToken cancellationToken)
    {
        var membership = await _context.Set<SqlOSMembership>()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.UserId == userId, cancellationToken);
        if (membership == null)
        {
            membership = new SqlOSMembership
            {
                OrganizationId = organizationId,
                UserId = userId,
                Role = "member",
                CreatedAt = now
            };
            _context.Set<SqlOSMembership>().Add(membership);
        }

        membership.IsActive = active;
    }

    private async Task<string> EnsureFgaUserAsync(
        SqlOSScimConnection connection,
        SqlOSUser user,
        string displayName,
        string? email,
        bool active,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existingSubjectId = await FindFgaSubjectIdForUserAsync(connection.Id, connection.OrganizationId, user.Id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(existingSubjectId))
        {
            var subject = await _context.Set<SqlOSFgaSubject>().FirstOrDefaultAsync(x => x.Id == existingSubjectId, cancellationToken);
            if (subject != null)
            {
                subject.DisplayName = displayName;
                subject.UpdatedAt = now;
            }

            var fgaUser = await _context.Set<SqlOSFgaUser>().FirstOrDefaultAsync(x => x.SubjectId == existingSubjectId, cancellationToken);
            if (fgaUser != null)
            {
                fgaUser.Email = email;
                fgaUser.IsActive = active;
            }

            return existingSubjectId;
        }

        await EnsureFgaSubjectTypeAsync("user", "User", cancellationToken);
        var newSubject = new SqlOSFgaSubject
        {
            Id = _cryptoService.GenerateId("subj"),
            SubjectTypeId = "user",
            DisplayName = displayName,
            OrganizationId = connection.OrganizationId,
            ExternalRef = user.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        var newUser = new SqlOSFgaUser
        {
            Id = _cryptoService.GenerateId("fusr"),
            SubjectId = newSubject.Id,
            Email = email,
            IsActive = active
        };
        _context.Set<SqlOSFgaSubject>().Add(newSubject);
        _context.Set<SqlOSFgaUser>().Add(newUser);
        return newSubject.Id;
    }

    private async Task EnsureFgaSubjectTypeAsync(string id, string name, CancellationToken cancellationToken)
    {
        if (!await _context.Set<SqlOSFgaSubjectType>().AnyAsync(x => x.Id == id, cancellationToken))
        {
            _context.Set<SqlOSFgaSubjectType>().Add(new SqlOSFgaSubjectType
            {
                Id = id,
                Name = name
            });
        }
    }

    private async Task<string?> FindFgaSubjectIdForUserAsync(
        string connectionId,
        string organizationId,
        string userId,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSScimExternalId>()
            .Where(x => x.ConnectionId == connectionId && x.ResourceType == "User" && x.EntityId == userId)
            .Select(x => x.FgaSubjectId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? await _context.Set<SqlOSFgaSubject>()
            .Where(x => x.SubjectTypeId == "user" && x.ExternalRef == userId && x.OrganizationId == organizationId)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task DeprovisionUserAccessAsync(SqlOSScimConnection connection, string userId, string? fgaSubjectId, CancellationToken cancellationToken)
    {
        var membership = await _context.Set<SqlOSMembership>()
            .FirstOrDefaultAsync(x => x.OrganizationId == connection.OrganizationId && x.UserId == userId, cancellationToken);
        if (membership != null)
        {
            membership.IsActive = false;
        }

        var sessions = await _context.Set<SqlOSSession>()
            .Where(x => x.OrganizationId == connection.OrganizationId && x.UserId == userId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = DateTime.UtcNow;
            session.RevocationReason = "scim_deprovisioned";
        }

        if (!string.IsNullOrWhiteSpace(fgaSubjectId))
        {
            var fgaUser = await _context.Set<SqlOSFgaUser>()
                .FirstOrDefaultAsync(x => x.SubjectId == fgaSubjectId, cancellationToken);
            if (fgaUser != null)
            {
                fgaUser.IsActive = false;
            }
            var scimGroups = await _context.Set<SqlOSScimExternalId>()
                .Where(x => x.ConnectionId == connection.Id && x.ResourceType == "Group")
                .Select(x => x.EntityId)
                .ToListAsync(cancellationToken);
            var memberships = await _context.Set<SqlOSFgaUserGroupMembership>()
                .Where(x => x.SubjectId == fgaSubjectId && scimGroups.Contains(x.UserGroupId))
                .ToListAsync(cancellationToken);
            _context.Set<SqlOSFgaUserGroupMembership>().RemoveRange(memberships);
        }
    }

    private async Task ReplaceGroupMembersAsync(
        SqlOSScimConnection connection,
        SqlOSFgaUserGroup group,
        string? groupExternalId,
        IReadOnlySet<string> desiredSubjectIds,
        CancellationToken cancellationToken)
    {
        var existing = await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(x => x.UserGroupId == group.Id)
            .ToListAsync(cancellationToken);
        var remove = existing.Where(x => !desiredSubjectIds.Contains(x.SubjectId)).ToList();
        _context.Set<SqlOSFgaUserGroupMembership>().RemoveRange(remove);

        var existingSubjectIds = existing.Select(x => x.SubjectId).ToHashSet(StringComparer.Ordinal);
        var addSubjectIds = desiredSubjectIds
            .Where(subjectId => !existingSubjectIds.Contains(subjectId))
            .OrderBy(subjectId => subjectId, StringComparer.Ordinal)
            .ToList();
        foreach (var subjectId in addSubjectIds)
        {
            _context.Set<SqlOSFgaUserGroupMembership>().Add(new SqlOSFgaUserGroupMembership
            {
                SubjectId = subjectId,
                UserGroupId = group.Id,
                CreatedAt = DateTime.UtcNow
            });
        }

        await RecordGroupMembershipDeltaAsync(
            connection,
            group,
            groupExternalId,
            "scim.group.member_removed",
            remove.Select(item => item.SubjectId).ToList(),
            cancellationToken);
        await RecordGroupMembershipDeltaAsync(
            connection,
            group,
            groupExternalId,
            "scim.group.member_added",
            addSubjectIds,
            cancellationToken);
    }

    private async Task RecordGroupMembershipDeltaAsync(
        SqlOSScimConnection connection,
        SqlOSFgaUserGroup group,
        string? groupExternalId,
        string action,
        IReadOnlyCollection<string> subjectIds,
        CancellationToken cancellationToken)
    {
        if (subjectIds.Count == 0)
        {
            return;
        }

        var auditedSubjectIds = subjectIds
            .OrderBy(subjectId => subjectId, StringComparer.Ordinal)
            .Take(MaxAuditedMembershipSubjectIds)
            .ToArray();
        var data = new
        {
            groupId = group.Id,
            memberCount = subjectIds.Count,
            subjectIds = auditedSubjectIds,
            truncated = subjectIds.Count > auditedSubjectIds.Length
        };
        await RecordSyncEventAsync(connection, "Group", group.Id, groupExternalId, action, "success", null, data, cancellationToken);
        await RecordAuditAsync(action, connection.OrganizationId, "Group", group.Id, groupExternalId, data, cancellationToken);
    }

    private async Task<IReadOnlySet<string>> ResolveMemberSubjectIdsAsync(
        string connectionId,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var normalizedValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (normalizedValues.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var candidates = await _context.Set<SqlOSScimExternalId>()
            .AsNoTracking()
            .Where(x => x.ConnectionId == connectionId
                && x.ResourceType == "User"
                && x.IsActive
                && x.DeletedAt == null
                && x.FgaSubjectId != null
                && (normalizedValues.Contains(x.EntityId)
                    || (x.ExternalId != null && normalizedValues.Contains(x.ExternalId))))
            .Select(x => new { x.EntityId, x.ExternalId, x.FgaSubjectId })
            .ToListAsync(cancellationToken);

        var entitySubjects = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var externalSubjects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            AddMemberLookup(entitySubjects, candidate.EntityId, candidate.FgaSubjectId!);
            if (!string.IsNullOrWhiteSpace(candidate.ExternalId))
            {
                AddMemberLookup(externalSubjects, candidate.ExternalId, candidate.FgaSubjectId!);
            }
        }

        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in normalizedValues)
        {
            var matches = new HashSet<string>(StringComparer.Ordinal);
            if (entitySubjects.TryGetValue(value, out var entityMatches))
            {
                matches.UnionWith(entityMatches);
            }
            if (externalSubjects.TryGetValue(value, out var externalMatches))
            {
                matches.UnionWith(externalMatches);
            }
            if (matches.Count == 0)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM group member '{value}' was not found in this directory connection.", "invalidValue");
            }
            if (matches.Count > 1)
            {
                throw new SqlOSScimException(StatusCodes.Status409Conflict, $"SCIM group member '{value}' is ambiguous in this directory connection.", "uniqueness");
            }
            subjectIds.Add(matches.Single());
        }
        return subjectIds;
    }

    private static void AddMemberLookup(
        IDictionary<string, HashSet<string>> lookup,
        string key,
        string subjectId)
    {
        if (!lookup.TryGetValue(key, out var subjects))
        {
            subjects = new HashSet<string>(StringComparer.Ordinal);
            lookup[key] = subjects;
        }
        subjects.Add(subjectId);
    }

    private async Task<List<string>> GetCurrentScimGroupMembersAsync(string connectionId, string groupId, CancellationToken cancellationToken)
    {
        var subjectIds = await _context.Set<SqlOSFgaUserGroupMembership>()
            .Where(x => x.UserGroupId == groupId)
            .Select(x => x.SubjectId)
            .ToListAsync(cancellationToken);
        return await _context.Set<SqlOSScimExternalId>()
            .Where(x => x.ConnectionId == connectionId && x.ResourceType == "User" && x.FgaSubjectId != null && subjectIds.Contains(x.FgaSubjectId))
            .Select(x => x.EntityId)
            .ToListAsync(cancellationToken);
    }

    private async Task ApplyGroupMappingsAsync(SqlOSScimConnection connection, SqlOSFgaUserGroup group, string? externalId, string displayName, CancellationToken cancellationToken)
    {
        var groupCorrelationId = externalId ?? group.Id;
        var mappings = await _context.Set<SqlOSScimGroupMapping>()
            .Where(x => x.ConnectionId == connection.Id)
            .ToListAsync(cancellationToken);

        foreach (var mapping in mappings)
        {
            var activeManaged = await _context.Set<SqlOSScimManagedGrant>()
                .Where(x => x.ConnectionId == connection.Id
                    && x.MappingId == mapping.Id
                    && x.FgaGroupId == group.Id
                    && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
            var resourceId = ResolveMappingResourceId(mapping, externalId, displayName);
            if (!mapping.IsEnabled || resourceId == null)
            {
                foreach (var managed in activeManaged)
                {
                    await RevokeManagedGrantEntityAsync(connection, managed, cancellationToken);
                }
                continue;
            }

            var role = await _context.Set<SqlOSFgaRole>()
                .FirstOrDefaultAsync(x => x.Key == mapping.RoleKey || x.Id == mapping.RoleKey, cancellationToken);
            if (role == null)
            {
                foreach (var managed in activeManaged)
                {
                    await RevokeManagedGrantEntityAsync(connection, managed, cancellationToken);
                }
                var data = new { mappingId = mapping.Id, mapping.RoleKey, resourceId, error = "Mapped role was not found." };
                await RecordSyncEventAsync(connection, "Group", group.Id, externalId, "scim.grant.role_missing", "failed", "Mapped role was not found; any previous managed grant was revoked.", data, cancellationToken);
                await RecordAuditAsync("scim.grant.role_missing", connection.OrganizationId, "Group", group.Id, externalId, data, cancellationToken);
                continue;
            }

            var resource = await _context.Set<SqlOSFgaResource>()
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken);
            if (resource == null)
            {
                foreach (var managed in activeManaged)
                {
                    await RevokeManagedGrantEntityAsync(connection, managed, cancellationToken);
                }
                var data = new { mappingId = mapping.Id, mapping.RoleKey, resourceId, error = "Mapped resource was not found." };
                await RecordSyncEventAsync(connection, "Group", group.Id, externalId, "scim.grant.resource_missing", "failed", "Mapped resource was not found; any previous managed grant was revoked.", data, cancellationToken);
                await RecordAuditAsync("scim.grant.resource_missing", connection.OrganizationId, "Group", group.Id, externalId, data, cancellationToken);
                continue;
            }

            foreach (var obsolete in activeManaged.Where(x => x.ResourceId != resource.Id || x.RoleId != role.Id))
            {
                await RevokeManagedGrantEntityAsync(connection, obsolete, cancellationToken);
            }

            if (activeManaged.Any(x => x.ResourceId == resource.Id && x.RoleId == role.Id))
            {
                continue;
            }

            var grant = new SqlOSFgaGrant
            {
                Id = _cryptoService.GenerateId("grant"),
                SubjectId = group.SubjectId,
                ResourceId = resource.Id,
                RoleId = role.Id,
                Description = string.IsNullOrWhiteSpace(mapping.Description)
                    ? $"SCIM mapping {mapping.Id}"
                    : mapping.Description,
                CreatedAt = DateTime.UtcNow
            };
            _context.Set<SqlOSFgaGrant>().Add(grant);
            _context.Set<SqlOSScimManagedGrant>().Add(new SqlOSScimManagedGrant
            {
                Id = _cryptoService.GenerateId("scgrant"),
                ConnectionId = connection.Id,
                MappingId = mapping.Id,
                GroupExternalId = groupCorrelationId,
                FgaGroupId = group.Id,
                FgaGroupSubjectId = group.SubjectId,
                GrantId = grant.Id,
                RoleId = role.Id,
                ResourceId = resource.Id,
                CreatedAt = DateTime.UtcNow
            });
            await RecordSyncEventAsync(connection, "Group", group.Id, externalId, "scim.grant.mapped", "success", null, new { mappingId = mapping.Id, grantId = grant.Id, roleId = role.Id, resourceId = resource.Id }, cancellationToken);
            await RecordAuditAsync("scim.grant.mapped", connection.OrganizationId, "Group", group.Id, externalId, new { mappingId = mapping.Id, grantId = grant.Id, roleId = role.Id, resourceId = resource.Id }, cancellationToken);
        }
    }

    private static string? ResolveMappingResourceId(SqlOSScimGroupMapping mapping, string? externalId, string displayName)
    {
        var match = mapping.MatchType switch
        {
            SqlOSScimGroupMappingMatchTypes.ExternalId => string.Equals(mapping.GroupExternalId, externalId, StringComparison.Ordinal) ? new Dictionary<string, string>() : null,
            SqlOSScimGroupMappingMatchTypes.DisplayName => string.Equals(mapping.GroupDisplayName, displayName, StringComparison.OrdinalIgnoreCase) ? new Dictionary<string, string>() : null,
            SqlOSScimGroupMappingMatchTypes.Pattern => MatchPattern(mapping.GroupPattern, displayName),
            _ => null
        };
        if (match == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(mapping.ResourceId))
        {
            return mapping.ResourceId;
        }

        if (string.IsNullOrWhiteSpace(mapping.ResourceIdTemplate))
        {
            return null;
        }

        var resourceId = mapping.ResourceIdTemplate;
        foreach (var (key, value) in match)
        {
            resourceId = resourceId.Replace("{" + key + "}", value, StringComparison.Ordinal);
        }

        return resourceId;
    }

    private static Dictionary<string, string>? MatchPattern(string? pattern, string displayName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        Match match;
        try
        {
            match = Regex.Match(
                displayName,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM group mapping pattern exceeded the evaluation limit.", "invalidValue");
        }
        catch (ArgumentException)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM group mapping pattern is invalid.", "invalidValue");
        }
        if (!match.Success)
        {
            return null;
        }

        return match.Groups.Keys
            .Where(key => !int.TryParse(key, out _))
            .ToDictionary(key => key, key => match.Groups[key].Value, StringComparer.Ordinal);
    }

    private async Task RevokeManagedGrantAsync(SqlOSScimConnection connection, string mappingId, string groupId, CancellationToken cancellationToken)
    {
        var grants = await _context.Set<SqlOSScimManagedGrant>()
            .Where(x => x.ConnectionId == connection.Id && x.MappingId == mappingId && x.FgaGroupId == groupId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var managed in grants)
        {
            await RevokeManagedGrantEntityAsync(connection, managed, cancellationToken);
        }
    }

    private async Task RevokeManagedGrantsForGroupAsync(SqlOSScimConnection connection, string groupId, CancellationToken cancellationToken)
    {
        var grants = await _context.Set<SqlOSScimManagedGrant>()
            .Where(x => x.ConnectionId == connection.Id && x.FgaGroupId == groupId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var managed in grants)
        {
            await RevokeManagedGrantEntityAsync(connection, managed, cancellationToken);
        }
    }

    private async Task RevokeManagedGrantEntityAsync(SqlOSScimConnection connection, SqlOSScimManagedGrant managed, CancellationToken cancellationToken)
    {
        var grant = await _context.Set<SqlOSFgaGrant>().FirstOrDefaultAsync(x => x.Id == managed.GrantId, cancellationToken);
        if (grant != null)
        {
            _context.Set<SqlOSFgaGrant>().Remove(grant);
        }

        managed.RevokedAt = DateTime.UtcNow;
        var data = new { mappingId = managed.MappingId, grantId = managed.GrantId, roleId = managed.RoleId, resourceId = managed.ResourceId };
        await RecordSyncEventAsync(connection, "Group", managed.FgaGroupId, managed.GroupExternalId, "scim.grant.revoked", "success", null, data, cancellationToken);
        await RecordAuditAsync("scim.grant.revoked", connection.OrganizationId, "Group", managed.FgaGroupId, managed.GroupExternalId, data, cancellationToken);
    }

    private async Task<SqlOSScimExternalId?> FindUserLinkAsync(string connectionId, string userName, string? externalId, CancellationToken cancellationToken)
    {
        var matches = await _context.Set<SqlOSScimExternalId>()
            .Where(x => x.ConnectionId == connectionId
                && x.ResourceType == "User"
                && (x.UserName == userName || (!string.IsNullOrWhiteSpace(externalId) && x.ExternalId == externalId)))
            .ToListAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(externalId)
            ? matches.FirstOrDefault(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))
                ?? matches.FirstOrDefault(x => EqualsScim(x.UserName, userName))
            : matches.FirstOrDefault(x => EqualsScim(x.UserName, userName));
    }

    private async Task<SqlOSScimExternalId?> FindGroupLinkAsync(string connectionId, string displayName, string? externalId, CancellationToken cancellationToken)
    {
        var matches = await _context.Set<SqlOSScimExternalId>()
            .Where(x => x.ConnectionId == connectionId
                && x.ResourceType == "Group"
                && (x.DisplayName == displayName || (!string.IsNullOrWhiteSpace(externalId) && x.ExternalId == externalId)))
            .ToListAsync(cancellationToken);
        return !string.IsNullOrWhiteSpace(externalId)
            ? matches.FirstOrDefault(x => string.Equals(x.ExternalId, externalId, StringComparison.Ordinal))
                ?? matches.FirstOrDefault(x => EqualsScim(x.DisplayName, displayName))
            : matches.FirstOrDefault(x => EqualsScim(x.DisplayName, displayName));
    }

    private async Task<SqlOSScimExternalId> GetRequiredUserLinkAsync(string connectionId, string id, bool includeDeleted, CancellationToken cancellationToken)
        => await _context.Set<SqlOSScimExternalId>()
            .FirstOrDefaultAsync(x => x.ConnectionId == connectionId
                && x.ResourceType == "User"
                && x.EntityId == id
                && (includeDeleted || x.DeletedAt == null), cancellationToken)
            ?? throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM user not found.");

    private async Task<SqlOSScimExternalId> GetRequiredGroupLinkAsync(string connectionId, string id, bool includeDeleted, CancellationToken cancellationToken)
        => await _context.Set<SqlOSScimExternalId>()
            .FirstOrDefaultAsync(x => x.ConnectionId == connectionId
                && x.ResourceType == "Group"
                && x.EntityId == id
                && (includeDeleted || x.DeletedAt == null), cancellationToken)
            ?? throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM group not found.");

    private async Task<SqlOSScimExternalId> UpsertExternalLinkAsync(
        string connectionId,
        string resourceType,
        string? externalId,
        string entityId,
        string? fgaSubjectId,
        string? userName,
        string? primaryEmail,
        string? displayName,
        string? formattedName,
        string? givenName,
        string? familyName,
        bool active,
        DateTime now,
        CancellationToken cancellationToken,
        SqlOSScimExternalId? link = null)
    {
        link ??= await _context.Set<SqlOSScimExternalId>()
            .FirstOrDefaultAsync(x => x.ConnectionId == connectionId && x.ResourceType == resourceType && x.EntityId == entityId, cancellationToken);
        if (link == null)
        {
            link = new SqlOSScimExternalId
            {
                Id = _cryptoService.GenerateId("scext"),
                ConnectionId = connectionId,
                ResourceType = resourceType,
                CreatedAt = now
            };
            _context.Set<SqlOSScimExternalId>().Add(link);
        }

        link.ExternalId = externalId;
        link.EntityId = entityId;
        link.FgaSubjectId = fgaSubjectId;
        link.UserName = userName;
        link.PrimaryEmail = primaryEmail;
        link.DisplayName = displayName;
        link.FormattedName = formattedName;
        link.GivenName = givenName;
        link.FamilyName = familyName;
        link.IsActive = active;
        link.UpdatedAt = now;
        link.LastSyncedAt = now;
        return link;
    }

    private async Task<JsonObject> ToScimUserAsync(
        SqlOSScimConnection connection,
        SqlOSUser user,
        SqlOSScimExternalId link,
        string? attributes,
        string? excludedAttributes,
        CancellationToken cancellationToken)
    {
        var selection = CreateAttributeSelection(attributes, excludedAttributes);
        var membershipIsActive = await _context.Set<SqlOSMembership>()
            .AsNoTracking()
            .Where(x => x.OrganizationId == connection.OrganizationId && x.UserId == user.Id)
            .Select(x => (bool?)x.IsActive)
            .FirstOrDefaultAsync(cancellationToken) ?? false;
        IReadOnlyList<SqlOSScimExternalId> groupLinks = [];
        if (selection.Includes("groups") && !string.IsNullOrWhiteSpace(link.FgaSubjectId))
        {
            var groupIds = await _context.Set<SqlOSFgaUserGroupMembership>()
                .AsNoTracking()
                .Where(x => x.SubjectId == link.FgaSubjectId)
                .Select(x => x.UserGroupId)
                .ToListAsync(cancellationToken);
            groupLinks = await _context.Set<SqlOSScimExternalId>()
                .AsNoTracking()
                .Where(x => x.ConnectionId == connection.Id
                    && x.ResourceType == "Group"
                    && x.DeletedAt == null
                    && groupIds.Contains(x.EntityId))
                .ToListAsync(cancellationToken);
        }
        return ToScimUser(user, link, selection, membershipIsActive, groupLinks);
    }

    private JsonObject ToScimUser(
        SqlOSUser user,
        SqlOSScimExternalId link,
        ScimAttributeSelection selection,
        bool membershipIsActive,
        IReadOnlyList<SqlOSScimExternalId> groupLinks)
    {
        var result = new JsonObject
        {
            ["schemas"] = new JsonArray(UserSchema),
            ["id"] = user.Id
        };
        if (selection.Includes("meta"))
        {
            var meta = new JsonObject();
            AddSelected(meta, selection, "meta.resourceType", "resourceType", "User");
            AddSelected(meta, selection, "meta.created", "created", link.CreatedAt);
            AddSelected(meta, selection, "meta.lastModified", "lastModified", link.UpdatedAt);
            AddSelected(meta, selection, "meta.location", "location", BuildScimLocation($"Users/{Uri.EscapeDataString(user.Id)}"));
            if (meta.Count > 0)
            {
                result["meta"] = meta;
            }
        }
        AddSelected(result, selection, "externalId", link.ExternalId);
        AddSelected(result, selection, "userName", link.UserName ?? link.PrimaryEmail ?? user.Id);
        AddSelected(result, selection, "displayName", link.DisplayName ?? user.DisplayName);
        AddSelected(result, selection, "active", link.IsActive && membershipIsActive);
        if (selection.Includes("name"))
        {
            var name = new JsonObject();
            AddSelected(name, selection, "name.formatted", "formatted", link.FormattedName ?? BuildFormattedName(link.GivenName, link.FamilyName) ?? link.DisplayName ?? user.DisplayName);
            AddSelected(name, selection, "name.givenName", "givenName", link.GivenName);
            AddSelected(name, selection, "name.familyName", "familyName", link.FamilyName);
            if (name.Count > 0)
            {
                result["name"] = name;
            }
        }
        if (selection.Includes("emails"))
        {
            var emails = new JsonArray();
            if (!string.IsNullOrWhiteSpace(link.PrimaryEmail))
            {
                var email = new JsonObject();
                AddSelected(email, selection, "emails.value", "value", link.PrimaryEmail);
                AddSelected(email, selection, "emails.primary", "primary", true);
                AddSelected(email, selection, "emails.type", "type", "work");
                if (email.Count > 0)
                {
                    emails.Add(email);
                }
            }
            result["emails"] = emails;
        }
        if (selection.Includes("groups") && !string.IsNullOrWhiteSpace(link.FgaSubjectId))
        {
            result["groups"] = new JsonArray(groupLinks.Select(x =>
            {
                var group = new JsonObject();
                AddSelected(group, selection, "groups.value", "value", x.EntityId);
                AddSelected(group, selection, "groups.display", "display", x.DisplayName);
                AddSelected(group, selection, "groups.$ref", "$ref", BuildScimLocation($"Groups/{Uri.EscapeDataString(x.EntityId)}"));
                return group;
            }).Where(group => group.Count > 0).ToArray<JsonNode?>());
        }

        return result;
    }

    private async Task<JsonObject> ToScimGroupAsync(
        SqlOSScimConnection connection,
        SqlOSScimExternalId link,
        string? attributes,
        string? excludedAttributes,
        CancellationToken cancellationToken)
    {
        var group = await _context.Set<SqlOSFgaUserGroup>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == link.EntityId, cancellationToken)
            ?? throw new SqlOSScimException(StatusCodes.Status404NotFound, "SCIM group not found.");
        var selection = CreateAttributeSelection(attributes, excludedAttributes);
        IReadOnlyList<SqlOSScimExternalId> userLinks = [];
        if (selection.Includes("members"))
        {
            var subjectIds = await _context.Set<SqlOSFgaUserGroupMembership>()
                .AsNoTracking()
                .Where(x => x.UserGroupId == group.Id)
                .Select(x => x.SubjectId)
                .ToListAsync(cancellationToken);
            userLinks = await _context.Set<SqlOSScimExternalId>()
                .AsNoTracking()
                .Where(x => x.ConnectionId == connection.Id
                    && x.ResourceType == "User"
                    && x.DeletedAt == null
                    && x.FgaSubjectId != null
                    && subjectIds.Contains(x.FgaSubjectId))
                .ToListAsync(cancellationToken);
        }
        return ToScimGroup(group, link, selection, userLinks);
    }

    private JsonObject ToScimGroup(
        SqlOSFgaUserGroup group,
        SqlOSScimExternalId link,
        ScimAttributeSelection selection,
        IReadOnlyList<SqlOSScimExternalId> userLinks)
    {
        var result = new JsonObject
        {
            ["schemas"] = new JsonArray(GroupSchema),
            ["id"] = group.Id
        };
        if (selection.Includes("meta"))
        {
            var meta = new JsonObject();
            AddSelected(meta, selection, "meta.resourceType", "resourceType", "Group");
            AddSelected(meta, selection, "meta.created", "created", link.CreatedAt);
            AddSelected(meta, selection, "meta.lastModified", "lastModified", link.UpdatedAt);
            AddSelected(meta, selection, "meta.location", "location", BuildScimLocation($"Groups/{Uri.EscapeDataString(group.Id)}"));
            if (meta.Count > 0)
            {
                result["meta"] = meta;
            }
        }
        AddSelected(result, selection, "externalId", link.ExternalId);
        AddSelected(result, selection, "displayName", link.DisplayName ?? group.Name);
        if (selection.Includes("members"))
        {
            result["members"] = new JsonArray(userLinks.Select(x =>
            {
                var member = new JsonObject();
                AddSelected(member, selection, "members.value", "value", x.EntityId);
                AddSelected(member, selection, "members.display", "display", x.DisplayName);
                AddSelected(member, selection, "members.$ref", "$ref", BuildScimLocation($"Users/{Uri.EscapeDataString(x.EntityId)}"));
                return member;
            }).Where(member => member.Count > 0).ToArray<JsonNode?>());
        }

        return result;
    }

    private async Task RecordSyncEventAsync(SqlOSScimConnection connection, string resourceType, string? resourceId, string? externalId, string action, string result, string? error, object? data, CancellationToken cancellationToken)
    {
        _context.Set<SqlOSScimSyncEvent>().Add(new SqlOSScimSyncEvent
        {
            Id = _cryptoService.GenerateId("scevt"),
            ConnectionId = connection.Id,
            OrganizationId = connection.OrganizationId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ExternalId = externalId,
            Action = action,
            Result = result,
            Error = error,
            DataJson = data == null ? null : JsonSerializer.Serialize(SummarizeSyncEventData(data), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            OccurredAt = DateTime.UtcNow
        });
    }

    private async Task RecordAuditAsync(string action, string organizationId, string resourceType, string? resourceId, string? externalId, object? data, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["resourceType"] = resourceType,
            ["resourceId"] = resourceId,
            ["externalId"] = externalId,
            ["payload"] = data
        };
        var audit = new SqlOSAuditLogService(_context, _cryptoService);
        await audit.RecordAsync(new SqlOSAuditLogRecordRequest(
            Action: action,
            OrganizationId: organizationId,
            Source: "scim",
            Actor: new SqlOSAuditActor("scim", externalId),
            Targets: string.IsNullOrWhiteSpace(resourceId) ? [] : [new SqlOSAuditTarget(resourceType.ToLowerInvariant(), resourceId)],
            Metadata: metadata), cancellationToken);
    }

    private static ScimUserRequest ParseUser(JsonObject payload, bool validateSchemas = true)
    {
        if (validateSchemas)
        {
            ValidateResourceSchemas(payload, UserSchema);
        }
        var userName = NormalizeRequired(ReadString(payload, "userName"), "SCIM userName is required.", 450);
        var name = TryGetNode(payload, "name") as JsonObject;
        var givenName = NormalizeOptional(name == null ? null : ReadString(name, "givenName"), 150);
        var familyName = NormalizeOptional(name == null ? null : ReadString(name, "familyName"), 150);
        var formattedName = NormalizeOptional(name == null ? null : ReadString(name, "formatted"), 300)
            ?? BuildFormattedName(givenName, familyName);
        var displayName = NormalizeOptional(ReadString(payload, "displayName"), 300)
            ?? formattedName
            ?? BuildFormattedName(givenName, familyName)
            ?? userName;
        return new ScimUserRequest(
            userName,
            NormalizeOptional(ReadString(payload, "externalId"), 450),
            NormalizeOptional(ReadPrimaryEmail(payload), 320),
            displayName,
            formattedName,
            givenName,
            familyName,
            ReadBoolean(payload, "active") ?? true);
    }

    private static ScimGroupRequest ParseGroup(JsonObject payload, bool validateSchemas = true)
    {
        if (validateSchemas)
        {
            ValidateResourceSchemas(payload, GroupSchema);
        }
        return new ScimGroupRequest(
            NormalizeRequired(ReadString(payload, "displayName"), "SCIM group displayName is required.", 300),
            NormalizeOptional(ReadString(payload, "externalId"), 450),
            ReadMemberValues(TryGetNode(payload, "members")));
    }

    private static List<ScimPatchOperation> ReadOperations(JsonObject payload)
    {
        ValidateResourceSchemas(payload, PatchOpSchema);

        if (TryGetNode(payload, "Operations") is not JsonArray operations || operations.Count == 0)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM PATCH requires at least one operation.", "invalidSyntax");
        }
        if (operations.Count > 100)
        {
            throw new SqlOSScimException(StatusCodes.Status413PayloadTooLarge, "SCIM PATCH contains too many operations.", "tooMany");
        }

        var result = new List<ScimPatchOperation>(operations.Count);
        foreach (var node in operations)
        {
            if (node is not JsonObject operation)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Every SCIM PATCH operation must be an object.", "invalidSyntax");
            }
            var op = ReadString(operation, "op")?.Trim().ToLowerInvariant();
            if (op is not ("add" or "remove" or "replace"))
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM PATCH op must be add, remove, or replace.", "invalidValue");
            }
            var path = ReadString(operation, "path");
            var hasValue = operation.Any(property => property.Key.Equals("value", StringComparison.OrdinalIgnoreCase));
            if (op is "add" or "replace" && !hasValue)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM PATCH '{op}' requires a value.", "invalidValue");
            }
            if (op == "remove" && string.IsNullOrWhiteSpace(path))
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM PATCH remove requires a path.", "noTarget");
            }
            result.Add(new ScimPatchOperation(op, path, TryGetNode(operation, "value")));
        }

        return result;
    }

    private static void ApplyUserPatchOperation(ScimUserState state, ScimPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            if (operation.Value is not JsonObject valueObject)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "A pathless SCIM user PATCH operation requires an object value.", "invalidValue");
            }
            foreach (var property in valueObject)
            {
                ApplyUserPatchOperation(state, new ScimPatchOperation(operation.Op, property.Key, property.Value));
            }
            return;
        }

        var path = NormalizePatchPath(operation.Path, UserSchema);
        if (path.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.Op == "remove")
            {
                state.GivenName = null;
                state.FamilyName = null;
                state.FormattedName = null;
                return;
            }
            if (operation.Value is not JsonObject name)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM name must be an object.", "invalidValue");
            }
            var formatted = name.FirstOrDefault(property => property.Key.Equals("formatted", StringComparison.OrdinalIgnoreCase));
            foreach (var property in name.Where(property => !property.Key.Equals("formatted", StringComparison.OrdinalIgnoreCase)))
            {
                ApplyUserPatchOperation(state, new ScimPatchOperation(operation.Op, $"name.{property.Key}", property.Value));
            }
            if (formatted.Key != null)
            {
                state.FormattedName = NormalizeOptional(ReadStringValue(formatted.Value), 300);
            }
            return;
        }

        if (path.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            state.Active = operation.Op == "remove" ? true : ReadBooleanValue(operation.Value, "SCIM active must be a boolean.");
        }
        else if (path.Equals("userName", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.Op == "remove")
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM userName is required.", "mutability");
            }
            state.UserName = NormalizeRequired(ReadStringValue(operation.Value), "SCIM userName is required.", 450);
        }
        else if (path.Equals("externalId", StringComparison.OrdinalIgnoreCase))
        {
            state.ExternalId = operation.Op == "remove" ? null : NormalizeOptional(ReadStringValue(operation.Value), 450);
        }
        else if (path.Equals("displayName", StringComparison.OrdinalIgnoreCase))
        {
            state.DisplayName = operation.Op == "remove"
                ? BuildFormattedName(state.GivenName, state.FamilyName) ?? state.UserName
                : NormalizeRequired(ReadStringValue(operation.Value), "SCIM displayName cannot be empty.", 300);
        }
        else if (path.Equals("name.formatted", StringComparison.OrdinalIgnoreCase))
        {
            state.FormattedName = operation.Op == "remove"
                ? BuildFormattedName(state.GivenName, state.FamilyName)
                : NormalizeOptional(ReadStringValue(operation.Value), 300);
        }
        else if (path.Equals("name.givenName", StringComparison.OrdinalIgnoreCase))
        {
            var previousDerivedName = BuildFormattedName(state.GivenName, state.FamilyName);
            state.GivenName = operation.Op == "remove" ? null : NormalizeOptional(ReadStringValue(operation.Value), 150);
            if (state.FormattedName == null || string.Equals(state.FormattedName, previousDerivedName, StringComparison.Ordinal))
            {
                state.FormattedName = BuildFormattedName(state.GivenName, state.FamilyName);
            }
        }
        else if (path.Equals("name.familyName", StringComparison.OrdinalIgnoreCase))
        {
            var previousDerivedName = BuildFormattedName(state.GivenName, state.FamilyName);
            state.FamilyName = operation.Op == "remove" ? null : NormalizeOptional(ReadStringValue(operation.Value), 150);
            if (state.FormattedName == null || string.Equals(state.FormattedName, previousDerivedName, StringComparison.Ordinal))
            {
                state.FormattedName = BuildFormattedName(state.GivenName, state.FamilyName);
            }
        }
        else if (path.Equals("emails", StringComparison.OrdinalIgnoreCase))
        {
            state.PrimaryEmail = operation.Op == "remove"
                ? null
                : NormalizeOptional(ReadPrimaryEmailValue(operation.Value), 320);
        }
        else if (Regex.IsMatch(path, "^emails\\[type\\s+eq\\s+\"work\"\\]\\.value$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            || path.Equals("emails.value", StringComparison.OrdinalIgnoreCase))
        {
            state.PrimaryEmail = operation.Op == "remove" ? null : NormalizeOptional(ReadStringValue(operation.Value), 320);
        }
        else if (Regex.IsMatch(path, "^emails\\[[^]]+\\]\\.value$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM email path '{path}' did not match the stored work email.", "noTarget");
        }
        else if (path.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            var suppliedId = ReadStringValue(operation.Value);
            if (operation.Op == "remove" || !string.Equals(suppliedId, state.ResourceId, StringComparison.Ordinal))
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM resource id cannot be changed.", "mutability");
            }
        }
        else if (path.Equals("meta", StringComparison.OrdinalIgnoreCase)
            || path.Equals("groups", StringComparison.OrdinalIgnoreCase)
            || path.Equals("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM attribute '{path}' is not writable.", "mutability");
        }
        else
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"Unsupported SCIM user PATCH path '{path}'.", "invalidPath");
        }
    }

    private static void ApplyGroupPatchOperation(ScimGroupState state, ScimPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Path))
        {
            if (operation.Value is not JsonObject valueObject)
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "A pathless SCIM group PATCH operation requires an object value.", "invalidValue");
            }
            foreach (var property in valueObject)
            {
                ApplyGroupPatchOperation(state, new ScimPatchOperation(operation.Op, property.Key, property.Value));
            }
            return;
        }

        var path = NormalizePatchPath(operation.Path, GroupSchema);
        var filteredMember = Regex.Match(
            path,
            "^members\\[value\\s+eq\\s+\"(?<value>[^\"]{1,450})\"\\]$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (filteredMember.Success)
        {
            if (operation.Op != "remove")
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Filtered SCIM members paths only support remove.", "invalidPath");
            }
            state.Members.RemoveAll(x => string.Equals(x, filteredMember.Groups["value"].Value, StringComparison.Ordinal));
            return;
        }

        if (path.Equals("displayName", StringComparison.OrdinalIgnoreCase))
        {
            if (operation.Op == "remove")
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM group displayName is required.", "mutability");
            }
            state.DisplayName = NormalizeRequired(ReadStringValue(operation.Value), "SCIM group displayName is required.", 300);
        }
        else if (path.Equals("externalId", StringComparison.OrdinalIgnoreCase))
        {
            state.ExternalId = operation.Op == "remove" ? null : NormalizeOptional(ReadStringValue(operation.Value), 450);
        }
        else if (path.Equals("members", StringComparison.OrdinalIgnoreCase))
        {
            var values = ReadMemberValues(operation.Value);
            if (operation.Op == "replace")
            {
                state.Members = values;
            }
            else if (operation.Op == "remove")
            {
                if (operation.Value == null)
                {
                    state.Members.Clear();
                }
                else
                {
                    state.Members.RemoveAll(x => values.Contains(x, StringComparer.Ordinal));
                }
            }
            else
            {
                foreach (var value in values.Where(value => !state.Members.Contains(value, StringComparer.Ordinal)))
                {
                    state.Members.Add(value);
                }
            }
        }
        else if (path.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            var suppliedId = ReadStringValue(operation.Value);
            if (operation.Op == "remove" || !string.Equals(suppliedId, state.ResourceId, StringComparison.Ordinal))
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM resource id cannot be changed.", "mutability");
            }
        }
        else if (path.Equals("meta", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM attribute '{path}' is not writable.", "mutability");
        }
        else
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"Unsupported SCIM group PATCH path '{path}'.", "invalidPath");
        }
    }

    private static List<string> ReadMemberValues(JsonNode? node)
    {
        if (node == null)
        {
            return [];
        }
        var objects = node switch
        {
            JsonArray array when array.All(item => item is JsonObject) => array.Select(item => (JsonObject)item!).ToList(),
            JsonArray => throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Every SCIM member must be an object.", "invalidValue"),
            JsonObject item => [item],
            _ => throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM members must be an array of objects.", "invalidValue")
        };
        var values = objects.Select(item => NormalizeRequired(ReadString(item, "value"), "Every SCIM member requires a value.", 450));
        return values
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? ReadPrimaryEmail(JsonObject payload)
        => TryGetNode(payload, "emails") is { } emails ? ReadPrimaryEmailValue(emails) : null;

    private static string? ReadPrimaryEmailValue(JsonNode? node)
    {
        if (node is not JsonArray emails)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM emails must be an array.", "invalidValue");
        }
        if (emails.Any(item => item is not JsonObject))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Every SCIM email must be an object.", "invalidValue");
        }
        var objects = emails.Select(item => (JsonObject)item!).ToList();
        foreach (var email in objects)
        {
            _ = NormalizeRequired(ReadString(email, "value"), "Every SCIM email requires a value.", 320);
        }
        var primaryEmails = objects.Where(x => ReadBoolean(x, "primary") == true).ToList();
        if (primaryEmails.Count > 1)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM emails may contain at most one primary value.", "invalidValue");
        }
        var selected = primaryEmails.SingleOrDefault()
            ?? objects.FirstOrDefault(x => string.Equals(ReadString(x, "type"), "work", StringComparison.OrdinalIgnoreCase))
            ?? objects.FirstOrDefault();
        return selected == null ? null : ReadString(selected, "value");
    }

    private static JsonNode? TryGetNode(JsonObject payload, string propertyName)
        => payload.FirstOrDefault(x => x.Key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? ReadString(JsonObject payload, string propertyName)
        => TryGetNode(payload, propertyName) is { } value ? ReadStringValue(value) : null;

    private static string? ReadStringValue(JsonNode? value)
    {
        if (value == null)
        {
            return null;
        }
        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM attribute value must be a string.", "invalidValue");
        }
    }

    private static bool? ReadBoolean(JsonObject payload, string propertyName)
        => TryGetNode(payload, propertyName) is { } value ? ReadBooleanValue(value, $"SCIM {propertyName} must be a boolean.") : null;

    private static bool ReadBooleanValue(JsonNode? value, string message)
    {
        try
        {
            return value?.GetValue<bool>() ?? throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, message, "invalidValue");
        }
    }

    private static string NormalizeRequired(string? value, string message, int maxLength)
    {
        var normalized = NormalizeOptional(value, maxLength);
        return normalized ?? throw new SqlOSScimException(StatusCodes.Status400BadRequest, message, "invalidValue");
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM attribute exceeds {maxLength} characters.", "invalidValue");
        }
        return normalized;
    }

    private static void ValidateResourceSchemas(JsonObject payload, string requiredSchema)
    {
        if (TryGetNode(payload, "schemas") is not JsonArray { Count: > 0 } schemas
            || schemas.Any(item => item is not JsonValue))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM payload schemas must include '{requiredSchema}'.", "invalidSyntax");
        }
        var schemaValues = schemas.Select(ReadStringValue).ToList();
        if (schemaValues.Any(string.IsNullOrWhiteSpace)
            || schemaValues.Distinct(StringComparer.Ordinal).Count() != schemaValues.Count
            || !schemaValues.Contains(requiredSchema, StringComparer.Ordinal))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, $"SCIM payload schemas must contain unique values including '{requiredSchema}'.", "invalidSyntax");
        }
        var incompatibleCoreSchema = requiredSchema switch
        {
            UserSchema => GroupSchema,
            GroupSchema => UserSchema,
            _ => null
        };
        if (incompatibleCoreSchema != null && schemaValues.Contains(incompatibleCoreSchema, StringComparer.Ordinal))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM payload contains an incompatible core resource schema.", "invalidSyntax");
        }
    }

    private static string NormalizePatchPath(string path, string schema)
    {
        var normalized = path.Trim();
        var prefix = schema + ":";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? normalized[prefix.Length..] : normalized;
    }

    private static bool EqualsScim(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeEmail(string value)
        => value.Contains('@', StringComparison.Ordinal) && value.Length <= 320;

    private static string? BuildFormattedName(string? givenName, string? familyName)
    {
        var parts = new[] { givenName, familyName }.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return parts.Length == 0 ? null : string.Join(' ', parts);
    }

    private static (string Attribute, string Value) ReadEqFilter(string filter, params string[] allowedAttributes)
    {
        if (filter.Length > 512)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "SCIM filter is too long.", "invalidFilter");
        }

        var workEmailMatch = Regex.Match(
            filter,
            "^emails\\[\\s*type\\s+eq\\s+\"work\"\\s*\\]\\.value\\s+eq\\s+\"(?<value>[^\"]{1,450})\"$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (workEmailMatch.Success)
        {
            if (!allowedAttributes.Contains("emails.value", StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Unsupported SCIM filter attribute.", "invalidFilter");
            }
            return ("emails.value", workEmailMatch.Groups["value"].Value);
        }

        var match = Regex.Match(
            filter,
            "^(?<attribute>[A-Za-z][A-Za-z0-9.]*)\\s+eq\\s+\"(?<value>[^\"]{1,450})\"$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (!match.Success)
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Unsupported SCIM filter.", "invalidFilter");
        }

        var attribute = match.Groups["attribute"].Value;
        if (!allowedAttributes.Contains(attribute, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlOSScimException(StatusCodes.Status400BadRequest, "Unsupported SCIM filter attribute.", "invalidFilter");
        }

        return (attribute.ToLowerInvariant(), match.Groups["value"].Value);
    }

    private static (int Skip, int Take, int StartIndex) ResolveScimPaging(int? startIndex, int? count)
    {
        var resolvedStart = Math.Max(1, startIndex ?? 1);
        var resolvedCount = Math.Clamp(count ?? 100, 0, 200);
        return (resolvedStart - 1, resolvedCount, resolvedStart);
    }

    private static JsonObject ListResponse(JsonArray resources, int totalResults, int startIndex)
        => new()
        {
            ["schemas"] = new JsonArray(ListResponseSchema),
            ["totalResults"] = totalResults,
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = resources.Count,
            ["Resources"] = resources
        };

    private string BuildScimLocation(string relativePath)
    {
        var basePath = string.IsNullOrWhiteSpace(_options.ScimBasePath) ? "/sqlos/scim/v2" : _options.ScimBasePath.Trim();
        if (!basePath.StartsWith('/'))
        {
            basePath = "/" + basePath;
        }
        var relative = relativePath.TrimStart('/');
        var path = $"{basePath.TrimEnd('/')}/{relative}";
        return string.IsNullOrWhiteSpace(_options.PublicOrigin) ? path : $"{_options.PublicOrigin.TrimEnd('/')}{path}";
    }

    private JsonObject BuildResourceType(string name, string endpoint, string schema)
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:ResourceType"),
            ["id"] = name,
            ["name"] = name,
            ["endpoint"] = endpoint,
            ["schema"] = schema,
            ["schemaExtensions"] = new JsonArray(),
            ["meta"] = new JsonObject
            {
                ["resourceType"] = "ResourceType",
                ["location"] = BuildScimLocation($"ResourceTypes/{name}")
            }
        };

    private JsonObject BuildUserSchema()
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Schema"),
            ["id"] = UserSchema,
            ["name"] = "User",
            ["description"] = "SqlOS SCIM user",
            ["attributes"] = new JsonArray(
                SchemaAttribute("userName", "string", required: true, uniqueness: "server"),
                SchemaAttribute("externalId", "string", caseExact: true),
                SchemaAttribute("displayName", "string"),
                SchemaAttribute("active", "boolean"),
                SchemaAttribute("name", "complex", subAttributes: new JsonArray(
                    SchemaAttribute("formatted", "string"),
                    SchemaAttribute("givenName", "string"),
                    SchemaAttribute("familyName", "string"))),
                SchemaAttribute("emails", "complex", multiValued: true, subAttributes: new JsonArray(
                    SchemaAttribute("value", "string"),
                    SchemaAttribute("type", "string"),
                    SchemaAttribute("primary", "boolean"))),
                SchemaAttribute("groups", "complex", multiValued: true, mutability: "readOnly", subAttributes: new JsonArray(
                    SchemaAttribute("value", "string", mutability: "readOnly"),
                    SchemaAttribute("$ref", "reference", mutability: "readOnly", referenceTypes: ["Group"]),
                    SchemaAttribute("display", "string", mutability: "readOnly")))),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = BuildScimLocation($"Schemas/{Uri.EscapeDataString(UserSchema)}") }
        };

    private JsonObject BuildGroupSchema()
        => new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:Schema"),
            ["id"] = GroupSchema,
            ["name"] = "Group",
            ["description"] = "SqlOS SCIM group",
            ["attributes"] = new JsonArray(
                SchemaAttribute("displayName", "string", required: true, uniqueness: "server"),
                SchemaAttribute("externalId", "string", caseExact: true),
                SchemaAttribute("members", "complex", multiValued: true, subAttributes: new JsonArray(
                    SchemaAttribute("value", "string", mutability: "immutable"),
                    SchemaAttribute("$ref", "reference", mutability: "immutable", referenceTypes: ["User"]),
                    SchemaAttribute("display", "string", mutability: "readOnly")))),
            ["meta"] = new JsonObject { ["resourceType"] = "Schema", ["location"] = BuildScimLocation($"Schemas/{Uri.EscapeDataString(GroupSchema)}") }
        };

    private static JsonObject SchemaAttribute(
        string name,
        string type,
        bool required = false,
        bool multiValued = false,
        string mutability = "readWrite",
        string uniqueness = "none",
        bool caseExact = false,
        string[]? referenceTypes = null,
        JsonArray? subAttributes = null)
    {
        var attribute = new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["multiValued"] = multiValued,
            ["required"] = required,
            ["caseExact"] = caseExact,
            ["mutability"] = mutability,
            ["returned"] = "default",
            ["uniqueness"] = uniqueness
        };
        if (subAttributes != null)
        {
            attribute["subAttributes"] = subAttributes;
        }
        if (referenceTypes != null)
        {
            attribute["referenceTypes"] = new JsonArray(referenceTypes.Select(value => (JsonNode?)value).ToArray());
        }
        return attribute;
    }

    private static ScimAttributeSelection CreateAttributeSelection(string? attributes, string? excludedAttributes)
    {
        var included = ParseAttributeSet(attributes);
        var excluded = ParseAttributeSet(excludedAttributes);
        if (included != null && excluded != null)
        {
            throw new SqlOSScimException(
                StatusCodes.Status400BadRequest,
                "SCIM attributes and excludedAttributes cannot be used together.",
                "invalidSyntax");
        }
        return new ScimAttributeSelection(included, excluded ?? []);
    }

    private static HashSet<string>? ParseAttributeSet(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeAttributePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeAttributePath(string attribute)
    {
        foreach (var schema in new[] { UserSchema, GroupSchema })
        {
            var prefix = schema + ":";
            if (attribute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return attribute[prefix.Length..];
            }
        }
        return attribute;
    }

    private static void AddSelected(JsonObject result, ScimAttributeSelection selection, string name, object? value)
    {
        if (selection.Includes(name) && value != null)
        {
            result[name] = JsonValue.Create(value);
        }
    }

    private static void AddSelected(
        JsonObject result,
        ScimAttributeSelection selection,
        string attributePath,
        string outputName,
        object? value)
    {
        if (selection.Includes(attributePath) && value != null)
        {
            result[outputName] = JsonValue.Create(value);
        }
    }

    private static object SummarizeSyncEventData(object data)
    {
        if (data is JsonObject payload)
        {
            return new
            {
                attributeNames = payload.Select(x => x.Key).Where(x => !IsSensitiveKey(x)).OrderBy(x => x).ToArray(),
                operationCount = TryGetNode(payload, "Operations") is JsonArray operations ? operations.Count : (int?)null,
                sensitiveAttributesRedacted = payload.Any(x => IsSensitiveKey(x.Key))
            };
        }
        return data;
    }

    private static bool IsSensitiveKey(string key)
        => new[] { "password", "secret", "token", "authorization", "cookie", "apiKey", "api_key", "privateKey", "private_key" }
            .Any(part => key.Contains(part, StringComparison.OrdinalIgnoreCase));

    private sealed record ScimUserRequest(
        string UserName,
        string? ExternalId,
        string? PrimaryEmail,
        string DisplayName,
        string? FormattedName,
        string? GivenName,
        string? FamilyName,
        bool Active);
    private sealed record ScimGroupRequest(string DisplayName, string? ExternalId, IReadOnlyList<string> Members);
    private sealed record ScimPatchOperation(string Op, string? Path, JsonNode? Value);

    private sealed class ScimUserState
    {
        public string ResourceId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string? ExternalId { get; set; }
        public string? PrimaryEmail { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string? FormattedName { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public bool Active { get; set; }

        public ScimUserRequest ToRequest()
            => new(
                NormalizeRequired(UserName, "SCIM userName is required.", 450),
                NormalizeOptional(ExternalId, 450),
                NormalizeOptional(PrimaryEmail, 320),
                NormalizeRequired(DisplayName, "SCIM displayName cannot be empty.", 300),
                NormalizeOptional(FormattedName, 300),
                NormalizeOptional(GivenName, 150),
                NormalizeOptional(FamilyName, 150),
                Active);
    }

    private sealed class ScimGroupState
    {
        public string ResourceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? ExternalId { get; set; }
        public List<string> Members { get; set; } = [];

        public ScimGroupRequest ToRequest()
            => new(
                NormalizeRequired(DisplayName, "SCIM group displayName is required.", 300),
                NormalizeOptional(ExternalId, 450),
                Members.Distinct(StringComparer.Ordinal).ToList());
    }

    private sealed record ScimAttributeSelection(HashSet<string>? Included, HashSet<string> Excluded)
    {
        public bool Includes(string attribute)
            => !Excluded.Any(excluded => attribute.Equals(excluded, StringComparison.OrdinalIgnoreCase)
                    || attribute.StartsWith(excluded + ".", StringComparison.OrdinalIgnoreCase))
                && (Included == null
                    || Included.Contains(attribute)
                    || Included.Any(included => included.StartsWith(attribute + ".", StringComparison.OrdinalIgnoreCase)
                        || attribute.StartsWith(included + ".", StringComparison.OrdinalIgnoreCase)));
    }
}
