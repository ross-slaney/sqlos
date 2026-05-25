using System.Text.Json;
using Azure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;

namespace SqlOS.Email.Services;

public sealed class SqlOSTransactionalEmailService : ISqlOSTransactionalEmailService
{
    private readonly ISqlOSAuthServerDbContext _context;
    private readonly SqlOSCryptoService _cryptoService;
    private readonly ISqlOSEmailSender _sender;
    private readonly SqlOSEmailTemplateRenderer _renderer;
    private readonly SqlOSEmailOptions _options;
    private readonly ILogger<SqlOSTransactionalEmailService>? _logger;

    public SqlOSTransactionalEmailService(
        ISqlOSAuthServerDbContext context,
        SqlOSCryptoService cryptoService,
        ISqlOSEmailSender sender,
        SqlOSEmailTemplateRenderer renderer,
        IOptions<SqlOSEmailOptions> options,
        ILogger<SqlOSTransactionalEmailService>? logger = null)
    {
        _context = context;
        _cryptoService = cryptoService;
        _sender = sender;
        _renderer = renderer;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SqlOSRenderedEmailPreview> PreviewAsync(
        string templateKey,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken = default)
    {
        var template = await GetActiveTemplateAsync(templateKey, cancellationToken);
        return _renderer.Render(template, variables);
    }

    public async Task<SqlOSSendEmailResult> SendAsync(
        SqlOSSendEmailRequest request,
        CancellationToken cancellationToken = default)
    {
        var templateKey = NormalizeTemplateKey(request.TemplateKey);
        var recipient = NormalizeRecipient(request.To);
        var idempotencyKey = NormalizeIdempotencyKey(request.IdempotencyKey);

        if (_options.EnableIdempotency && idempotencyKey != null)
        {
            var existing = await _context.Set<SqlOSEmailDelivery>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
            if (existing != null)
            {
                return ToSendResult(existing);
            }
        }

        var template = await GetActiveTemplateAsync(templateKey, cancellationToken);
        var rendered = _renderer.Render(template, request.Variables);
        var now = DateTime.UtcNow;
        var delivery = new SqlOSEmailDelivery
        {
            Id = _cryptoService.GenerateId("edl"),
            TemplateId = template.Id,
            TemplateKey = template.Key,
            TemplateVersion = template.Version,
            To = recipient,
            Status = SqlOSEmailDeliveryStatuses.Pending,
            RenderedSubject = TrimTo(rendered.Subject, 500),
            RenderedTextPreview = SqlOSBuiltInEmailTemplates.SuppressesRenderedContentStorage(template.Key)
                ? "[suppressed for sensitive built-in template]"
                : TrimTo(rendered.TextBody, _options.RenderedTextPreviewMaxLength),
            RenderedHtmlPreview = _options.PersistRenderedHtmlPreview
                && !SqlOSBuiltInEmailTemplates.SuppressesRenderedContentStorage(template.Key)
                    ? rendered.HtmlBody
                    : null,
            IdempotencyKey = _options.EnableIdempotency ? idempotencyKey : null,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.Set<SqlOSEmailDelivery>().Add(delivery);
        await _context.SaveChangesAsync(cancellationToken);

        if (!_sender.IsConfigured)
        {
            return await MarkFailedAsync(
                delivery,
                "Transactional email delivery is not configured.",
                cancellationToken);
        }

        try
        {
            var providerResult = await _sender.SendAsync(
                new SqlOSEmailMessage(recipient, rendered.Subject, rendered.HtmlBody, rendered.TextBody),
                cancellationToken);

            delivery.Status = SqlOSEmailDeliveryStatuses.Queued;
            delivery.ProviderMessageId = providerResult.ProviderMessageId;
            delivery.SentAt = DateTime.UtcNow;
            delivery.UpdatedAt = delivery.SentAt.Value;
            AddAuditEvent(
                "email.send.queued",
                "email_delivery",
                delivery.Id,
                new
                {
                    deliveryId = delivery.Id,
                    templateKey = delivery.TemplateKey,
                    templateVersion = delivery.TemplateVersion,
                    providerMessageId = delivery.ProviderMessageId
                });
            await _context.SaveChangesAsync(cancellationToken);
            return ToSendResult(delivery);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var sanitizedError = BuildSanitizedProviderError(ex);
            _logger?.LogWarning(
                ex,
                "Transactional email delivery failed for delivery {DeliveryId}, template {TemplateKey}, recipient {Recipient}: {SanitizedError}",
                delivery.Id,
                delivery.TemplateKey,
                recipient,
                sanitizedError);
            return await MarkFailedAsync(delivery, sanitizedError, cancellationToken);
        }
    }

    private async Task<SqlOSEmailTemplate> GetActiveTemplateAsync(
        string templateKey,
        CancellationToken cancellationToken)
        => await _context.Set<SqlOSEmailTemplate>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == NormalizeTemplateKey(templateKey) && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException($"Email template '{templateKey}' was not found or is inactive.");

    private async Task<SqlOSSendEmailResult> MarkFailedAsync(
        SqlOSEmailDelivery delivery,
        string sanitizedError,
        CancellationToken cancellationToken)
    {
        delivery.Status = SqlOSEmailDeliveryStatuses.Failed;
        delivery.SanitizedError = TrimTo(sanitizedError, 500);
        delivery.FailedAt = DateTime.UtcNow;
        delivery.UpdatedAt = delivery.FailedAt.Value;
        AddAuditEvent(
            "email.send.failed",
            "email_delivery",
            delivery.Id,
            new
            {
                deliveryId = delivery.Id,
                templateKey = delivery.TemplateKey,
                templateVersion = delivery.TemplateVersion,
                error = delivery.SanitizedError
            });
        await _context.SaveChangesAsync(cancellationToken);
        return ToSendResult(delivery);
    }

    private void AddAuditEvent(string eventType, string actorType, string actorId, object data)
    {
        _context.Set<SqlOSAuditEvent>().Add(new SqlOSAuditEvent
        {
            Id = _cryptoService.GenerateId("evt"),
            EventType = eventType,
            ActorType = actorType,
            ActorId = actorId,
            OccurredAt = DateTime.UtcNow,
            DataJson = JsonSerializer.Serialize(data)
        });
    }

    private static SqlOSSendEmailResult ToSendResult(SqlOSEmailDelivery delivery)
        => new(
            delivery.Id,
            delivery.Status,
            delivery.TemplateKey,
            delivery.TemplateVersion,
            delivery.ProviderMessageId,
            delivery.SanitizedError);

    internal static string NormalizeTemplateKey(string key)
    {
        var normalized = key?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Email template key is required.");
        }

        return normalized;
    }

    internal static string NormalizeRecipient(string to)
    {
        var normalized = to?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Email recipient is required.");
        }

        return normalized;
    }

    internal static string? NormalizeIdempotencyKey(string? idempotencyKey)
        => string.IsNullOrWhiteSpace(idempotencyKey) ? null : TrimTo(idempotencyKey.Trim(), 200);

    internal static string TrimTo(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string BuildSanitizedProviderError(Exception exception)
    {
        if (exception is RequestFailedException requestFailedException)
        {
            var message = NormalizeProviderMessage(requestFailedException.Message);
            var status = requestFailedException.Status > 0
                ? $"Status {requestFailedException.Status}"
                : "Status unknown";
            var errorCode = string.IsNullOrWhiteSpace(requestFailedException.ErrorCode)
                ? null
                : $"ErrorCode {requestFailedException.ErrorCode}";

            return TrimTo(
                string.Join(
                    ": ",
                    new[]
                    {
                        "Azure Communication Email send failed",
                        status,
                        errorCode,
                        string.IsNullOrWhiteSpace(message) ? null : message
                    }.Where(static value => !string.IsNullOrWhiteSpace(value))),
                500);
        }

        return "Email delivery failed. See server logs for provider details.";
    }

    private static string NormalizeProviderMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
