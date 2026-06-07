using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;

namespace SqlOS.AuditLogs;

public sealed class SqlOSAuditLogService : ISqlOSAuditLogService
{
    public const int MaxPageSize = 100;
    public const int MaxExportRows = 5000;
    public static readonly TimeSpan MaxExportRange = TimeSpan.FromDays(366);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RedactedMetadataKeyParts =
    [
        "password",
        "secret",
        "token",
        "authorization",
        "cookie",
        "api_key",
        "apikey",
        "clientsecret",
        "client_secret",
        "private_key",
        "privatekey",
        "stacktrace",
        "stack_trace",
        "exception"
    ];

    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;

    public SqlOSAuditLogService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService)
    {
        _context = context;
        _cryptoService = cryptoService;
    }

    public async Task<SqlOSAuditLogRecordResult> RecordAsync(
        SqlOSAuditLogRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        var action = NormalizeRequired(request.Action, nameof(request.Action), 160);
        var source = NormalizeNullable(request.Source, 80) ?? "application";
        var actor = NormalizeActor(request.Actor);
        var targets = NormalizeTargets(request.Targets);
        var now = DateTime.UtcNow;
        var occurredAt = request.OccurredAt?.ToUniversalTime() ?? now;
        var (applicationId, applicationKey) = await ResolveApplicationAsync(
            request.ApplicationId,
            request.ApplicationKey,
            cancellationToken);
        var idempotencyKeyHash = HashIdempotencyKey(request.IdempotencyKey);

        if (idempotencyKeyHash != null)
        {
            var existing = await _context.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdempotencyKeyHash == idempotencyKeyHash, cancellationToken);
            if (existing != null)
            {
                return new SqlOSAuditLogRecordResult(existing.Id, Created: false, MapEvent(existing));
            }
        }

        var sanitizedMetadata = SanitizeMetadata(request.Metadata);
        var metadataJson = sanitizedMetadata == null
            ? null
            : JsonSerializer.Serialize(sanitizedMetadata, JsonOptions);
        var contextJson = request.Context == null
            ? null
            : JsonSerializer.Serialize(request.Context, JsonOptions);

        var entity = new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            OrganizationId = NormalizeNullable(request.OrganizationId, 64),
            ApplicationId = applicationId,
            ApplicationKey = applicationKey,
            UserId = NormalizeNullable(request.UserId, 64)
                ?? (string.Equals(actor.Type, "user", StringComparison.OrdinalIgnoreCase) ? actor.Id : null),
            SessionId = NormalizeNullable(request.Context?.SessionId, 64),
            EventType = action,
            Source = source,
            Action = action,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplayName = actor.DisplayName,
            TargetsJson = JsonSerializer.Serialize(targets, JsonOptions),
            ContextJson = contextJson,
            MetadataJson = metadataJson,
            DataJson = metadataJson,
            OccurredAt = occurredAt,
            IngestedAt = now,
            IpAddress = NormalizeNullable(request.Context?.IpAddress, 128),
            UserAgent = NormalizeNullable(request.Context?.UserAgent, 512),
            RequestId = NormalizeNullable(request.Context?.RequestId, 128),
            CorrelationId = NormalizeNullable(request.Context?.CorrelationId, 128),
            IdempotencyKeyHash = idempotencyKeyHash
        };

        _context.Set<SqlOSAuditEvent>().Add(entity);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKeyHash != null)
        {
            var existing = await _context.Set<SqlOSAuditEvent>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdempotencyKeyHash == idempotencyKeyHash, cancellationToken);
            if (existing != null)
            {
                return new SqlOSAuditLogRecordResult(existing.Id, Created: false, MapEvent(existing));
            }

            throw;
        }

        return new SqlOSAuditLogRecordResult(entity.Id, Created: true, MapEvent(entity));
    }

    public async Task<SqlOSAuditLogListResult> ListAsync(
        SqlOSAuditLogListRequest request,
        CancellationToken cancellationToken = default)
    {
        var (page, pageSize) = NormalizePagination(request.Page, request.PageSize);
        var query = ApplyFilters(_context.Set<SqlOSAuditEvent>().AsNoTracking(), request);
        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        var events = await query
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.IngestedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new SqlOSAuditLogListResult(
            events.Select(MapEvent).ToList(),
            page,
            pageSize,
            totalCount,
            totalPages);
    }

    public async Task<SqlOSAuditLogEvent?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var normalizedId = NormalizeNullable(id, 64);
        if (normalizedId == null)
        {
            return null;
        }

        var entity = await _context.Set<SqlOSAuditEvent>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == normalizedId, cancellationToken);
        return entity == null ? null : MapEvent(entity);
    }

    public async Task<SqlOSAuditLogCsvExportResult> ExportCsvAsync(
        SqlOSAuditLogListRequest request,
        CancellationToken cancellationToken = default)
    {
        var from = request.OccurredAtFrom ?? DateTime.UtcNow.AddDays(-30);
        var to = request.OccurredAtTo ?? DateTime.UtcNow;
        if (to < from)
        {
            throw new InvalidOperationException("Audit log export end date must be after the start date.");
        }

        if (to - from > MaxExportRange)
        {
            throw new InvalidOperationException("Audit log export date range cannot exceed 366 days.");
        }

        var exportRequest = request with
        {
            OccurredAtFrom = from,
            OccurredAtTo = to,
            Page = 1,
            PageSize = MaxExportRows
        };

        var entities = await ApplyFilters(_context.Set<SqlOSAuditEvent>().AsNoTracking(), exportRequest)
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.IngestedAt)
            .Take(MaxExportRows)
            .ToListAsync(cancellationToken);

        var rows = entities.Select(MapEvent).ToList();
        var csv = BuildCsv(rows);
        var fileName = $"sqlos-audit-{DateTime.UtcNow:yyyyMMddHHmmss}.csv";
        return new SqlOSAuditLogCsvExportResult(fileName, csv);
    }

    private async Task<(string? ApplicationId, string? ApplicationKey)> ResolveApplicationAsync(
        string? applicationId,
        string? applicationKey,
        CancellationToken cancellationToken)
    {
        var normalizedId = NormalizeNullable(applicationId, 64);
        var normalizedKey = NormalizeNullable(applicationKey, 200);

        if (normalizedId == null && normalizedKey == null)
        {
            return (null, null);
        }

        SqlOSClientApplication? client = null;
        if (normalizedId != null)
        {
            client = await _context.Set<SqlOSClientApplication>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == normalizedId, cancellationToken);
        }

        if (client == null && normalizedKey != null)
        {
            client = await _context.Set<SqlOSClientApplication>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientId == normalizedKey || x.Id == normalizedKey, cancellationToken);
        }

        return (
            normalizedId ?? client?.Id,
            normalizedKey ?? client?.ClientId);
    }

    private static IQueryable<SqlOSAuditEvent> ApplyFilters(
        IQueryable<SqlOSAuditEvent> query,
        SqlOSAuditLogListRequest request)
    {
        var organizationId = NormalizeNullable(request.OrganizationId, 64);
        if (organizationId != null)
        {
            query = query.Where(x => x.OrganizationId == organizationId);
        }

        var applicationId = NormalizeNullable(request.ApplicationId, 64);
        if (applicationId != null)
        {
            query = query.Where(x => x.ApplicationId == applicationId);
        }

        var applicationKey = NormalizeNullable(request.ApplicationKey, 200);
        if (applicationKey != null)
        {
            query = query.Where(x => x.ApplicationKey == applicationKey);
        }

        var application = NormalizeNullable(request.Application, 200);
        if (application != null)
        {
            query = query.Where(x => x.ApplicationId == application || x.ApplicationKey == application);
        }

        var source = NormalizeNullable(request.Source, 80);
        if (source != null)
        {
            query = query.Where(x => x.Source == source);
        }

        var action = NormalizeNullable(request.Action, 160);
        if (action != null)
        {
            query = query.Where(x => x.Action == action || x.EventType == action);
        }

        var actorType = NormalizeNullable(request.ActorType, 80);
        if (actorType != null)
        {
            query = query.Where(x => x.ActorType == actorType);
        }

        var actorId = NormalizeNullable(request.ActorId, 128);
        if (actorId != null)
        {
            query = query.Where(x => x.ActorId == actorId);
        }

        var targetType = NormalizeNullable(request.TargetType, 80);
        if (targetType != null)
        {
            var targetTypeToken = $"\"type\":\"{EscapeForJsonContains(targetType)}\"";
            query = query.Where(x => x.TargetsJson.Contains(targetTypeToken));
        }

        var targetId = NormalizeNullable(request.TargetId, 128);
        if (targetId != null)
        {
            var targetIdToken = $"\"id\":\"{EscapeForJsonContains(targetId)}\"";
            query = query.Where(x => x.TargetsJson.Contains(targetIdToken));
        }

        var result = NormalizeNullable(request.Result, 80);
        if (result != null)
        {
            query = query.Where(x =>
                (x.MetadataJson != null && x.MetadataJson.Contains(result)) ||
                (x.DataJson != null && x.DataJson.Contains(result)));
        }

        var search = NormalizeNullable(request.Search, 200);
        if (search != null)
        {
            query = query.Where(x =>
                x.Action.Contains(search) ||
                x.EventType.Contains(search) ||
                x.Source.Contains(search) ||
                (x.ActorId != null && x.ActorId.Contains(search)) ||
                (x.ActorDisplayName != null && x.ActorDisplayName.Contains(search)) ||
                (x.OrganizationId != null && x.OrganizationId.Contains(search)) ||
                (x.ApplicationId != null && x.ApplicationId.Contains(search)) ||
                (x.ApplicationKey != null && x.ApplicationKey.Contains(search)) ||
                x.TargetsJson.Contains(search) ||
                (x.MetadataJson != null && x.MetadataJson.Contains(search)) ||
                (x.DataJson != null && x.DataJson.Contains(search)));
        }

        if (request.OccurredAtFrom is { } from)
        {
            query = query.Where(x => x.OccurredAt >= from.ToUniversalTime());
        }

        if (request.OccurredAtTo is { } to)
        {
            query = query.Where(x => x.OccurredAt <= to.ToUniversalTime());
        }

        return query;
    }

    private static SqlOSAuditLogEvent MapEvent(SqlOSAuditEvent entity)
    {
        var action = string.IsNullOrWhiteSpace(entity.Action) ? entity.EventType : entity.Action;
        var targets = DeserializeTargets(entity.TargetsJson);
        var context = DeserializeContext(entity.ContextJson)
            ?? BuildContextFromColumns(entity);

        return new SqlOSAuditLogEvent(
            entity.Id,
            entity.OrganizationId,
            entity.ApplicationId,
            entity.ApplicationKey,
            string.IsNullOrWhiteSpace(entity.Source) ? "authserver" : entity.Source,
            action,
            entity.EventType,
            new SqlOSAuditActor(
                string.IsNullOrWhiteSpace(entity.ActorType) ? "system" : entity.ActorType,
                entity.ActorId,
                entity.ActorDisplayName),
            targets,
            context,
            DeserializeMetadata(entity.MetadataJson) ?? DeserializeMetadata(entity.DataJson),
            entity.UserId,
            entity.SessionId,
            entity.IpAddress,
            entity.UserAgent,
            entity.RequestId,
            entity.CorrelationId,
            entity.OccurredAt,
            entity.IngestedAt == default ? entity.OccurredAt : entity.IngestedAt);
    }

    private static SqlOSAuditContext? BuildContextFromColumns(SqlOSAuditEvent entity)
    {
        if (string.IsNullOrWhiteSpace(entity.IpAddress)
            && string.IsNullOrWhiteSpace(entity.UserAgent)
            && string.IsNullOrWhiteSpace(entity.SessionId)
            && string.IsNullOrWhiteSpace(entity.RequestId)
            && string.IsNullOrWhiteSpace(entity.CorrelationId))
        {
            return null;
        }

        return new SqlOSAuditContext(
            entity.IpAddress,
            entity.UserAgent,
            entity.SessionId,
            entity.RequestId,
            entity.CorrelationId);
    }

    private static IReadOnlyList<SqlOSAuditTarget> DeserializeTargets(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SqlOSAuditTarget>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SqlOSAuditContext? DeserializeContext(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SqlOSAuditContext>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, object?>? DeserializeMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, object?>? SanitizeMetadata(
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        var element = JsonSerializer.SerializeToElement(metadata, JsonOptions);
        return SanitizeElement(element, propertyName: null) as Dictionary<string, object?>;
    }

    private static object? SanitizeElement(JsonElement element, string? propertyName)
    {
        if (ShouldRedact(propertyName))
        {
            return "[redacted]";
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => SanitizeObject(element),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => SanitizeElement(item, propertyName: null))
                .ToList(),
            JsonValueKind.String => Truncate(element.GetString(), 2048),
            JsonValueKind.Number => ReadNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static Dictionary<string, object?> SanitizeObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = SanitizeElement(property.Value, property.Name);
        }

        return result;
    }

    private static object ReadNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var int64))
        {
            return int64;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        return element.GetDouble();
    }

    private static bool ShouldRedact(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var normalized = propertyName.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        if (normalized is "access" or "refresh")
        {
            return true;
        }

        return RedactedMetadataKeyParts.Any(normalized.Contains);
    }

    private static SqlOSAuditActor NormalizeActor(SqlOSAuditActor? actor)
        => actor == null
            ? new SqlOSAuditActor("system")
            : new SqlOSAuditActor(
                NormalizeNullable(actor.Type, 80) ?? "system",
                NormalizeNullable(actor.Id, 128),
                NormalizeNullable(actor.DisplayName, 320));

    private static IReadOnlyList<SqlOSAuditTarget> NormalizeTargets(IReadOnlyList<SqlOSAuditTarget>? targets)
        => targets?
            .Where(x => !string.IsNullOrWhiteSpace(x.Type) && !string.IsNullOrWhiteSpace(x.Id))
            .Select(x => new SqlOSAuditTarget(
                NormalizeRequired(x.Type, "target.type", 80),
                NormalizeRequired(x.Id, "target.id", 128),
                NormalizeNullable(x.DisplayName, 320)))
            .ToList()
        ?? [];

    private static (int Page, int PageSize) NormalizePagination(int? page, int? pageSize)
    {
        var resolvedPage = Math.Max(1, page.GetValueOrDefault(1));
        var resolvedPageSize = Math.Clamp(pageSize.GetValueOrDefault(25), 1, MaxPageSize);
        return (resolvedPage, resolvedPageSize);
    }

    private static string NormalizeRequired(string? value, string name, int maxLength)
        => NormalizeNullable(value, maxLength)
           ?? throw new ArgumentException($"{name} is required.", name);

    private static string? NormalizeNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Truncate(value.Trim(), maxLength);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static string? HashIdempotencyKey(string? idempotencyKey)
    {
        var normalized = NormalizeNullable(idempotencyKey, 512);
        if (normalized == null)
        {
            return null;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string EscapeForJsonContains(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string BuildCsv(IReadOnlyList<SqlOSAuditLogEvent> events)
    {
        var builder = new StringBuilder();
        builder.AppendLine("id,occurred_at,ingested_at,organization_id,application_id,application_key,source,action,actor_type,actor_id,actor_display_name,target_summary,ip_address,request_id,correlation_id,metadata_json");

        foreach (var item in events)
        {
            var targetSummary = string.Join(
                "; ",
                item.Targets.Select(x => $"{x.Type}:{x.Id}{(string.IsNullOrWhiteSpace(x.DisplayName) ? string.Empty : $" ({x.DisplayName})")}"));
            var metadataJson = item.Metadata == null ? string.Empty : JsonSerializer.Serialize(item.Metadata, JsonOptions);
            var values = new[]
            {
                item.Id,
                item.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                item.IngestedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                item.OrganizationId,
                item.ApplicationId,
                item.ApplicationKey,
                item.Source,
                item.Action,
                item.Actor.Type,
                item.Actor.Id,
                item.Actor.DisplayName,
                targetSummary,
                item.IpAddress,
                item.RequestId,
                item.CorrelationId,
                metadataJson
            };

            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
