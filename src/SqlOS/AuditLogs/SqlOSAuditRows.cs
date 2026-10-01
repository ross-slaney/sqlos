using System.Text.Json;
using SqlOS.AuthServer.Models;

namespace SqlOS.AuditLogs;

/// <summary>
/// Builds <see cref="SqlOSAuditEvent"/> rows exactly as SqlOS 7.x writes them.
/// </summary>
/// <remarks>
/// <see cref="SqlOSAuditLogService.RecordAsync"/> and <c>SqlOSAdminService.RecordAuditAsync</c> build
/// their rows here, and so does <see cref="SqlOSAuditProjection"/>, so a row projected from a domain
/// event and the row the 7.x call wrote for the same action are the same row by construction: the
/// same normalization, truncation, metadata redaction and JSON. The parts of
/// <see cref="SqlOSAuditLogService.RecordAsync"/> that read the database (resolving an application
/// and the idempotency lookup) stay in the service; a projected row has no idempotency key, and its
/// builder passes any application it names already resolved.
/// </remarks>
internal static class SqlOSAuditRows
{
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

    /// <summary>
    /// The request <c>SqlOSAdminService.RecordAuditAsync</c> records: source <c>authserver</c>, the
    /// actor, the IP address and session as context, and <paramref name="data"/> as metadata (its
    /// member names as declared, its values through web JSON).
    /// </summary>
    public static SqlOSAuditLogRecordRequest AuthServerRequest(
        string eventType,
        string actorType,
        string? actorId,
        string? userId = null,
        string? organizationId = null,
        string? sessionId = null,
        string? ipAddress = null,
        object? data = null)
    {
        IReadOnlyDictionary<string, object?>? metadata = data == null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(data), JsonOptions);

        return new SqlOSAuditLogRecordRequest(
            Action: eventType,
            OrganizationId: organizationId,
            UserId: userId,
            Source: "authserver",
            Actor: new SqlOSAuditActor(actorType, actorId),
            Context: new SqlOSAuditContext(
                IpAddress: ipAddress,
                SessionId: sessionId),
            Metadata: metadata);
    }

    /// <summary>
    /// The row <see cref="SqlOSAuditLogService.RecordAsync"/> writes for <paramref name="request"/>,
    /// ingested at <paramref name="now"/>.
    /// </summary>
    public static SqlOSAuditEvent Create(
        SqlOSAuditLogRecordRequest request,
        string id,
        DateTime now,
        string? applicationId = null,
        string? applicationKey = null,
        string? idempotencyScopeHash = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var action = NormalizeRequired(request.Action, nameof(request.Action), 160);
        var source = NormalizeNullable(request.Source, 80) ?? "application";
        var actor = NormalizeActor(request.Actor);
        var targets = NormalizeTargets(request.Targets);
        var sanitizedMetadata = SanitizeMetadata(request.Metadata);
        var metadataJson = sanitizedMetadata == null
            ? null
            : JsonSerializer.Serialize(sanitizedMetadata, JsonOptions);
        var contextJson = request.Context == null
            ? null
            : JsonSerializer.Serialize(request.Context, JsonOptions);

        return new SqlOSAuditEvent
        {
            Id = id,
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
            OccurredAt = request.OccurredAt?.ToUniversalTime() ?? now,
            IngestedAt = now,
            IpAddress = NormalizeNullable(request.Context?.IpAddress, 128),
            UserAgent = NormalizeNullable(request.Context?.UserAgent, 512),
            RequestId = NormalizeNullable(request.Context?.RequestId, 128),
            CorrelationId = NormalizeNullable(request.Context?.CorrelationId, 128),
            IdempotencyScopeHash = idempotencyScopeHash
        };
    }

    internal static string NormalizeRequired(string? value, string name, int maxLength)
        => NormalizeNullable(value, maxLength)
           ?? throw new ArgumentException($"{name} is required.", name);

    internal static string? NormalizeNullable(string? value, int maxLength)
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
}
