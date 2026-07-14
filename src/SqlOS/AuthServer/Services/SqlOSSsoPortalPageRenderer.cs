using System.Net;

namespace SqlOS.AuthServer.Services;

public static class SqlOSSsoPortalPageRenderer
{
    public static string RenderShell() =>
        """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>SSO Setup Portal</title>
            <style>
                :root {
                    color-scheme: light;
                    --bg: #f7f7f8;
                    --panel: #ffffff;
                    --ink: #171717;
                    --muted: #62646a;
                    --line: #dedfe3;
                    --accent: #0f766e;
                    --accent-ink: #ffffff;
                    --warn-bg: #fff7ed;
                    --warn-line: #fed7aa;
                    --ok-bg: #ecfdf5;
                    --ok-line: #a7f3d0;
                    --bad-bg: #fef2f2;
                    --bad-line: #fecaca;
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, system-ui, sans-serif;
                }
                * { box-sizing: border-box; }
                body { margin: 0; background: var(--bg); color: var(--ink); }
                header { border-bottom: 1px solid var(--line); background: var(--panel); }
                .wrap { max-width: 1160px; margin: 0 auto; padding: 22px; }
                .top { display: flex; justify-content: space-between; gap: 16px; align-items: center; }
                .brand { display: flex; gap: 12px; align-items: center; }
                .logo { width: 36px; height: 36px; border-radius: 8px; background: #111827; color: #fff; display: grid; place-items: center; font-weight: 800; }
                .eyebrow { color: var(--muted); font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: .08em; }
                h1 { margin: 2px 0 0; font-size: 24px; line-height: 1.15; letter-spacing: 0; }
                h2 { font-size: 16px; margin: 0 0 8px; letter-spacing: 0; }
                h3 { font-size: 14px; margin: 0 0 6px; letter-spacing: 0; }
                p { margin: 0; color: var(--muted); }
                main.wrap { display: grid; gap: 16px; }
                .grid { display: grid; grid-template-columns: minmax(270px, 360px) 1fr; gap: 16px; align-items: start; }
                .panel { background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 18px; }
                .stack { display: grid; gap: 12px; }
                .provider-list { display: grid; gap: 8px; }
                .provider { width: 100%; text-align: left; border: 1px solid var(--line); background: var(--panel); color: var(--ink); border-radius: 8px; padding: 12px; cursor: pointer; }
                .provider.active { border-color: var(--accent); box-shadow: 0 0 0 3px rgba(15, 118, 110, .12); }
                button, .button { border: 0; border-radius: 8px; padding: 9px 12px; background: var(--accent); color: var(--accent-ink); font: inherit; font-weight: 700; cursor: pointer; text-decoration: none; display: inline-flex; align-items: center; justify-content: center; gap: 6px; }
                button.secondary { background: #f4f4f5; color: var(--ink); border: 1px solid var(--line); }
                button.danger { background: #991b1b; }
                button:disabled { opacity: .45; cursor: not-allowed; }
                .row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
                .row-between { justify-content: space-between; }
                .check-row { display: flex; gap: 10px; align-items: flex-start; padding: 8px 0; }
                .check-row input { width: auto; margin-top: 2px; }
                .field-grid { display: grid; gap: 10px; }
                .field-grid-spaced { margin-top: 12px; }
                label { display: grid; gap: 6px; font-size: 12px; color: var(--muted); font-weight: 700; }
                input, textarea { width: 100%; border: 1px solid var(--line); border-radius: 8px; padding: 10px; font: inherit; background: #fff; color: var(--ink); }
                textarea { min-height: 180px; resize: vertical; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; }
                .code-row { display: grid; grid-template-columns: 1fr auto; gap: 8px; align-items: center; }
                .code { overflow-wrap: anywhere; border: 1px solid var(--line); background: #fafafa; border-radius: 8px; padding: 9px 10px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; color: #111827; }
                .meta { display: grid; gap: 8px; }
                .meta-row { display: grid; grid-template-columns: 160px 1fr; gap: 10px; padding: 8px 0; border-bottom: 1px solid #f0f0f2; }
                .meta-row:last-child { border-bottom: 0; }
                .meta-key { color: var(--muted); font-size: 12px; font-weight: 700; }
                .badge { display: inline-flex; align-items: center; width: max-content; border-radius: 999px; border: 1px solid var(--line); padding: 3px 8px; font-size: 12px; font-weight: 700; background: #fff; }
                .badge.ok { border-color: var(--ok-line); background: var(--ok-bg); color: #065f46; }
                .badge.warn { border-color: var(--warn-line); background: var(--warn-bg); color: #92400e; }
                .notice { border: 1px solid var(--line); border-radius: 8px; padding: 12px; color: var(--muted); }
                .notice.ok { border-color: var(--ok-line); background: var(--ok-bg); color: #065f46; }
                .notice.warn { border-color: var(--warn-line); background: var(--warn-bg); color: #92400e; }
                .notice.bad { border-color: var(--bad-line); background: var(--bad-bg); color: #991b1b; }
                ol { margin: 8px 0 0; padding-left: 20px; color: #2f3034; }
                li + li { margin-top: 6px; }
                .hidden { display: none !important; }
                @media (max-width: 820px) {
                    .grid { grid-template-columns: 1fr; }
                    .meta-row { grid-template-columns: 1fr; gap: 2px; }
                    .code-row { grid-template-columns: 1fr; }
                }
            </style>
        </head>
        <body>
            <header>
                <div class="wrap top">
                    <div class="brand">
                        <div class="logo">So</div>
                        <div>
                            <div class="eyebrow">SqlOS SSO Portal</div>
                            <h1 id="title">Organization SSO setup</h1>
                        </div>
                    </div>
                    <button id="signout" type="button" class="secondary">Close session</button>
                </div>
            </header>
            <main class="wrap">
                <div id="banner" class="notice hidden"></div>
                <section id="expired" class="panel hidden">
                    <h2>Setup link unavailable</h2>
                    <p>This SSO setup session is expired, revoked, or has not been opened in this browser.</p>
                </section>
                <div id="app" class="grid hidden">
                    <aside class="stack">
                        <section class="panel">
                            <h2>Provider</h2>
                            <div id="providers" class="provider-list"></div>
                        </section>
                        <section class="panel">
                            <h2>Organization</h2>
                            <div id="org-meta" class="meta"></div>
                        </section>
                    </aside>
                    <div class="stack">
                        <section class="panel">
                            <div class="row row-between">
                                <div>
                                    <h2>Service Provider Values</h2>
                                    <p>Copy these into the identity provider application.</p>
                                </div>
                                <span id="setup-status" class="badge"></span>
                            </div>
                            <div id="sp-values" class="field-grid field-grid-spaced"></div>
                        </section>
                        <section class="panel">
                            <h2 id="guide-title">Setup Steps</h2>
                            <ol id="guide-steps"></ol>
                        </section>
                        <section class="panel">
                            <h2>Access Policy</h2>
                            <div class="field-grid">
                                <label class="check-row">
                                    <input id="require-sso" type="checkbox">
                                    <span>
                                        <strong>Require SSO for existing members</strong><br>
                                        Existing organization members with verified email on this domain must use SSO on their next sign-in.
                                    </span>
                                </label>
                                <label class="check-row">
                                    <input id="allow-jit" type="checkbox">
                                    <span>
                                        <strong>Allow JIT provisioning from SSO</strong><br>
                                        Successful SSO sign-ins can create missing user access for this organization.
                                    </span>
                                </label>
                                <div class="row">
                                    <button id="save-policy" type="button" class="secondary">Save policy</button>
                                    <button id="revoke-sessions" type="button" class="danger">Sign out existing sessions</button>
                                </div>
                            </div>
                        </section>
                        <section class="panel">
                            <h2>Domain Verification</h2>
                            <div class="field-grid">
                                <label>Organization email domain
                                    <input id="domain" placeholder="acme.com">
                                </label>
                                <div class="row">
                                    <button id="domain-start" type="button" class="secondary">Start verification</button>
                                    <button id="domain-confirm" type="button">Confirm TXT record</button>
                                </div>
                                <div id="domain-record" class="notice hidden"></div>
                            </div>
                        </section>
                        <section class="panel">
                            <h2>Metadata</h2>
                            <div class="field-grid">
                                <label>Upload metadata XML
                                    <input id="metadata-file" type="file" accept=".xml,text/xml,application/xml">
                                </label>
                                <label>Paste metadata XML
                                    <textarea id="metadata" spellcheck="false"></textarea>
                                </label>
                                <div class="row">
                                    <button id="validate" type="button" class="secondary">Validate metadata</button>
                                    <button id="import" type="button">Save metadata</button>
                                    <button id="activate" type="button">Activate connection</button>
                                    <button id="disable" type="button" class="danger">Disable</button>
                                </div>
                            </div>
                        </section>
                        <section class="panel">
                            <h2>Test</h2>
                            <div class="field-grid">
                                <div class="row">
                                    <input id="client-id" placeholder="Client ID for test redirect">
                                    <input id="redirect-uri" placeholder="Redirect URI">
                                </div>
                                <button id="test" type="button" class="secondary">Run test</button>
                                <div id="test-result" class="notice hidden"></div>
                            </div>
                        </section>
                    </div>
                </div>
            </main>
            <script>
                const api = "./api";
                let state = null;

                const $ = (id) => document.getElementById(id);
                const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));

                function showBanner(message, kind = "ok") {
                    const banner = $("banner");
                    banner.className = `notice ${kind}`;
                    banner.textContent = message;
                    banner.classList.remove("hidden");
                }

                async function request(path, init) {
                    const response = await fetch(`${api}${path}`, {
                        credentials: "same-origin",
                        headers: { "content-type": "application/json", ...(init?.headers || {}) },
                        ...init
                    });
                    if (response.status === 401) {
                        $("app").classList.add("hidden");
                        $("expired").classList.remove("hidden");
                        throw new Error("Portal session is invalid or expired.");
                    }
                    const text = await response.text();
                    const data = text ? JSON.parse(text) : null;
                    if (!response.ok) {
                        throw new Error(data?.message || data?.error || `Request failed with ${response.status}`);
                    }
                    return data;
                }

                async function loadState() {
                    state = await request("/state");
                    render();
                }

                function provider() {
                    return state.providers.find((item) => item.key === state.provider) || state.providers[0];
                }

                function enrollmentPolicy() {
                    return state.connection.enrollmentPolicy || {
                        requireSsoForExistingMembers: state.connection.autoLinkByEmail,
                        allowJitProvisioning: state.connection.autoProvisionUsers
                    };
                }

                function render() {
                    $("expired").classList.add("hidden");
                    $("app").classList.remove("hidden");
                    $("title").textContent = `${state.organization.name} SSO setup`;
                    const selected = provider();
                    $("providers").innerHTML = state.providers.map((item) => `
                        <button type="button" class="provider ${item.key === selected.key ? "active" : ""}" data-provider="${esc(item.key)}">
                            <strong>${esc(item.label)}</strong><br>
                            <span>${esc(item.metadataLabel)}</span>
                        </button>
                    `).join("");
                    document.querySelectorAll("[data-provider]").forEach((button) => {
                        button.addEventListener("click", async () => {
                            state = await request("/provider", { method: "PUT", body: JSON.stringify({ provider: button.dataset.provider }) });
                            showBanner("Provider saved.");
                            render();
                        });
                    });

                    $("org-meta").innerHTML = metaRows([
                        ["Name", state.organization.name],
                        ["Primary domain", state.organization.primaryDomain || "Not set"],
                        ["Organization ID", state.organization.id]
                    ]);

                    const status = state.connection.setupStatus;
                    $("setup-status").textContent = status.replaceAll("_", " ");
                    $("setup-status").className = `badge ${status === "active" ? "ok" : "warn"}`;
                    $("sp-values").innerHTML = `
                        ${codeField(selected.entityIdLabel, state.serviceProviderEntityId)}
                        ${codeField(selected.acsUrlLabel, state.assertionConsumerServiceUrl)}
                    `;
                    document.querySelectorAll("[data-copy]").forEach((button) => {
                        button.addEventListener("click", async () => {
                            await navigator.clipboard.writeText(button.dataset.copy);
                            showBanner("Copied.");
                        });
                    });

                    $("guide-title").textContent = `${selected.label} setup`;
                    $("guide-steps").innerHTML = selected.steps.map((step) => `<li>${esc(step)}</li>`).join("");
                    const policy = enrollmentPolicy();
                    $("require-sso").checked = !!policy.requireSsoForExistingMembers;
                    $("allow-jit").checked = !!policy.allowJitProvisioning;
                    $("save-policy").disabled = !(state.allowedActions?.canUpdateEnrollmentPolicy ?? true);
                    $("revoke-sessions").disabled = !(state.allowedActions?.canRevokeOrganizationSessions ?? false);
                    renderDomain();
                    $("activate").disabled = !(state.allowedActions?.canActivate ?? true);
                    $("disable").disabled = !(state.allowedActions?.canDisable ?? false);
                    $("test").disabled = !(state.allowedActions?.canTest ?? false);
                    if (state.latestTest) {
                        showTest(state.latestTest);
                    }
                }

                function renderDomain() {
                    const domain = state.domain;
                    $("domain").value = domain?.domain || state.organization.primaryDomain || "";
                    $("domain-confirm").disabled = !(state.allowedActions?.canConfirmDomainVerification ?? false);
                    const box = $("domain-record");
                    if (!domain) {
                        box.classList.add("hidden");
                        box.innerHTML = "";
                        return;
                    }

                    const statusClass = domain.status === "active" ? "ok" : domain.lastError ? "bad" : "warn";
                    const record = domain.ownershipRecord
                        ? `
                            ${metaRows([
                                ["Type", domain.ownershipRecord.type],
                                ["Name", domain.ownershipRecord.name],
                                ["Value", domain.ownershipRecord.value]
                            ])}
                        `
                        : "";
                    box.className = `notice ${statusClass}`;
                    box.innerHTML = `
                        <strong>${esc(domain.domain)} ${esc(domain.status.replaceAll("_", " "))}</strong>
                        ${record}
                        ${domain.lastError ? `<p>${esc(domain.lastError)}</p>` : ""}
                    `;
                    box.classList.remove("hidden");
                }

                function metaRows(rows) {
                    return rows.map(([key, value]) => `<div class="meta-row"><div class="meta-key">${esc(key)}</div><div>${esc(value)}</div></div>`).join("");
                }

                function codeField(label, value) {
                    return `<label>${esc(label)}<div class="code-row"><div class="code">${esc(value)}</div><button type="button" class="secondary" data-copy="${esc(value)}">Copy</button></div></label>`;
                }

                function metadataPayload() {
                    return { metadataXml: $("metadata").value };
                }

                function showTest(result) {
                    const box = $("test-result");
                    box.className = `notice ${result.status === "ready" ? "ok" : result.status === "started" ? "ok" : "bad"}`;
                    box.innerHTML = `${esc(result.message)}${result.authorizationUrl ? `<br><a href="${esc(result.authorizationUrl)}">Open IdP test redirect</a>` : ""}`;
                    box.classList.remove("hidden");
                }

                $("metadata-file").addEventListener("change", async (event) => {
                    const file = event.target.files && event.target.files[0];
                    if (file) $("metadata").value = await file.text();
                });
                $("validate").addEventListener("click", async () => {
                    const result = await request("/metadata/validate", { method: "POST", body: JSON.stringify(metadataPayload()) });
                    if (result.isValid) showBanner(`Metadata valid for ${result.identityProviderEntityId}.`);
                    else showBanner(result.error || "Metadata is invalid.", "bad");
                });
                $("import").addEventListener("click", async () => {
                    state = await request("/metadata", { method: "POST", body: JSON.stringify(metadataPayload()) });
                    showBanner("Metadata saved. Review before activation.");
                    render();
                });
                $("domain-start").addEventListener("click", async () => {
                    state = await request("/domain", { method: "POST", body: JSON.stringify({ domain: $("domain").value }) });
                    showBanner("Domain verification record created.");
                    render();
                });
                $("domain-confirm").addEventListener("click", async () => {
                    if (!state.domain?.id) return;
                    state = await request(`/domains/${encodeURIComponent(state.domain.id)}/confirm`, { method: "POST", body: "{}" });
                    if (state.domain?.status === "active") showBanner("Domain verified.");
                    else showBanner(state.domain?.lastError || "Domain record was not found yet.", "bad");
                    render();
                });
                $("save-policy").addEventListener("click", async () => {
                    state = await request("/enrollment-policy", {
                        method: "PUT",
                        body: JSON.stringify({
                            requireSsoForExistingMembers: $("require-sso").checked,
                            allowJitProvisioning: $("allow-jit").checked
                        })
                    });
                    showBanner("Access policy saved.");
                    render();
                });
                $("activate").addEventListener("click", async () => {
                    state = await request("/activate", { method: "POST", body: "{}" });
                    showBanner("Connection activated.");
                    render();
                });
                $("disable").addEventListener("click", async () => {
                    state = await request("/disable", { method: "POST", body: "{}" });
                    showBanner("Connection disabled.", "warn");
                    render();
                });
                $("test").addEventListener("click", async () => {
                    const toBase64Url = (bytes) => btoa(String.fromCharCode(...bytes))
                        .replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
                    const verifierBytes = crypto.getRandomValues(new Uint8Array(32));
                    const verifier = toBase64Url(verifierBytes);
                    const challengeBytes = new Uint8Array(await crypto.subtle.digest(
                        "SHA-256",
                        new TextEncoder().encode(verifier)));
                    const stateBytes = crypto.getRandomValues(new Uint8Array(32));
                    const result = await request("/test", {
                        method: "POST",
                        body: JSON.stringify({
                            clientId: $("client-id").value || null,
                            redirectUri: $("redirect-uri").value || null,
                            state: toBase64Url(stateBytes),
                            codeChallenge: toBase64Url(challengeBytes),
                            codeChallengeMethod: "S256"
                        })
                    });
                    showTest(result);
                });
                $("revoke-sessions").addEventListener("click", async () => {
                    if (!confirm("Sign out active sessions for this organization and SSO domain?")) return;
                    const result = await request("/organization-sessions/revoke", { method: "POST", body: JSON.stringify({ confirm: true }) });
                    showBanner(`${result.revokedSessions} active session${result.revokedSessions === 1 ? "" : "s"} signed out.`);
                });
                $("signout").addEventListener("click", async () => {
                    await request("/signout", { method: "POST", body: "{}" });
                    window.location.reload();
                });

                loadState().catch((error) => {
                    showBanner(error.message, "bad");
                });
            </script>
        </body>
        </html>
        """;

    public static string RenderStartError(string message) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>SSO Setup Portal</title>
            <style>
                body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f7f7f8; color: #171717; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Inter, system-ui, sans-serif; }
                main { width: min(520px, calc(100vw - 32px)); background: #fff; border: 1px solid #dedfe3; border-radius: 8px; padding: 22px; }
                h1 { margin: 0 0 8px; font-size: 22px; letter-spacing: 0; }
                p { margin: 0; color: #62646a; }
            </style>
        </head>
        <body>
            <main>
                <h1>Setup link unavailable</h1>
                <p>{{WebUtility.HtmlEncode(message)}}</p>
            </main>
        </body>
        </html>
        """;
}
