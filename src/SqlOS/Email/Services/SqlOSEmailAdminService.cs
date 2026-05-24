using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Contracts;
using SqlOS.Email.Models;

namespace SqlOS.Email.Services;

public sealed partial class SqlOSEmailAdminService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly SqlOSEmailTemplateRenderer _renderer;

    public SqlOSEmailAdminService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        SqlOSEmailTemplateRenderer renderer)
    {
        _context = context;
        _cryptoService = cryptoService;
        _renderer = renderer;
    }

    public async Task<object> ListTemplatesAsync(
        string? search = null,
        bool includeInactive = true,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSEmailTemplate>().AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            query = query.Where(x => x.Key.Contains(trimmed) || x.DisplayName.Contains(trimmed));
        }

        query = query.OrderBy(x => x.Key);
        return await PaginateAsync(query, resolvedPage, resolvedPageSize, ToTemplateSummary, cancellationToken);
    }

    public async Task EnsureBuiltInTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var builtInKeys = SqlOSBuiltInEmailTemplates.All.Select(definition => definition.Key).ToList();
        var existingKeys = await _context.Set<SqlOSEmailTemplate>()
            .Where(template => builtInKeys.Contains(template.Key))
            .Select(template => template.Key)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var existingKeySet = existingKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var definition in SqlOSBuiltInEmailTemplates.All)
        {
            if (existingKeySet.Contains(definition.Key))
            {
                continue;
            }

            var template = new SqlOSEmailTemplate
            {
                Id = _cryptoService.GenerateId("emt"),
                Key = definition.Key,
                DisplayName = definition.DisplayName,
                SubjectTemplate = definition.SubjectTemplate,
                HtmlBodyTemplate = definition.HtmlBodyTemplate,
                TextBodyTemplate = definition.TextBodyTemplate,
                VariablesJson = definition.VariablesJson,
                IsActive = true,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };

            _context.Set<SqlOSEmailTemplate>().Add(template);
            AddAuditEvent("email.template.seeded", template, new { templateId = template.Id, template.Key, template.Version });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<object> GetTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var template = await GetRequiredTemplateAsync(templateId, cancellationToken);
        return ToTemplateDetail(template);
    }

    public async Task<object> CreateTemplateAsync(
        SqlOSCreateEmailTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeTemplateRequest(request);
        if (await _context.Set<SqlOSEmailTemplate>().AnyAsync(x => x.Key == normalized.Key, cancellationToken))
        {
            throw new InvalidOperationException($"Email template '{normalized.Key}' already exists.");
        }

        var now = DateTime.UtcNow;
        var template = new SqlOSEmailTemplate
        {
            Id = _cryptoService.GenerateId("emt"),
            Key = normalized.Key,
            DisplayName = normalized.DisplayName,
            SubjectTemplate = normalized.SubjectTemplate,
            HtmlBodyTemplate = normalized.HtmlBodyTemplate,
            TextBodyTemplate = normalized.TextBodyTemplate,
            VariablesJson = normalized.VariablesJson,
            IsActive = normalized.IsActive,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.Set<SqlOSEmailTemplate>().Add(template);
        AddAuditEvent("email.template.created", template, new { templateId = template.Id, template.Key, template.Version });
        await _context.SaveChangesAsync(cancellationToken);
        return ToTemplateDetail(template);
    }

    public async Task<object> UpdateTemplateAsync(
        string templateId,
        SqlOSUpdateEmailTemplateRequest request,
        CancellationToken cancellationToken = default)
    {
        var template = await GetRequiredTemplateAsync(templateId, cancellationToken);
        var normalized = NormalizeTemplateRequest(request);
        if (await _context.Set<SqlOSEmailTemplate>().AnyAsync(x => x.Id != template.Id && x.Key == normalized.Key, cancellationToken))
        {
            throw new InvalidOperationException($"Email template '{normalized.Key}' already exists.");
        }

        var contentChanged =
            !string.Equals(template.Key, normalized.Key, StringComparison.Ordinal)
            || !string.Equals(template.SubjectTemplate, normalized.SubjectTemplate, StringComparison.Ordinal)
            || !string.Equals(template.HtmlBodyTemplate, normalized.HtmlBodyTemplate, StringComparison.Ordinal)
            || !string.Equals(template.TextBodyTemplate, normalized.TextBodyTemplate, StringComparison.Ordinal)
            || !string.Equals(template.VariablesJson, normalized.VariablesJson, StringComparison.Ordinal);

        template.Key = normalized.Key;
        template.DisplayName = normalized.DisplayName;
        template.SubjectTemplate = normalized.SubjectTemplate;
        template.HtmlBodyTemplate = normalized.HtmlBodyTemplate;
        template.TextBodyTemplate = normalized.TextBodyTemplate;
        template.VariablesJson = normalized.VariablesJson;
        template.IsActive = normalized.IsActive;
        template.UpdatedAt = DateTime.UtcNow;
        if (contentChanged)
        {
            template.Version++;
        }

        AddAuditEvent("email.template.updated", template, new { templateId = template.Id, template.Key, template.Version, contentChanged });
        await _context.SaveChangesAsync(cancellationToken);
        return ToTemplateDetail(template);
    }

    public async Task DeleteTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var template = await GetRequiredTemplateAsync(templateId, cancellationToken);
        var hasDeliveries = await _context.Set<SqlOSEmailDelivery>()
            .AnyAsync(x => x.TemplateId == template.Id || x.TemplateKey == template.Key, cancellationToken);

        if (hasDeliveries)
        {
            template.IsActive = false;
            template.UpdatedAt = DateTime.UtcNow;
            AddAuditEvent("email.template.deactivated", template, new { templateId = template.Id, template.Key, template.Version });
        }
        else
        {
            _context.Set<SqlOSEmailTemplate>().Remove(template);
            AddAuditEvent("email.template.deleted", template, new { templateId = template.Id, template.Key, template.Version });
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SqlOSRenderedEmailPreview> PreviewTemplateAsync(
        string templateId,
        JsonObject? variables,
        CancellationToken cancellationToken = default)
    {
        var template = await GetRequiredTemplateAsync(templateId, cancellationToken);
        return _renderer.Render(template, SqlOSEmailTemplateRenderer.ToDictionary(variables));
    }

    public async Task<object> ListMessagesAsync(
        string? status = null,
        string? templateKey = null,
        string? recipient = null,
        DateTime? from = null,
        DateTime? to = null,
        int? page = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var (resolvedPage, resolvedPageSize) = NormalizePagination(page, pageSize);
        var query = _context.Set<SqlOSEmailDelivery>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(status) && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedStatus = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == normalizedStatus);
        }

        if (!string.IsNullOrWhiteSpace(templateKey))
        {
            var normalizedTemplateKey = templateKey.Trim();
            query = query.Where(x => x.TemplateKey == normalizedTemplateKey);
        }

        if (!string.IsNullOrWhiteSpace(recipient))
        {
            var normalizedRecipient = recipient.Trim();
            query = query.Where(x => x.To.Contains(normalizedRecipient));
        }

        if (from != null)
        {
            query = query.Where(x => x.CreatedAt >= from.Value);
        }

        if (to != null)
        {
            query = query.Where(x => x.CreatedAt <= to.Value);
        }

        query = query.OrderByDescending(x => x.CreatedAt);
        return await PaginateAsync(query, resolvedPage, resolvedPageSize, ToDeliveryListItem, cancellationToken);
    }

    private async Task<SqlOSEmailTemplate> GetRequiredTemplateAsync(
        string templateId,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSEmailTemplate>()
            .FirstOrDefaultAsync(x => x.Id == templateId || x.Key == templateId, cancellationToken)
            ?? throw new InvalidOperationException("Email template not found.");

    private static object ToTemplateSummary(SqlOSEmailTemplate template)
        => new
        {
            template.Id,
            template.Key,
            template.DisplayName,
            template.IsActive,
            template.Version,
            template.CreatedAt,
            template.UpdatedAt,
            Variables = ParseJsonObject(template.VariablesJson)
        };

    private static object ToTemplateDetail(SqlOSEmailTemplate template)
        => new
        {
            template.Id,
            template.Key,
            template.DisplayName,
            template.SubjectTemplate,
            template.HtmlBodyTemplate,
            template.TextBodyTemplate,
            template.IsActive,
            template.Version,
            template.CreatedAt,
            template.UpdatedAt,
            Variables = ParseJsonObject(template.VariablesJson)
        };

    private static object ToDeliveryListItem(SqlOSEmailDelivery delivery)
        => new
        {
            delivery.Id,
            delivery.To,
            delivery.TemplateKey,
            delivery.TemplateVersion,
            delivery.Status,
            delivery.ProviderMessageId,
            delivery.SanitizedError,
            delivery.RenderedSubject,
            delivery.RenderedTextPreview,
            delivery.RenderedHtmlPreview,
            delivery.IdempotencyKey,
            delivery.CreatedAt,
            delivery.UpdatedAt,
            delivery.SentAt,
            delivery.FailedAt
        };

    private static TemplateRequest NormalizeTemplateRequest(SqlOSCreateEmailTemplateRequest request)
        => NormalizeTemplateRequest(
            request.Key,
            request.DisplayName,
            request.SubjectTemplate,
            request.HtmlBodyTemplate,
            request.TextBodyTemplate,
            request.Variables,
            request.IsActive);

    private static TemplateRequest NormalizeTemplateRequest(SqlOSUpdateEmailTemplateRequest request)
        => NormalizeTemplateRequest(
            request.Key,
            request.DisplayName,
            request.SubjectTemplate,
            request.HtmlBodyTemplate,
            request.TextBodyTemplate,
            request.Variables,
            request.IsActive);

    private static TemplateRequest NormalizeTemplateRequest(
        string key,
        string displayName,
        string subjectTemplate,
        string htmlBodyTemplate,
        string textBodyTemplate,
        JsonObject? variables,
        bool isActive)
    {
        var normalizedKey = (key ?? string.Empty).Trim();
        if (!TemplateKeyRegex().IsMatch(normalizedKey))
        {
            throw new InvalidOperationException("Email template key must be 1-120 characters and contain only letters, numbers, dots, underscores, and dashes.");
        }

        var normalizedSubject = RequireTemplateValue(subjectTemplate, "Subject template");
        var normalizedHtml = RequireTemplateValue(htmlBodyTemplate, "HTML body template");
        var normalizedText = RequireTemplateValue(textBodyTemplate, "Text body template");
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName) ? normalizedKey : displayName.Trim();
        var variablesJson = variables == null ? "{}" : variables.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        return new TemplateRequest(
            normalizedKey,
            SqlOSTransactionalEmailService.TrimTo(normalizedDisplayName, 200),
            SqlOSTransactionalEmailService.TrimTo(normalizedSubject, 500),
            normalizedHtml,
            normalizedText,
            variablesJson,
            isActive);
    }

    private static string RequireTemplateValue(string value, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        return normalized;
    }

    private static object ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private void AddAuditEvent(string eventType, SqlOSEmailTemplate template, object data)
    {
        _context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            EventType = eventType,
            ActorType = "email_template",
            ActorId = template.Id,
            OccurredAt = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(data)
        });
    }

    private static (int Page, int PageSize) NormalizePagination(int? page, int? pageSize)
    {
        var resolvedPage = Math.Max(1, page.GetValueOrDefault(1));
        var resolvedPageSize = Math.Clamp(pageSize.GetValueOrDefault(25), 1, 100);
        return (resolvedPage, resolvedPageSize);
    }

    private static async Task<object> PaginateAsync<T>(
        IQueryable<T> query,
        int page,
        int pageSize,
        Func<T, object> selector,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var currentPage = Math.Min(page, totalPages);
        var data = await query
            .Skip((currentPage - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new
        {
            Data = data.Select(selector).ToList(),
            Page = currentPage,
            PageSize = pageSize,
            TotalCount = totalCount,
            TotalPages = totalPages
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateKeyRegex();

    private sealed record TemplateRequest(
        string Key,
        string DisplayName,
        string SubjectTemplate,
        string HtmlBodyTemplate,
        string TextBodyTemplate,
        string VariablesJson,
        bool IsActive);
}
