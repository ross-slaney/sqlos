using Azure;
using Azure.Communication.Email;
using Azure.Core.Pipeline;
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
        sender.Messages[0].Headers.Should().BeNull();

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
        var sender = new FakeTransactionalEmailSender
        {
            ExceptionOnSend = new InvalidOperationException("Provider rejected request with accesskey=secret-value")
        };
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
        result.SanitizedError.Should().Be("Email delivery failed: InvalidOperationException: Provider rejected request with accesskey=[redacted]");

        var delivery = await context.Set<SqlOSEmailDelivery>().SingleAsync();
        delivery.Status.Should().Be(SqlOSEmailDeliveryStatuses.Failed);
        delivery.SanitizedError.Should().Be("Email delivery failed: InvalidOperationException: Provider rejected request with accesskey=[redacted]");
        delivery.SanitizedError.Should().NotContain("secret-value");

        (await context.Set<SqlOSAuditEvent>().AnyAsync(x =>
            x.EventType == "email.send.failed"
            && x.ActorId == delivery.Id
            && x.DataJson!.Contains(delivery.Id))).Should().BeTrue();
    }

    [TestMethod]
    public async Task TransactionalEmail_AzureSendFailure_RecordsProviderReason()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender
        {
            ExceptionOnSend = new RequestFailedException(
                400,
                "The specified sender domain is not linked to this Communication Services resource.",
                "BadRequest",
                null)
        };
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-azure-failed");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "order-azure-failed",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Failed);
        result.SanitizedError.Should().Contain("Azure Communication Email send failed");
        result.SanitizedError.Should().Contain("Status 400");
        result.SanitizedError.Should().Contain("ErrorCode BadRequest");
        result.SanitizedError.Should().Contain("sender domain is not linked");
    }

    [TestMethod]
    public async Task TransactionalEmail_WrappedAzureSendFailure_RecordsProviderReason()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender
        {
            ExceptionOnSend = new InvalidOperationException(
                "Azure email client failed.",
                new RequestFailedException(
                    401,
                    "Authentication failed for accesskey=secret-value.",
                    "Denied",
                    null))
        };
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-wrapped-azure-failed");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "order-wrapped-azure-failed",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Failed);
        result.SanitizedError.Should().Contain("Azure Communication Email send failed");
        result.SanitizedError.Should().Contain("Status 401");
        result.SanitizedError.Should().Contain("ErrorCode Denied");
        result.SanitizedError.Should().Contain("accesskey=[redacted]");
        result.SanitizedError.Should().NotContain("secret-value");
    }

    [TestMethod]
    public async Task AcsEmailSender_InvalidAccessKey_FailsWithActionableConfigurationError()
    {
        var sender = new SqlOSAcsEmailSender(
            Options.Create(new SqlOSEmailOptions
            {
                AzureCommunicationServicesConnectionString = "endpoint=https://example.unitedstates.communication.azure.com/;accesskey=not-base64",
                FromAddress = "hello@example.com"
            }));

        sender.IsConfigured.Should().BeTrue();

        var act = async () => await sender.SendAsync(new SqlOSEmailMessage(
            "user@example.com",
            "Subject",
            "<p>Body</p>",
            "Body"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*accesskey must be a valid base64 value*primaryConnectionString*");
    }

    [TestMethod]
    public async Task DefaultEmailSender_UsesLegacyAuthEmailSender_WhenAcsIsNotConfigured()
    {
        var legacySender = new LegacyAuthEmailSender { IsConfigured = true };
        var sender = new SqlOSDefaultEmailSender(
            new SqlOSAcsEmailSender(Options.Create(new SqlOSEmailOptions())),
            legacySender);

        sender.IsConfigured.Should().BeTrue();

        var headers = new Dictionary<string, string>
        {
            ["List-Unsubscribe"] = "<https://app.example.test/unsubscribe/one-click?token=legacy>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
        };
        var result = await sender.SendAsync(new SqlOSEmailMessage(
            "user@example.com",
            "Template subject",
            "<p>Template body</p>",
            "Template body",
            headers));

        result.ProviderMessageId.Should().BeNull();
        legacySender.Messages.Should().ContainSingle();
        legacySender.Messages[0].To.Should().Be("user@example.com");
        legacySender.Messages[0].Subject.Should().Be("Template subject");
        legacySender.Messages[0].HtmlBody.Should().Be("<p>Template body</p>");
        legacySender.Messages[0].TextBody.Should().Be("Template body");
        legacySender.Messages[0].Headers.Should().Equal(headers);
    }

    [TestMethod]
    public async Task TransactionalEmail_ListUnsubscribe_SendsOneClickHeadersToAzure()
    {
        using var context = CreateContext();
        var acs = new CapturingAcsHandler();
        var sender = new SqlOSDefaultEmailSender(
            new SqlOSAcsEmailSender(
                Options.Create(new SqlOSEmailOptions
                {
                    AzureCommunicationServicesConnectionString = AcsTestConnectionString,
                    FromAddress = "news@example.test"
                }),
                authOptions: null,
                acs.CreateClientOptions()),
            new LegacyAuthEmailSender());
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "weekly-digest");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "weekly-digest",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            },
            ListUnsubscribe: new SqlOSEmailListUnsubscribe(
                OneClickUrl: new Uri("https://app.example.test/unsubscribe/one-click?token=secret-unsubscribe-token"),
                MailTo: new Uri("mailto:unsubscribe@example.test?subject=unsubscribe"))));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Queued);
        var request = acs.Requests.Should().ContainSingle().Subject;
        request.GetProperty("senderAddress").GetString().Should().Be("news@example.test");
        var headers = request.GetProperty("headers");
        headers.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(
            ["List-Unsubscribe", "List-Unsubscribe-Post"]);
        headers.GetProperty("List-Unsubscribe").GetString().Should().Be(
            "<https://app.example.test/unsubscribe/one-click?token=secret-unsubscribe-token>, <mailto:unsubscribe@example.test?subject=unsubscribe>");
        headers.GetProperty("List-Unsubscribe-Post").GetString().Should().Be("List-Unsubscribe=One-Click");

        var delivery = await context.Set<SqlOSEmailDelivery>().AsNoTracking().SingleAsync();
        System.Text.Json.JsonSerializer.Serialize(delivery).Should().NotContain("secret-unsubscribe-token");
        (await context.Set<SqlOSAuditEvent>().Select(x => x.DataJson).ToListAsync())
            .Should().NotContain(x => x != null && x.Contains("secret-unsubscribe-token"));
    }

    [TestMethod]
    public async Task TransactionalEmail_WithoutListUnsubscribe_SendsNoHeadersToAzure()
    {
        using var context = CreateContext();
        var acs = new CapturingAcsHandler();
        var sender = new SqlOSAcsEmailSender(
            Options.Create(new SqlOSEmailOptions
            {
                AzureCommunicationServicesConnectionString = AcsTestConnectionString,
                FromAddress = "news@example.test"
            }),
            authOptions: null,
            acs.CreateClientOptions());
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-no-headers");

        var result = await service.SendAsync(new SqlOSSendEmailRequest(
            "order-no-headers",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            }));

        result.Status.Should().Be(SqlOSEmailDeliveryStatuses.Queued);
        acs.Requests.Should().ContainSingle()
            .Which.TryGetProperty("headers", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task TransactionalEmail_ListUnsubscribe_MailtoOnly_OmitsOneClickPost()
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender();
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-mailto-unsubscribe");

        await service.SendAsync(new SqlOSSendEmailRequest(
            "order-mailto-unsubscribe",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            },
            ListUnsubscribe: new SqlOSEmailListUnsubscribe(
                MailTo: new Uri("mailto:unsubscribe@example.test"))));

        sender.Messages.Should().ContainSingle()
            .Which.Headers.Should().Equal(new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = "<mailto:unsubscribe@example.test>"
            });
    }

    [TestMethod]
    [DataRow(null, null, "*one-click HTTPS URL, a mailto URI, or both*")]
    [DataRow("http://app.example.test/unsubscribe", null, "*one-click URL must be an absolute https URI*")]
    [DataRow("unsubscribe?token=abc", null, "*one-click URL must be an absolute https URI*")]
    [DataRow("mailto:unsubscribe@example.test", null, "*one-click URL must be an absolute https URI*")]
    [DataRow("https://user:password@app.example.test/unsubscribe", null, "*one-click URL must not contain user info*")]
    [DataRow("https://bücher.example.test/unsubscribe", null, "*one-click URL must be printable ASCII*")]
    [DataRow(null, "https://app.example.test/unsubscribe", "*mailto URI must be an absolute mailto URI*")]
    public async Task TransactionalEmail_InvalidListUnsubscribe_FailsBeforeDelivery(
        string? oneClickUrl,
        string? mailTo,
        string expectedMessage)
    {
        using var context = CreateContext();
        var sender = new FakeTransactionalEmailSender();
        var service = CreateEmailService(context, sender);
        await AddTemplateAsync(context, "order-invalid-unsubscribe");

        var act = async () => await service.SendAsync(new SqlOSSendEmailRequest(
            "order-invalid-unsubscribe",
            "user@example.com",
            new Dictionary<string, object?>
            {
                ["orderId"] = "123",
                ["trackingUrl"] = "https://tracking.example.test/123"
            },
            ListUnsubscribe: new SqlOSEmailListUnsubscribe(
                oneClickUrl == null ? null : new Uri(oneClickUrl, UriKind.RelativeOrAbsolute),
                mailTo == null ? null : new Uri(mailTo, UriKind.RelativeOrAbsolute))));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expectedMessage);
        sender.Messages.Should().BeEmpty();
        (await context.Set<SqlOSEmailDelivery>().AnyAsync()).Should().BeFalse();
    }

    [TestMethod]
    public async Task DefaultEmailSender_AcsAuthFallback_SendsHeadersToAzure()
    {
        var acs = new CapturingAcsHandler();
        var authOptions = new SqlOSAuthServerOptions();
        authOptions.ConfigureEmailOtp(email =>
        {
            email.AzureCommunicationServicesConnectionString = AcsTestConnectionString;
            email.FromAddress = "auth@example.test";
        });
        var sender = new SqlOSDefaultEmailSender(
            new SqlOSAcsEmailSender(Options.Create(new SqlOSEmailOptions())),
            new SqlOSAcsAuthEmailSender(Options.Create(authOptions), acs.CreateClientOptions()));

        await sender.SendAsync(new SqlOSEmailMessage(
            "user@example.com",
            "Template subject",
            "<p>Template body</p>",
            "Template body",
            new Dictionary<string, string>
            {
                ["List-Unsubscribe"] = "<https://app.example.test/unsubscribe/one-click?token=auth-fallback>",
                ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click"
            }));

        var request = acs.Requests.Should().ContainSingle().Subject;
        request.GetProperty("senderAddress").GetString().Should().Be("auth@example.test");
        var headers = request.GetProperty("headers");
        headers.GetProperty("List-Unsubscribe").GetString().Should().Be(
            "<https://app.example.test/unsubscribe/one-click?token=auth-fallback>");
        headers.GetProperty("List-Unsubscribe-Post").GetString().Should().Be("List-Unsubscribe=One-Click");
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

        json.GetProperty("data").GetArrayLength().Should().Be(1);
        json.GetProperty("hasNextPage").GetBoolean().Should().BeFalse();
        json.TryGetProperty("totalCount", out _).Should().BeFalse();
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
            SqlOSBuiltInEmailTemplates.AuthEmailVerificationKey,
            SqlOSBuiltInEmailTemplates.AuthInvitationKey,
            SqlOSBuiltInEmailTemplates.AuthMagicLinkKey,
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
        var crypto = TestCryptoService.Create(context, options);
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
        var crypto = TestCryptoService.Create(context, options);
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

    private static readonly string AcsTestConnectionString =
        $"endpoint=https://example.unitedstates.communication.azure.com/;accesskey={Convert.ToBase64String(new byte[32])}";

    private static SqlOSTransactionalEmailService CreateEmailService(
        TestSqlOSInMemoryDbContext context,
        ISqlOSEmailSender sender)
        => new(
            context,
            TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions())),
            sender,
            new SqlOSEmailTemplateRenderer(),
            Options.Create(new SqlOSEmailOptions()));

    private static SqlOSEmailAdminService CreateEmailAdmin(TestSqlOSInMemoryDbContext context)
        => new(
            context,
            TestCryptoService.Create(context, Options.Create(new SqlOSAuthServerOptions())),
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
        public Exception? ExceptionOnSend { get; set; }
        public List<SqlOSEmailMessage> Messages { get; } = [];

        public Task<SqlOSEmailProviderResult> SendAsync(
            SqlOSEmailMessage message,
            CancellationToken cancellationToken = default)
        {
            if (ExceptionOnSend != null)
            {
                throw ExceptionOnSend;
            }

            if (ThrowOnSend)
            {
                throw new InvalidOperationException("Provider secret leaked in raw exception.");
            }

            Messages.Add(message);
            return Task.FromResult(new SqlOSEmailProviderResult($"provider-{Messages.Count}"));
        }
    }

    /// <summary>Captures the JSON body the Azure Communication Services SDK sends and accepts the send.</summary>
    private sealed class CapturingAcsHandler : HttpMessageHandler
    {
        public List<System.Text.Json.JsonElement> Requests { get; } = [];

        public EmailClientOptions CreateClientOptions()
        {
            var options = new EmailClientOptions
            {
                Transport = new HttpClientTransport(this)
            };
            options.Retry.MaxRetries = 0;
            return options;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.Should().Be("/emails:send");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(System.Text.Json.JsonDocument.Parse(body).RootElement.Clone());

            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
            {
                Content = new StreamContent(new NetworkLikeStream(System.Text.Encoding.UTF8.GetBytes(
                    "{\"id\":\"acs-operation-1\",\"status\":\"Running\"}")))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            response.Headers.Add(
                "Operation-Location",
                "https://example.unitedstates.communication.azure.com/emails/operations/acs-operation-1?api-version=2023-03-31");
            return response;
        }
    }

    /// <summary>
    /// Non-seekable like a socket stream, so Azure.Core buffers it the way it buffers a real ACS response.
    /// </summary>
    private sealed class NetworkLikeStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LegacyAuthEmailSender : ISqlOSAuthEmailSender
    {
        public bool IsConfigured { get; set; }
        public List<SqlOSAuthEmailMessage> Messages { get; } = [];

        public Task SendAsync(
            SqlOSAuthEmailMessage message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
