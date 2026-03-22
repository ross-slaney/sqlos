using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Example.Api.Configuration;
using SqlOS.Example.Api.Data;
using SqlOS.Example.Api.FgaRetail.Seeding;
using SqlOS.Example.Api.Services;
using SqlOS.Fga.Models;

namespace SqlOS.Example.Api.Endpoints;

public static class ExampleDemoEndpoints
{
    public static void MapDemoEndpoints(this WebApplication app)
    {
        var demo = app.MapGroup("/api/demo");
        demo.ExcludeFromDescription();

        demo.MapGet("/users", async (ExampleAppDbContext db, CancellationToken cancellationToken) =>
        {
            var org = await db.Set<SqlOSOrganization>()
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Slug == RetailSeedService.RetailOrgSlug, cancellationToken);
            if (org is null)
                return Results.Ok(Array.Empty<DemoSubjectResponse>());

            var primaryEmails = await db.Set<SqlOSUserEmail>()
                .AsNoTracking()
                .Where(e => e.IsPrimary)
                .Select(e => new { e.UserId, e.Email })
                .ToListAsync(cancellationToken);

            var primaryEmailByUserId = primaryEmails
                .GroupBy(e => e.UserId)
                .ToDictionary(g => g.Key, g => g.First().Email);

            var subjectRoles = await db.Set<SqlOSFgaGrant>()
                .AsNoTracking()
                .Join(
                    db.Set<SqlOSFgaRole>().AsNoTracking(),
                    grant => grant.RoleId,
                    role => role.Id,
                    (grant, role) => new { grant.SubjectId, role.Key })
                .ToListAsync(cancellationToken);

            var roleBySubjectId = subjectRoles
                .GroupBy(x => x.SubjectId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(", ", g.Select(x => x.Key).Distinct().OrderBy(x => x)));

            var users = await db.Set<SqlOSMembership>()
                .AsNoTracking()
                .Where(m => m.OrganizationId == org.Id && m.IsActive)
                .Join(
                    db.Set<SqlOSUser>().AsNoTracking().Where(u => u.IsActive),
                    membership => membership.UserId,
                    user => user.Id,
                    (membership, user) => new
                    {
                        user.Id,
                        user.DisplayName,
                        user.DefaultEmail,
                        MembershipRole = membership.Role
                    })
                .OrderBy(x => x.DisplayName)
                .ToListAsync(cancellationToken);

            var userSubjects = users.Select(user =>
            {
                var email = !string.IsNullOrWhiteSpace(user.DefaultEmail)
                    ? user.DefaultEmail
                    : (primaryEmailByUserId.TryGetValue(user.Id, out var fallbackEmail) ? fallbackEmail : null);

                var role = roleBySubjectId.TryGetValue(user.Id, out var fgaRoles)
                    ? fgaRoles
                    : user.MembershipRole;

                return new DemoSubjectResponse(
                    Email: email,
                    DisplayName: user.DisplayName,
                    Role: role,
                    Description: "AuthServer user",
                    Type: "user",
                    Credential: null);
            });

            var agents = await db.Set<SqlOSFgaAgent>()
                .AsNoTracking()
                .Select(agent => new
                {
                    agent.SubjectId,
                    DisplayName = agent.Subject != null && !string.IsNullOrWhiteSpace(agent.Subject.DisplayName)
                        ? agent.Subject.DisplayName
                        : agent.SubjectId,
                    agent.AgentType,
                    agent.Description
                })
                .OrderBy(a => a.DisplayName)
                .ToListAsync(cancellationToken);

            var agentSubjects = agents.Select(agent =>
            {
                var role = roleBySubjectId.TryGetValue(agent.SubjectId, out var fgaRoles)
                    ? fgaRoles
                    : (agent.AgentType ?? "agent");

                return new DemoSubjectResponse(
                    Email: null,
                    DisplayName: agent.DisplayName,
                    Role: role,
                    Description: string.IsNullOrWhiteSpace(agent.Description) ? "Automated agent subject." : agent.Description,
                    Type: "agent",
                    Credential: agent.SubjectId);
            });

            var serviceAccounts = await db.Set<SqlOSFgaServiceAccount>()
                .AsNoTracking()
                .Select(account => new
                {
                    account.SubjectId,
                    account.ClientId,
                    DisplayName = account.Subject != null && !string.IsNullOrWhiteSpace(account.Subject.DisplayName)
                        ? account.Subject.DisplayName
                        : account.ClientId,
                    account.Description
                })
                .OrderBy(x => x.DisplayName)
                .ToListAsync(cancellationToken);

            var serviceAccountSubjects = serviceAccounts.Select(account =>
            {
                var role = roleBySubjectId.TryGetValue(account.SubjectId, out var fgaRoles)
                    ? fgaRoles
                    : "service_account";

                return new DemoSubjectResponse(
                    Email: null,
                    DisplayName: account.DisplayName,
                    Role: role,
                    Description: string.IsNullOrWhiteSpace(account.Description) ? "Service account subject." : account.Description,
                    Type: "service_account",
                    Credential: account.ClientId);
            });

            var subjects = userSubjects
                .Concat(agentSubjects)
                .Concat(serviceAccountSubjects)
                .OrderBy(x => TypeSortOrder(x.Type))
                .ThenBy(x => x.DisplayName)
                .ToArray();

            return Results.Ok(subjects);
        });

        var authDemo = app.MapGroup("/api/v1/auth/demo");
        authDemo.ExcludeFromDescription();

        authDemo.MapPost("/switch", async (
            DemoSwitchRequest request,
            SqlOSAuthService authService,
            ExampleFgaService fgaService,
            ExampleAppDbContext db,
            IOptions<ExampleWebOptions> webOptions,
            IOptions<SqlOS.AuthServer.Configuration.SqlOSAuthServerOptions> authOptions,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var org = await db.Set<SqlOSOrganization>()
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Slug == RetailSeedService.RetailOrgSlug, cancellationToken);

            if (org is null)
                return Results.BadRequest(new { error = "Retail demo organization not found. Has seed data been applied?" });

            var requestedEmail = request.Email?.Trim();
            if (string.IsNullOrWhiteSpace(requestedEmail))
                return Results.BadRequest(new { error = "Email is required." });

            var normalizedEmail = requestedEmail.ToLowerInvariant();
            var userId = await ResolveUserIdByEmailAsync(db, normalizedEmail, cancellationToken);
            if (userId is null)
                return Results.BadRequest(new { error = "Requested user was not found." });

            var isEligibleMember = await db.Set<SqlOSMembership>()
                .AsNoTracking()
                .AnyAsync(m => m.OrganizationId == org.Id && m.UserId == userId && m.IsActive, cancellationToken);
            if (!isEligibleMember)
                return Results.BadRequest(new { error = "Requested user is not eligible for this retail demo." });

            try
            {
                var result = await authService.LoginWithPasswordAsync(
                    new SqlOSPasswordLoginRequest(requestedEmail, RetailSeedService.DemoPassword, webOptions.Value.ClientId, org.Id),
                    httpContext,
                    cancellationToken);

                if (result.Tokens == null)
                    return Results.BadRequest(new { error = "Login did not produce tokens." });

                var validated = await authService.ValidateAccessTokenAsync(
                        result.Tokens.AccessToken,
                        authOptions.Value.DefaultAudience,
                        cancellationToken)
                    ?? throw new InvalidOperationException("Token validation failed after login.");

                await fgaService.EnsureUserAccessAsync(validated.UserId!, org.Id, cancellationToken);

                return Results.Ok(new
                {
                    accessToken = result.Tokens.AccessToken,
                    refreshToken = result.Tokens.RefreshToken,
                    sessionId = result.Tokens.SessionId,
                    organizationId = result.Tokens.OrganizationId,
                    accessTokenExpiresAt = result.Tokens.AccessTokenExpiresAt,
                    refreshTokenExpiresAt = result.Tokens.RefreshTokenExpiresAt,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static async Task<string?> ResolveUserIdByEmailAsync(
        ExampleAppDbContext db,
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var userId = await db.Set<SqlOSUser>()
            .AsNoTracking()
            .Where(u => u.DefaultEmail != null && u.DefaultEmail.ToLower() == normalizedEmail)
            .Select(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(userId))
            return userId;

        return await db.Set<SqlOSUserEmail>()
            .AsNoTracking()
            .Where(e => e.NormalizedEmail == normalizedEmail || e.Email.ToLower() == normalizedEmail)
            .Select(e => e.UserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static int TypeSortOrder(string type)
        => type switch
        {
            "user" => 0,
            "agent" => 1,
            "service_account" => 2,
            _ => 3
        };

    private sealed record DemoSwitchRequest(string Email);
    private sealed record DemoSubjectResponse(
        string? Email,
        string DisplayName,
        string Role,
        string Description,
        string Type,
        string? Credential);
}
