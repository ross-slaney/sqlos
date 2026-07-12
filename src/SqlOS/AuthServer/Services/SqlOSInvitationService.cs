using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSInvitationService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSAdminService _adminService;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly ISqlOSAuthEmailSender _emailSender;
    private readonly ISqlOSTransactionalEmailService? _transactionalEmailService;
    private readonly SqlOSSettingsService _settingsService;
    private readonly SqlOSAuthServerOptions _options;
    private readonly SqlOSInvitationOptions _invitationOptions;

    public SqlOSInvitationService(
        ISqlOSAuthServerDbContext context,
        SqlOSAdminService adminService,
        SqlOSCryptoService cryptoService,
        ISqlOSAuthEmailSender emailSender,
        SqlOSSettingsService settingsService,
        IOptions<SqlOSAuthServerOptions> options,
        ISqlOSTransactionalEmailService? transactionalEmailService = null)
    {
        _context = context;
        _adminService = adminService;
        _cryptoService = cryptoService;
        _emailSender = emailSender;
        _transactionalEmailService = transactionalEmailService;
        _settingsService = settingsService;
        _options = options.Value;
        _invitationOptions = _options.Invitations;
    }

    public async Task<SqlOSEmailInvitationResult> CreateEmailInvitationAsync(
        SqlOSCreateEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var organization = await RequireActiveOrganizationAsync(request.OrganizationId, cancellationToken);
        var normalizedEmail = SqlOSAdminService.NormalizeEmail(request.Email);
        var invitedEmail = request.Email.Trim();
        var role = NormalizeRole(request.Role);
        var now = DateTime.UtcNow;
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        var expiresAt = request.ExpiresAt ?? now.Add(_invitationOptions.DefaultLifetime);
        if (expiresAt <= now)
        {
            throw new InvalidOperationException("Invitation expiration must be in the future.");
        }

        SqlOSClientApplication? client = null;
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            client = await _adminService.RequireClientAsync(request.ClientId, request.RedirectUri, cancellationToken, httpContext);
        }

        await EnforceRateLimitsAsync(organization.Id, normalizedEmail, request.InvitedByUserId, ipAddress, now, cancellationToken);

        var superseded = await _context.Set<SqlOSInvitation>()
            .Where(x => x.OrganizationId == organization.Id
                && x.NormalizedEmail == normalizedEmail
                && x.AcceptedAt == null
                && x.RevokedAt == null
                && x.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        foreach (var supersededInvitation in superseded)
        {
            supersededInvitation.RevokedAt = now;
            supersededInvitation.RevokedReason = "superseded";
        }

        var rawToken = _cryptoService.GenerateOpaqueToken();
        var invitation = new SqlOSInvitation
        {
            Id = _cryptoService.GenerateId("inv"),
            OrganizationId = organization.Id,
            InvitedEmail = invitedEmail,
            NormalizedEmail = normalizedEmail,
            Role = role,
            TokenHash = _cryptoService.HashToken(rawToken),
            InvitedByUserId = string.IsNullOrWhiteSpace(request.InvitedByUserId) ? null : request.InvitedByUserId.Trim(),
            ClientApplicationId = client?.Id,
            RedirectUri = string.IsNullOrWhiteSpace(request.RedirectUri) ? null : request.RedirectUri.Trim(),
            Scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim(),
            Resource = string.IsNullOrWhiteSpace(request.Resource) ? null : request.Resource.Trim(),
            CustomFieldsJson = request.CustomFields?.ToJsonString(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            IpAddress = ipAddress,
            UserAgent = httpContext?.Request.Headers.UserAgent.ToString()
        };

        _context.Set<SqlOSInvitation>().Add(invitation);
        AddAuditEvent(
            "invitation.created",
            "system",
            request.InvitedByUserId,
            organization.Id,
            ipAddress,
            new { maskedEmail = MaskEmail(invitedEmail), invitation.Role, clientId = client?.ClientId });
        await _context.SaveChangesAsync(cancellationToken);

        var inviteUrl = BuildAcceptUrl(rawToken, httpContext);
        if (request.SendEmail)
        {
            await SendInvitationEmailAsync(invitation, organization, rawToken, httpContext, cancellationToken);
        }

        return ToResult(invitation, organization, inviteUrl);
    }

    public async Task<object> ListOrganizationInvitationsAsync(
        string organizationId,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireActiveOrganizationAsync(organizationId, cancellationToken);
        var resolvedPage = Math.Max(1, page.GetValueOrDefault(1));
        var resolvedPageSize = Math.Clamp(pageSize.GetValueOrDefault(50), 1, 200);
        var query = _context.Set<SqlOSInvitation>()
            .AsNoTracking()
            .Include(x => x.Organization)
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.CreatedAt);

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)resolvedPageSize));
        var currentPage = Math.Min(resolvedPage, totalPages);
        var data = await query
            .Skip((currentPage - 1) * resolvedPageSize)
            .Take(resolvedPageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            Data = data.Select(x => ToResult(x, x.Organization!, inviteUrl: null)).ToList(),
            Page = currentPage,
            PageSize = resolvedPageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    public async Task<SqlOSEmailInvitationResult> ResendEmailInvitationAsync(
        SqlOSResendEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var invitation = await _context.Set<SqlOSInvitation>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == request.InvitationId, cancellationToken)
            ?? throw new InvalidOperationException("Invitation not found.");
        EnsureInvitationPending(invitation);

        var rawToken = _cryptoService.GenerateOpaqueToken();
        invitation.TokenHash = _cryptoService.HashToken(rawToken);
        invitation.LastSendError = null;
        await _context.SaveChangesAsync(cancellationToken);

        await SendInvitationEmailAsync(invitation, invitation.Organization!, rawToken, httpContext, cancellationToken);
        return ToResult(invitation, invitation.Organization!, BuildAcceptUrl(rawToken, httpContext));
    }

    public async Task<SqlOSEmailInvitationResult> RevokeEmailInvitationAsync(
        SqlOSRevokeEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var invitation = await _context.Set<SqlOSInvitation>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == request.InvitationId, cancellationToken)
            ?? throw new InvalidOperationException("Invitation not found.");

        if (invitation.AcceptedAt != null)
        {
            throw new InvalidOperationException("Accepted invitations cannot be revoked.");
        }

        if (invitation.RevokedAt == null)
        {
            invitation.RevokedAt = DateTime.UtcNow;
            invitation.RevokedReason = string.IsNullOrWhiteSpace(request.Reason) ? "revoked" : request.Reason.Trim();
            AddAuditEvent(
                "invitation.revoked",
                "system",
                null,
                invitation.OrganizationId,
                httpContext?.Connection.RemoteIpAddress?.ToString(),
                new { invitation.Id, maskedEmail = MaskEmail(invitation.InvitedEmail), invitation.RevokedReason });
            await _context.SaveChangesAsync(cancellationToken);
        }

        return ToResult(invitation, invitation.Organization!, inviteUrl: null);
    }

    public async Task<SqlOSEmailInvitationResult> ResolveEmailInvitationAsync(
        string invitationToken,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var invitation = await FindInvitationByTokenAsync(invitationToken, cancellationToken);
        EnsureInvitationPending(invitation);
        return ToResult(invitation, invitation.Organization!, BuildAcceptUrl(invitationToken.Trim(), httpContext));
    }

    public async Task<SqlOSEmailInvitationResult> BindInvitationToAuthorizationRequestAsync(
        string invitationToken,
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken = default)
    {
        var invitation = await FindInvitationByTokenAsync(invitationToken, cancellationToken);
        EnsureInvitationPending(invitation);

        authorizationRequest.InvitationId = invitation.Id;
        authorizationRequest.LoginHintEmail = invitation.InvitedEmail;
        await _context.SaveChangesAsync(cancellationToken);
        return ToResult(invitation, invitation.Organization!, inviteUrl: null);
    }

    public async Task<SqlOSEmailInvitationResult?> GetBoundInvitationAsync(
        SqlOSAuthorizationRequest authorizationRequest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationRequest.InvitationId))
        {
            return null;
        }

        var invitation = await _context.Set<SqlOSInvitation>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == authorizationRequest.InvitationId, cancellationToken);
        return invitation == null ? null : ToResult(invitation, invitation.Organization!, inviteUrl: null);
    }

    public async Task<SqlOSInvitationAcceptanceResult> AcceptEmailInvitationAsync(
        SqlOSAcceptEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        try
        {
            if (SupportsDatabaseTransactions())
            {
                transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            var invitation = await FindInvitationByTokenAsync(request.InvitationToken, cancellationToken);
            var result = await AcceptInvitationForUserAsync(
                invitation,
                request.UserId,
                saveChanges: true,
                httpContext,
                cancellationToken);

            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<SqlOSInvitationAcceptanceResult> AcceptEmailInvitationInCurrentTransactionAsync(
        SqlOSAcceptEmailInvitationRequest request,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        var invitation = await FindInvitationByTokenAsync(request.InvitationToken, cancellationToken);
        return await AcceptInvitationForUserAsync(
            invitation,
            request.UserId,
            saveChanges: false,
            httpContext,
            cancellationToken);
    }

    public async Task<SqlOSInvitationAcceptanceResult?> AcceptBoundInvitationAsync(
        string? invitationId,
        string userId,
        bool saveChanges,
        HttpContext? httpContext = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invitationId))
        {
            return null;
        }

        var invitation = await _context.Set<SqlOSInvitation>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.Id == invitationId, cancellationToken)
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");
        if (invitation.Organization == null || !invitation.Organization.IsActive)
        {
            throw new InvalidOperationException("Invitation is invalid or expired.");
        }

        if (invitation.AcceptedAt != null)
        {
            if (!string.Equals(invitation.AcceptedByUserId, userId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invitation is invalid or expired.");
            }

            var membership = await _context.Set<SqlOSMembership>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x =>
                    x.OrganizationId == invitation.OrganizationId
                    && x.UserId == userId
                    && x.IsActive,
                    cancellationToken)
                ?? throw new InvalidOperationException("Invitation is invalid or expired.");

            return new SqlOSInvitationAcceptanceResult(
                invitation.Id,
                invitation.OrganizationId,
                userId,
                membership.Role,
                invitation.AcceptedAt.Value,
                MembershipCreated: false,
                MembershipReactivated: false,
                EmailVerified: false);
        }

        return await AcceptInvitationForUserAsync(invitation, userId, saveChanges, httpContext, cancellationToken);
    }

    private async Task<SqlOSInvitationAcceptanceResult> AcceptInvitationForUserAsync(
        SqlOSInvitation invitation,
        string userId,
        bool saveChanges,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        EnsureInvitationPending(invitation);
        var now = DateTime.UtcNow;
        var user = await _context.Set<SqlOSUser>()
            .Include(x => x.Emails)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        if (!user.IsActive)
        {
            throw new InvalidOperationException("User is not active.");
        }

        var email = user.Emails.FirstOrDefault(x => x.NormalizedEmail == invitation.NormalizedEmail)
            ?? throw new InvalidOperationException("This invitation was sent to another email address.");

        var emailVerified = false;
        if (!email.IsVerified)
        {
            email.IsVerified = true;
            email.VerifiedAt = now;
            user.DefaultEmail = email.Email;
            user.UpdatedAt = now;
            emailVerified = true;
        }

        var membershipCreated = false;
        var membershipReactivated = false;
        var membership = await _context.Set<SqlOSMembership>()
            .FirstOrDefaultAsync(x => x.OrganizationId == invitation.OrganizationId && x.UserId == user.Id, cancellationToken);
        if (membership == null)
        {
            membership = new SqlOSMembership
            {
                OrganizationId = invitation.OrganizationId,
                UserId = user.Id,
                Role = invitation.Role,
                IsActive = true,
                CreatedAt = now
            };
            _context.Set<SqlOSMembership>().Add(membership);
            membershipCreated = true;
        }
        else if (!membership.IsActive)
        {
            membership.IsActive = true;
            membership.Role = invitation.Role;
            membershipReactivated = true;
        }

        invitation.AcceptedAt = now;
        invitation.AcceptedByUserId = user.Id;
        AddAuditEvent(
            "invitation.accepted",
            "user",
            user.Id,
            invitation.OrganizationId,
            httpContext?.Connection.RemoteIpAddress?.ToString(),
            new
            {
                invitation.Id,
                maskedEmail = MaskEmail(invitation.InvitedEmail),
                inviteRole = invitation.Role,
                membershipRole = membership.Role,
                membershipCreated,
                membershipReactivated,
                emailVerified
            },
            user.Id);

        if (saveChanges)
        {
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException("Invitation is invalid or expired.");
            }
        }

        return new SqlOSInvitationAcceptanceResult(
            invitation.Id,
            invitation.OrganizationId,
            user.Id,
            membership.Role,
            now,
            membershipCreated,
            membershipReactivated,
            emailVerified);
    }

    private async Task SendInvitationEmailAsync(
        SqlOSInvitation invitation,
        SqlOSOrganization organization,
        string rawToken,
        HttpContext? httpContext,
        CancellationToken cancellationToken)
    {
        var inviteUrl = BuildAcceptUrl(rawToken, httpContext);
        try
        {
            await SendInvitationMessageAsync(invitation, organization, inviteUrl, cancellationToken);
            invitation.LastSentAt = DateTime.UtcNow;
            invitation.LastSendError = null;
            AddAuditEvent(
                "invitation.sent",
                "system",
                invitation.InvitedByUserId,
                invitation.OrganizationId,
                httpContext?.Connection.RemoteIpAddress?.ToString(),
                new { invitation.Id, maskedEmail = MaskEmail(invitation.InvitedEmail), invitation.Role });
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            invitation.LastSendError = ex.Message;
            AddAuditEvent(
                "invitation.send_failed",
                "system",
                invitation.InvitedByUserId,
                invitation.OrganizationId,
                httpContext?.Connection.RemoteIpAddress?.ToString(),
                new { invitation.Id, maskedEmail = MaskEmail(invitation.InvitedEmail), error = ex.Message });
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("We couldn't send the invitation email right now.");
        }
    }

    private async Task SendInvitationMessageAsync(
        SqlOSInvitation invitation,
        SqlOSOrganization organization,
        string acceptUrl,
        CancellationToken cancellationToken)
    {
        var context = await BuildMessageContextAsync(invitation, organization, acceptUrl, cancellationToken);
        if (_invitationOptions.BuildMessage != null)
        {
            await _emailSender.SendAsync(BuildLegacyMessage(context), cancellationToken);
            return;
        }

        var transactionalEmailService = _transactionalEmailService
            ?? throw new InvalidOperationException("Transactional email service is not registered.");
        var result = await transactionalEmailService.SendAsync(
            new SqlOSSendEmailRequest(
                SqlOSBuiltInEmailTemplates.AuthInvitationKey,
                invitation.InvitedEmail,
                BuildTemplateVariables(context),
                IdempotencyKey: $"auth-invitation:{invitation.Id}:{invitation.TokenHash[..Math.Min(32, invitation.TokenHash.Length)]}"),
            cancellationToken);

        if (string.Equals(result.Status, SqlOSEmailDeliveryStatuses.Failed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(result.SanitizedError ?? "Invitation email delivery failed.");
        }
    }

    private async Task<SqlOSInvitationMessageContext> BuildMessageContextAsync(
        SqlOSInvitation invitation,
        SqlOSOrganization organization,
        string acceptUrl,
        CancellationToken cancellationToken)
    {
        var branding = await _settingsService.GetResolvedAuthEmailBrandingAsync(cancellationToken);
        var applicationName = !string.IsNullOrWhiteSpace(_invitationOptions.ApplicationName)
            ? _invitationOptions.ApplicationName.Trim()
            : !string.IsNullOrWhiteSpace(branding.ApplicationName)
                ? branding.ApplicationName
                : !string.IsNullOrWhiteSpace(_options.EmailOtp.ApplicationName)
                    ? _options.EmailOtp.ApplicationName.Trim()
                    : "SqlOS";
        var context = new SqlOSInvitationMessageContext(
            applicationName,
            organization.Name,
            invitation.InvitedEmail,
            MaskEmail(invitation.InvitedEmail),
            invitation.Role,
            acceptUrl,
            invitation.ExpiresAt,
            invitation.ExpiresAt - invitation.CreatedAt)
        {
            Branding = branding with { ApplicationName = applicationName }
        };

        return context;
    }

    private SqlOSAuthEmailMessage BuildLegacyMessage(SqlOSInvitationMessageContext context)
        => _invitationOptions.BuildMessage?.Invoke(context)
            ?? new SqlOSAuthEmailMessage(
                context.Email,
                $"You're invited to {context.OrganizationName}",
                SqlOSAuthEmailTemplateRenderer.BuildInvitationHtmlBody(context),
                SqlOSAuthEmailTemplateRenderer.BuildInvitationTextBody(context));

    private static IReadOnlyDictionary<string, object?> BuildTemplateVariables(SqlOSInvitationMessageContext context)
    {
        var days = Math.Max(1, (int)Math.Ceiling(context.Lifetime.TotalDays));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["applicationName"] = context.ApplicationName,
            ["logoBase64"] = context.Branding.LogoBase64 ?? string.Empty,
            ["logoImageDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "none" : "block",
            ["logoTextDisplay"] = string.IsNullOrWhiteSpace(context.Branding.LogoBase64) ? "block" : "none",
            ["organizationName"] = context.OrganizationName,
            ["maskedEmail"] = context.MaskedEmail,
            ["role"] = context.Role,
            ["acceptUrl"] = context.AcceptUrl,
            ["expiresInDays"] = days,
            ["primaryColor"] = context.Branding.PrimaryColor,
            ["accentColor"] = context.Branding.AccentColor,
            ["backgroundColor"] = context.Branding.BackgroundColor
        };
    }

    private async Task<SqlOSInvitation> FindInvitationByTokenAsync(string invitationToken, CancellationToken cancellationToken)
    {
        var rawToken = invitationToken?.Trim()
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw new InvalidOperationException("Invitation is invalid or expired.");
        }

        var tokenHash = _cryptoService.HashToken(rawToken);
        return await _context.Set<SqlOSInvitation>()
            .Include(x => x.Organization)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            ?? throw new InvalidOperationException("Invitation is invalid or expired.");
    }

    private async Task<SqlOSOrganization> RequireActiveOrganizationAsync(string organizationId, CancellationToken cancellationToken)
        => await _context.Set<SqlOSOrganization>()
            .FirstOrDefaultAsync(x => x.Id == organizationId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Organization not found or inactive.");

    private async Task EnforceRateLimitsAsync(
        string organizationId,
        string normalizedEmail,
        string? invitedByUserId,
        string? ipAddress,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var since = now.AddHours(-1);
        if (await _context.Set<SqlOSInvitation>().CountAsync(x => x.NormalizedEmail == normalizedEmail && x.CreatedAt >= since, cancellationToken) >= _invitationOptions.MaxInvitationsPerEmailPerHour)
        {
            await RecordRateLimitAsync("email", organizationId, normalizedEmail, ipAddress, cancellationToken);
            throw new InvalidOperationException("Too many invitation emails have been requested for this address. Try again later.");
        }

        if (!string.IsNullOrWhiteSpace(ipAddress)
            && await _context.Set<SqlOSInvitation>().CountAsync(x => x.IpAddress == ipAddress && x.CreatedAt >= since, cancellationToken) >= _invitationOptions.MaxInvitationsPerIpPerHour)
        {
            await RecordRateLimitAsync("ip", organizationId, normalizedEmail, ipAddress, cancellationToken);
            throw new InvalidOperationException("Too many invitation emails have been requested. Try again later.");
        }

        if (await _context.Set<SqlOSInvitation>().CountAsync(x => x.OrganizationId == organizationId && x.CreatedAt >= since, cancellationToken) >= _invitationOptions.MaxInvitationsPerOrganizationPerHour)
        {
            await RecordRateLimitAsync("organization", organizationId, normalizedEmail, ipAddress, cancellationToken);
            throw new InvalidOperationException("Too many invitation emails have been requested for this organization. Try again later.");
        }

        if (!string.IsNullOrWhiteSpace(invitedByUserId)
            && await _context.Set<SqlOSInvitation>().CountAsync(x => x.InvitedByUserId == invitedByUserId && x.CreatedAt >= since, cancellationToken) >= _invitationOptions.MaxInvitationsPerInviterPerHour)
        {
            await RecordRateLimitAsync("inviter", organizationId, normalizedEmail, ipAddress, cancellationToken);
            throw new InvalidOperationException("Too many invitation emails have been requested by this inviter. Try again later.");
        }
    }

    private async Task RecordRateLimitAsync(
        string limit,
        string organizationId,
        string normalizedEmail,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        AddAuditEvent(
            "invitation.rate_limit_rejected",
            "system",
            null,
            organizationId,
            ipAddress,
            new { limit, normalizedEmail });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private void AddAuditEvent(
        string eventType,
        string actorType,
        string? actorId,
        string? organizationId,
        string? ipAddress,
        object? data,
        string? userId = null)
    {
        _context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            EventType = eventType,
            ActorType = actorType,
            ActorId = actorId,
            UserId = userId,
            OrganizationId = organizationId,
            IpAddress = ipAddress,
            DataJson = data == null ? null : JsonSerializer.Serialize(data),
            OccurredAt = DateTime.UtcNow
        });
    }

    private SqlOSEmailInvitationResult ToResult(SqlOSInvitation invitation, SqlOSOrganization organization, string? inviteUrl)
        => new(
            invitation.Id,
            invitation.OrganizationId,
            organization.Name,
            invitation.InvitedEmail,
            invitation.Role,
            GetStatus(invitation),
            inviteUrl,
            invitation.CreatedAt,
            invitation.ExpiresAt,
            invitation.LastSentAt,
            invitation.AcceptedAt,
            invitation.AcceptedByUserId,
            invitation.RevokedAt,
            invitation.RevokedReason,
            invitation.LastSendError,
            ParseCustomFields(invitation.CustomFieldsJson));

    private string BuildAcceptUrl(string rawToken, HttpContext? httpContext)
    {
        var origin = GetPublicOrigin(httpContext);
        return $"{origin}{_options.BasePath.TrimEnd('/')}/invitations/accept?token={Uri.EscapeDataString(rawToken)}";
    }

    private string GetPublicOrigin(HttpContext? httpContext)
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicOrigin))
        {
            return _options.PublicOrigin.TrimEnd('/');
        }

        if (httpContext != null)
        {
            return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}".TrimEnd('/');
        }

        return _options.Issuer.TrimEnd('/').EndsWith(_options.BasePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            ? _options.Issuer.TrimEnd('/')[..^_options.BasePath.TrimEnd('/').Length]
            : _options.Issuer.TrimEnd('/');
    }

    private static void EnsureInvitationPending(SqlOSInvitation invitation)
    {
        if (invitation.AcceptedAt != null || invitation.RevokedAt != null || invitation.ExpiresAt <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("Invitation is invalid or expired.");
        }
    }

    private static string NormalizeRole(string? role)
    {
        var normalized = string.IsNullOrWhiteSpace(role) ? "member" : role.Trim();
        if (normalized.Length > 50)
        {
            throw new InvalidOperationException("Invitation role is too long.");
        }

        return normalized;
    }

    private static string GetStatus(SqlOSInvitation invitation)
    {
        if (invitation.AcceptedAt != null)
        {
            return "accepted";
        }

        if (invitation.RevokedAt != null)
        {
            return "revoked";
        }

        return invitation.ExpiresAt <= DateTime.UtcNow ? "expired" : "pending";
    }

    private static JsonObject? ParseCustomFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private bool SupportsDatabaseTransactions()
        => !string.Equals(_context.Database.ProviderName, "Microsoft.EntityFrameworkCore.InMemory", StringComparison.Ordinal);

    private static string MaskEmail(string email)
    {
        var atIndex = email.IndexOf('@');
        if (atIndex <= 1 || atIndex == email.Length - 1)
        {
            return email;
        }

        var local = email[..atIndex];
        var domain = email[(atIndex + 1)..];
        var visibleCount = Math.Min(2, local.Length);
        return $"{local[..visibleCount]}***@{domain}";
    }

}
