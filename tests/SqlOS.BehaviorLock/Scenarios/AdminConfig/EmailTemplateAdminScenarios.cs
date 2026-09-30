using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.BehaviorLock.Host.Profiles;
using SqlOS.BehaviorLock.Infrastructure.Scenarios;

namespace SqlOS.BehaviorLock.Scenarios.AdminConfig;

/// <summary>
/// The transactional email admin API (<c>/sqlos/admin/email/api</c>) and its dashboard page:
/// template create, read by ID or key, preview, versioned edits, delete versus deactivate, the
/// delivery log with its filters, the validation branches, and the runtime effect of editing a
/// built-in auth template. Deliveries come from the documented
/// <c>ISqlOSTransactionalEmailService.SendAsync</c> (the email probe) and SqlOS's own auth emails.
/// </summary>
[TestClass]
public sealed class EmailTemplateAdminScenarios
{
    private const string TemplatesRoute = "/sqlos/admin/email/api/templates";
    private const string MessagesRoute = "/sqlos/admin/email/api/messages";
    private const string SendProbe = "/__probe/email/send";

    [Scenario]
    [Covers("GET /sqlos/admin/email/api/templates")]
    [Covers("POST /sqlos/admin/email/api/templates")]
    [Covers("GET /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("POST /sqlos/admin/email/api/templates/{templateId}/preview")]
    [Covers("PUT /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("DELETE /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("GET /sqlos/admin/email/{*page}")]
    public async Task Operator_creates_previews_edits_and_deletes_an_email_template()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);

        t.Observe(
            await t.Operator.GetAsync(TemplatesRoute, options => options.WithoutCredentials()),
            "without operator credentials the template list is not found");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}?pageSize=100"), "startup seeded the built-in auth templates");

        var created = t.Observe(
            await t.Operator.PostJsonAsync(TemplatesRoute, new
            {
                key = " billing.receipt ",
                displayName = " ",
                subjectTemplate = " Your {product} receipt ",
                htmlBodyTemplate = "<p>Hi {name}, you paid {amount}.</p>",
                textBodyTemplate = "Hi {name}, you paid {amount}.",
                variables = new { name = "Ada", amount = "$10", product = "Pro" }
            }),
            "create a template: a blank display name falls back to the key");
        var templateId = created.JsonString("id");

        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}/{templateId}"), "read it by ID");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}/billing.receipt"), "or by key");
        t.Observe(
            await t.Operator.PostJsonAsync($"{TemplatesRoute}/{templateId}/preview", new
            {
                variables = new { name = "<b>Ada</b>", amount = 10.5, product = "Pro", unused = true }
            }),
            "preview it: values are HTML-encoded in the HTML body only, and numbers render invariantly");
        t.Observe(
            await t.Operator.PostJsonAsync($"{TemplatesRoute}/{templateId}/preview", new { variables = new { name = "Ada" } }),
            "a preview without every variable lists the missing ones");
        t.Observe(
            await t.Operator.PutJsonAsync($"{TemplatesRoute}/{templateId}", new
            {
                key = "billing.receipt",
                displayName = "Billing receipt",
                subjectTemplate = "Your {product} receipt",
                htmlBodyTemplate = "<p>Hi {name}, you paid {amount}.</p>",
                textBodyTemplate = "Hi {name}, you paid {amount}.",
                variables = new { name = "Ada", amount = "$10", product = "Pro" }
            }),
            "renaming only the display name keeps the version");
        t.Observe(
            await t.Operator.PutJsonAsync($"{TemplatesRoute}/{templateId}", new
            {
                key = "billing.receipt",
                displayName = "Billing receipt",
                subjectTemplate = "Receipt for {product}",
                htmlBodyTemplate = "<p>Thanks {name}: {amount}.</p>",
                textBodyTemplate = "Thanks {name}: {amount}.",
                isActive = false
            }),
            "changing the content bumps the version; the template can be switched off at the same time");
        t.Observe(await t.Operator.DeleteAsync($"{TemplatesRoute}/{templateId}"), "a template that never sent is deleted");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}/{templateId}"), "and can no longer be read");

        t.Observe(await t.Operator.GetAsync("/sqlos/admin/email/templates"), "the dashboard's email page is the dashboard shell");

        await t.ObserveAuditAsync("template changes are audited as template events");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("GET /sqlos/admin/email/api/messages")]
    [Covers("DELETE /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("GET /sqlos/admin/email/api/templates")]
    [Covers("POST /sqlos/admin/email/api/templates/{templateId}/preview")]
    public async Task A_template_that_has_sent_mail_is_deactivated_instead_of_deleted()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var host = t.NewClient("host");
        var ada = t.Unique.Email("ada");
        var grace = t.Unique.Email("grace");
        await t.Setup.OperatorPostAsync(TemplatesRoute, new
        {
            key = "team.invite",
            displayName = "Team invite",
            subjectTemplate = "Join {team}",
            htmlBodyTemplate = "<p>{inviter} invited you to {team}.</p>",
            textBodyTemplate = "{inviter} invited you to {team}."
        });

        t.Observe(
            await host.PostJsonAsync(SendProbe, new
            {
                templateKey = "team.invite",
                to = ada,
                variables = new { team = "Research", inviter = "Grace" },
                idempotencyKey = "invite-ada"
            }),
            "the host sends an invitation through the transactional email service");
        t.Observe(
            await host.PostJsonAsync(SendProbe, new
            {
                templateKey = "team.invite",
                to = ada,
                variables = new { team = "Research", inviter = "Grace" },
                idempotencyKey = "invite-ada"
            }),
            "resending with the same idempotency key returns the first delivery and sends nothing");
        t.Observe(
            await host.PostJsonAsync(SendProbe, new
            {
                templateKey = "team.invite",
                to = grace,
                variables = new { team = "Research", inviter = "Ada" }
            }),
            "a second invitation");

        t.Observe(
            await t.Operator.GetAsync(MessagesRoute, options => options.WithoutCredentials()),
            "without operator credentials the delivery log is not found");
        var first = t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?templateKey=team.invite&status=queued&pageSize=1"), "the delivery log pages newest first");
        t.Observe(
            await t.Operator.GetAsync($"{MessagesRoute}?templateKey=team.invite&status=queued&pageSize=1&cursor={Uri.EscapeDataString(first.JsonString("nextCursor"))}"),
            "the cursor continues");
        t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?recipient=ada-"), "by recipient substring");
        t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?status=FAILED"), "by status");
        t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?status=all&from=2000-01-01T00:00:00Z&to=2100-01-01T00:00:00Z"), "by creation window");
        t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?to=2000-01-01T00:00:00Z"), "a window before any delivery");

        t.Observe(await t.Operator.DeleteAsync($"{TemplatesRoute}/team.invite"), "deleting the template only deactivates it, because it has deliveries");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}?search=team&includeInactive=false"), "active templates exclude it");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}?search=team"), "the full list shows it inactive");
        t.Observe(
            await t.Operator.PostJsonAsync($"{TemplatesRoute}/team.invite/preview", new { variables = new { team = "Research", inviter = "Grace" } }),
            "an inactive template can still be previewed");
        t.Observe(
            await host.PostJsonAsync(SendProbe, new { templateKey = "team.invite", to = ada, variables = new { team = "Research", inviter = "Grace" } }),
            "but the host can no longer send with it");

        await t.ObserveAuditAsync("deliveries and the deactivation");
        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("PUT /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("GET /sqlos/admin/email/api/messages")]
    [Covers("GET /sqlos/admin/email/api/templates/{templateId}")]
    public async Task Editing_a_built_in_auth_template_changes_the_email_sqlos_sends()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        var alice = await t.Setup.CreateUserAsync("alice");

        var builtIn = t.Observe(
            await t.Operator.GetAsync($"{TemplatesRoute}/auth.password-reset"),
            "the built-in password-reset template");
        t.Observe(
            await t.Operator.PutJsonAsync($"{TemplatesRoute}/auth.password-reset", new
            {
                key = "auth.password-reset",
                displayName = builtIn.JsonString("displayName"),
                subjectTemplate = "{applicationName}: reset link inside",
                htmlBodyTemplate = "<p>Reset {maskedEmail}: <a href=\"{resetUrl}\">{resetUrl}</a></p>",
                textBodyTemplate = "Reset {maskedEmail}: {resetUrl}",
                variables = builtIn.Json!["variables"]!.DeepClone()
            }),
            "an operator rewrites it");
        t.Observe(
            await t.Api.PostJsonAsync("/sqlos/auth/password/forgot", new { email = alice.Email }),
            "the next password-reset email uses the edited template");
        t.Observe(
            await t.Operator.GetAsync($"{MessagesRoute}?templateKey=auth.password-reset"),
            "the delivery log does not store rendered content of sensitive built-in templates");

        await t.ApproveAsync();
    }

    [Scenario]
    [Covers("POST /sqlos/admin/email/api/templates")]
    [Covers("PUT /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("DELETE /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("GET /sqlos/admin/email/api/templates/{templateId}")]
    [Covers("POST /sqlos/admin/email/api/templates/{templateId}/preview")]
    [Covers("GET /sqlos/admin/email/api/templates")]
    [Covers("GET /sqlos/admin/email/api/messages")]
    public async Task Invalid_email_template_requests_are_rejected()
    {
        await using var t = await AdminConfigHost.StartAsync(HostProfiles.Modules);
        await t.Setup.OperatorPostAsync(TemplatesRoute, new
        {
            key = "billing.receipt",
            displayName = "Receipt",
            subjectTemplate = "Receipt",
            htmlBodyTemplate = "<p>Receipt</p>",
            textBodyTemplate = "Receipt"
        });

        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template("bad key!")), "a key with a space and punctuation");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template(".hidden")), "a key that does not start with a letter or digit");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template("no.subject", subject: " ")), "a blank subject");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template("no.html", html: "")), "a blank HTML body");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template("no.text", text: " ")), "a blank text body");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, Template("auth.email-otp")), "a key a built-in template already uses");
        t.Observe(await t.Operator.PostJsonAsync(TemplatesRoute, "{\"key\":"), "a malformed body is rejected by request binding");
        t.Observe(
            await t.Operator.PutJsonAsync($"{TemplatesRoute}/billing.receipt", Template("auth.magic-link")),
            "renaming a template to another template's key");
        t.Observe(await t.Operator.PutJsonAsync($"{TemplatesRoute}/missing.template", Template("missing.template")), "editing an unknown template");
        t.Observe(await t.Operator.DeleteAsync($"{TemplatesRoute}/missing.template"), "deleting an unknown template");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}/missing.template"), "reading an unknown template");
        t.Observe(await t.Operator.PostJsonAsync($"{TemplatesRoute}/missing.template/preview", new { }), "previewing an unknown template");
        t.Observe(await t.Operator.GetAsync($"{TemplatesRoute}?page=2"), "offset paging of templates");
        t.Observe(await t.Operator.GetAsync($"{MessagesRoute}?cursor=bm90LWEtY3Vyc29y"), "a cursor that is not a SqlOS cursor");

        await t.ObserveAuditAsync("rejected changes write no audit events");
        await t.ApproveAsync();
    }

    private static object Template(string key, string subject = "Subject", string html = "<p>Body</p>", string text = "Body")
        => new { key, displayName = key, subjectTemplate = subject, htmlBodyTemplate = html, textBodyTemplate = text };
}
