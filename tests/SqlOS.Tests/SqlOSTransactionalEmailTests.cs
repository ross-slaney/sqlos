using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Interfaces;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Configuration;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;
using SqlOS.Tests.Infrastructure;

namespace SqlOS.Tests;

[TestClass]
public sealed class SqlOSTransactionalEmailTests
{
    [TestMethod]
    public void EmailTemplateRenderer_Html_EncodesVariables()
    {
        var renderer = new SqlOSEmailTemplateRenderer();

        var result = renderer.Render(
            "Hello {name}",
            "<p>Hello {name}</p>",
            "Hello {name}",
            new Dictionary<string, object?> { ["name"] = "<strong>Alice</strong>" });

        result.HtmlBody.Should().Contain("&lt;strong&gt;Alice&lt;/strong&gt;");
        result.TextBody.Should().Contain("<strong>Alice</strong>");
    }

    [TestMethod]
    public void EmailTemplateRenderer_Text_ReplacesVariables()
    {
        var renderer = new SqlOSEmailTemplateRenderer();

        var result = renderer.Render(
            "Order {orderId}",
            "<p>Order {orderId}</p>",
            "Track {orderId} at {trackingUrl}",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            });

        result.Subject.Should().Be("Order 123");
        result.TextBody.Should().Be("Track 123 at https://tracking.example.test/123");
    }

    [TestMethod]
    public void EmailTemplateRenderer_MissingVariable_FailsValidation()
    {
        var renderer = new SqlOSEmailTemplateRenderer();

        var act = () => renderer.Render(
            "Hello {name}",
            "<p>Hello {name}</p>",
            "Hello {name}",
            new Dictionary<string, object?>());

        act.Should().Throw<SqlOSEmailTemplateValidationException>()
            .Which.MissingVariables.Should().ContainSingle("name");
    }

    [TestMethod]
    public async Task TransactionalEmail_Send_ByTemplateKey_RecordsDelivery()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender();
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-shipped");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "order-shipped",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Queued);
        result.ProviderMessageId.Should().Be("provider-1");
        sender.Messages.Should().ContainSingle();

        var delivery = await context.Set<SqlOSEmailDelivery>().SingleAsync();
        delivery.TemplateKey.Should().Be("order-shipped");
        delivery.TemplateVersion.Should().Be(1);
        delivery.RenderedSubject.Should().Be("Order 123 shipped");
        delivery.RenderedTextPreview.Should().Contain("https://tracking.example.test/123");
        delivery.RenderedHtmlPreview.Should().BeNull();

        (await context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "email.send.queued"
            && x.ActorId == delivery.Id
            && x.DataJson!.Contains(delivery.Id))).Should().BeTrue();
    }

    [TestMethod]
    public async Task TransactionalEmail_SendFailure_RecordsDeliveryAndAuditEvent()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender { ThrowOnSend = true };
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-failed");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "order-failed",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Failed);
        result.SanitizedError.Should().Be("Email delivery failed.");

        var delivery = await context.Set<SqlOSEmailDelivery>().SingleAsync();
        delivery.Status.Should().Be(SqlOSEmailDeliveryStatuses.Failed);
        delivery.SanitizedError.Should().Be("Email delivery failed.");
        delivery.SanitizedError.Should().NotContain("secret");

        (await context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "email.send.failed"
            && x.ActorId == delivery.Id
            && x.DataJson!.Contains(delivery.Id))).Should().BeTrue();
    }

    [TestMethod]
    public async Task TransactionalEmail_IdempotencyKey_PreventsDuplicateSend()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender();
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-idempotent");

        var request = new SqlOSSendEmailRequest(
            "order-idempotent",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            },
            "order-123-shipped");

        var first = await service.SendAsync(request);
        var second = await service.SendAsync(request);

        second.DeliveryId.Should().Be(first.DeliveryId);
        sender.Messages.Should().ContainSingle();
        (await context.Set<SqlOSEmailDelivery>().CountAsync()).Should().Be(1);
    }

    [TestMethod]
    public async Task TransactionalEmail_AdminApi_ListMessages()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender();
        var service = CreateEmailService(context, sender);
        var admin = CreateEmailAdmin(context);
        await AddTemplateAsync(context, "order-message-list");
        await service.SendAsync(new SqlOSSendEmailRequest(
            "order-message-list",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        var result = await admin.ListMessagesAsync(
            status: "queued",
            templateKey: "order-message-list",
            recipient: "user@example.com",
            page: 1,
            pageSize: 10);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        json.GetProperty("totalCount").GetInt32().Should().Be(1);
        var item = json.GetProperty("data")[0];
        item.GetProperty("templateKey").GetString().Should().Be("order-message-list");
        item.GetProperty("status").GetString().Should().Be(SqlOSEmailDeliveryStatuses.Queued);
        item.GetProperty("renderedTextPreview").GetString().Should().Contain("123");
    }

    [TestMethod]
    public async Task BuiltInAuthTemplates_AreSeededAutomatically()
    {
        using var context = CreateContext();
        var admin = CreateEmailAdmin(context);

        await admin.EnsureBuiltInTemplatesAsync();

        var keys = await context.Set<SqlOSEmailTemplate>()
            .Select(x => x.Key)
            .ToListAsync();
        keys.Should().Contain([
            SqlOSBuiltInEmailTemplates.AuthEmailOtpKey,
            SqlOSBuiltInEmailTemplates.AuthInvitationKey,
            SqlOSBuiltInEmailTemplates.AuthPasswordResetKey
        ]);
    }

    [TestMethod]
    public async Task AuthEmailOtpAndInvitation_UseBuiltInTransactionalTemplates_WhenConfigured()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
        authOptions.SeedAuthPage(page => page.EnabledCredentialTypes = ["email_otp"]);
        var options = Options.Create(authOptions);
        var authEmailSender = new TestAuthEmailSender { IsConfigured = false };
        var transactionalSender = new FakeTransactionalEmailSender();
        var transactionalService = CreateEmailService(context, transactionalSender);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, authEmailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, authEmailSender, options, transactionalService);
        var invitationService = new SqlOSInvitationService(context, admin, crypto, authEmailSender, settings, options, transactionalService);
        await CreateEmailAdmin(context).EnsureBuiltInTemplatesAsync();

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();
        await settings.UpsertSeededAuthPageSettingsAsync();
        await settings.UpsertSeededAuthEmailSettingsAsync();

        await admin.CreateUserAsync(new SqlOSCreateUserRequest("Alice", "alice@example.com", "P@ssword123!"));
        await emailOtp.StartForClientAsync(new SqlOSEmailOtpStartRequest("alice@example.com", "test-client", null));

        var org = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest("Email Template Org", null));
        await invitationService.CreateEmailInvitationAsync(new SqlOSCreateEmailInvitationRequest(
            org.Id,
            "invitee@example.com",
            "member"));

        authEmailSender.Messages.Should().BeEmpty();
        transactionalSender.Messages.Should().HaveCount(2);
        transactionalSender.Messages[0].Subject.Should().Contain("sign-in code");
        transactionalSender.Messages[1].Subject.Should().Be("You're invited to Email Template Org");

        var deliveries = await context.Set<SqlOSEmailDelivery>()
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();
        deliveries.Should().HaveCount(2);
        deliveries.Select(x => x.TemplateKey).Should().Contain([
            SqlOSBuiltInEmailTemplates.AuthEmailOtpKey,
            SqlOSBuiltInEmailTemplates.AuthInvitationKey
        ]);
        deliveries.Should().OnlyContain(x => x.RenderedTextPreview == "[suppressed for sensitive built-in template]");
    }

    [TestMethod]
    public async Task PasswordResetEmail_UsesBuiltInTransactionalTemplate()
    {
        using var context = CreateContext();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.SeedBrowserClient("test-client", "Test Client", "https://client.example.test/callback");
        var options = Options.Create(authOptions);
        var authEmailSender = new TestAuthEmailSender { IsConfigured = false };
        var transactionalSender = new FakeTransactionalEmailSender();
        var transactionalService = CreateEmailService(context, transactionalSender);
        var crypto = new SqlOSCryptoService(context, options);
        var admin = new SqlOSAdminService(context, options, crypto);
        var settings = new SqlOSSettingsService(context, options, authEmailSender);
        var emailOtp = new SqlOSEmailOtpService(context, admin, crypto, settings, authEmailSender, options, transactionalService);
        var auth = new SqlOSAuthService(context, options, admin, crypto, settings, emailOtp, transactionalEmailService: transactionalService);
        await CreateEmailAdmin(context).EnsureBuiltInTemplatesAsync();

        await crypto.EnsureActiveSigningKeyAsync();
        await admin.UpsertSeededClientsAsync();
        await admin.CreateUserAsync(new SqlOSCreateUserRequest("Reset User", "reset@example.com", "OldPassword123!"));

        var result = await auth.SendPasswordResetEmailAsync(new SqlOSSendPasswordResetEmailRequest("reset@example.com"));

        result.DeliveryStatus.Should().Be(SqlOSEmailDeliveryStatuses.Queued);
        transactionalSender.Messages.Should().ContainSingle();
        transactionalSender.Messages[0].Subject.Should().Be("Reset your SqlOS password");
        transactionalSender.Messages[0].TextBody.Should().Contain("/sqlos/auth/password/reset?token=");

        var delivery = await context.Set<SqlOSEmailDelivery>().SingleAsync();
        delivery.TemplateKey.Should().Be(SqlOSBuiltInEmailTemplates.AuthPasswordResetKey);
        delivery.RenderedTextPreview.Should().Be("[suppressed for sensitive built-in template]");
    }

    private static TestSqlOSInMemoryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestSqlOSInMemoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestSqlOSInMemoryDbContext(options);
    }

    private static SqlOSTransactionalEmailService CreateEmailService(
        TestSqlOSInMemoryDbContext context,
        FakeTransactionalEmailSender sender)
        => new(
            context,
            new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions())),
            sender,
            new SqlOSEmailTemplateRenderer(),
            Options.Create(new SqlOSEmailOptions()));

    private static SqlOSEmailAdminService CreateEmailAdmin(TestSqlOSInMemoryDbContext context)
        => new(
            context,
            new SqlOSCryptoService(context, Options.Create(new SqlOSAuthServerOptions())),
            new SqlOSEmailTemplateRenderer());

    private static async Task AddTemplateAsync(TestSqlOSInMemoryDbContext context, string key)
    {
        context.Set<SqlOSEmailTemplate>().Add(new SqlOSEmailTemplate
        {
            Id = $"emt_{Guid.NewGuid():N}",
            Key = key,
            DisplayName = "Order shipped",
            SubjectTemplate = "Order {orderId} shipped",
            HtmlBodyTemplate = "<p>Order {orderId} ships to <a href=\"{trackingUrl}\">tracking</a></p>",
            TextBodyTemplate = "Order {orderId} tracking: {trackingUrl}",
            VariablesJson = "{\"orderId\":{\"description\":\"Order id\"},\"trackingUrl\":{\"description\":\"Tracking URL\"}}",
            IsActive = true,
            Version = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private sealed class FakeTransactionalEmailSender : ISqlOSEmailSender
    {
        public bool IsConfigured { get; set; } = true;
        public bool ThrowOnSend { get; set; }
        public List<SqlOSEmailMessage> Messages { get; } = [];

        public Task<SqlOSEmailProviderResult> SendAsync(
            SqlOSEmailMessage message,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("Provider secret leaked in raw exception.");
            }

            Messages.Add(message);
            return Task.FromResult(new SqlOSEmailProviderResult($"provider-{Messages.Count}"));
        }
    }
}
