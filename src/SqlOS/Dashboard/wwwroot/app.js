(function () {
    const dashboardBasePath = normalizeBasePath(window.__SQL_OS_BASE_PATH__ || "/sqlos");
    const authDashboardPath = `${dashboardBasePath}/admin/auth`;
    const fgaDashboardPath = `${dashboardBasePath}/admin/fga`;
    const authApiBasePath = `${authDashboardPath}/api`;
    const fgaApiBasePath = `${fgaDashboardPath}/api`;

    const content = document.getElementById("content");
    const pageEyebrow = document.getElementById("page-eyebrow");
    const pageTitle = document.getElementById("page-title");
    const pageDescription = document.getElementById("page-description");
    const topbarTitle = document.getElementById("topbar-title");

    let flashMessage = null;
    let latestSsoDraft = null;

    const authViews = {
        overview: { title: "Auth Server", description: "Organizations, users, sessions, SSO, clients, and security settings." },
        organizations: { title: "Organizations", description: "Create and manage organizations and their primary domains." },
        users: { title: "Users", description: "Create users and bootstrap password credentials." },
        memberships: { title: "Memberships", description: "Assign users to organizations and manage roles." },
        clients: { title: "Clients", description: "Register clients, audiences, and redirect URIs." },
        oidc: { title: "OIDC", description: "Configure global Google, Microsoft, Apple, and custom OIDC connections." },
        sso: { title: "SSO", description: "Create SAML drafts, import Entra metadata, and review setup state." },
        security: { title: "Security", description: "Tune refresh, idle, and absolute session lifetimes." },
        sessions: { title: "Sessions", description: "Inspect active sessions and authentication methods." },
        audit: { title: "Audit Events", description: "Review recent auth and admin activity." }
    };

    const fgaViews = {
        resources: { title: "Resources", description: "Inspect the resource hierarchy and navigate the authorization graph.", hash: "/resources" },
        grants: { title: "Grants", description: "Review and manage subject grants across the resource tree.", hash: "/grants" },
        roles: { title: "Roles", description: "Maintain the role model used by authorization checks.", hash: "/roles" },
        permissions: { title: "Permissions", description: "Manage permission keys and their resource associations.", hash: "/permissions" },
        users: { title: "FGA Users", description: "Inspect user subjects in the authorization graph.", hash: "/users" },
        agents: { title: "Agents", description: "Inspect non-human agent subjects.", hash: "/agents" },
        "service-accounts": { title: "Service Accounts", description: "Inspect service account subjects and grants.", hash: "/service-accounts" },
        "user-groups": { title: "User Groups", description: "Review groups and inherited access paths.", hash: "/user-groups" },
        "access-tester": { title: "Access Tester", description: "Trace access decisions for a subject, resource, and permission.", hash: "/access-tester" }
    };

    configureNavLinks();

    document.addEventListener("click", (event) => {
        const link = event.target.closest("a[data-route], a[data-dashboard-route]");
        if (!link) {
            return;
        }

        const href = link.getAttribute("href");
        if (!href) {
            return;
        }

        const url = new URL(href, window.location.origin);
        if (url.origin !== window.location.origin) {
            return;
        }

        event.preventDefault();
        history.pushState({}, "", url.pathname);
        render();
    });

    window.addEventListener("popstate", render);

    document.getElementById("hamburger")?.addEventListener("click", () => {
        document.getElementById("sidebar")?.classList.add("open");
    });

    document.getElementById("sidebar-close")?.addEventListener("click", () => {
        document.getElementById("sidebar")?.classList.remove("open");
    });

    render();

    function normalizeBasePath(value) {
        if (!value || value === "/") {
            return "";
        }

        return value.endsWith("/") ? value.slice(0, -1) : value;
    }

    function fetchJson(url, options = {}) {
        return fetch(url, {
            ...options,
            headers: {
                "Content-Type": "application/json",
                ...(options.headers || {})
            }
        }).then(async response => {
            if (!response.ok) {
                const text = await response.text();
                const error = new Error(text || `${response.status}`);
                error.status = response.status;
                throw error;
            }

            return response.status === 204 ? null : response.json();
        });
    }

    function esc(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll("\"", "&quot;")
            .replaceAll("'", "&#39;");
    }

    function setHeader(eyebrow, title, description) {
        pageEyebrow.textContent = eyebrow;
        pageTitle.textContent = title;
        pageDescription.textContent = description;
        topbarTitle.textContent = title;
    }

    function configureNavLinks() {
        document.querySelectorAll("a[data-route]").forEach(link => {
            const route = link.dataset.route;
            link.href = pathForRoute(route);
        });
    }

    function pathForRoute(route) {
        if (route === "home") {
            return `${dashboardBasePath}/`;
        }

        if (route.startsWith("auth-")) {
            return `${authDashboardPath}/${route.slice(5)}`;
        }

        if (route.startsWith("fga-")) {
            return `${fgaDashboardPath}/${route.slice(4)}`;
        }

        return `${dashboardBasePath}/`;
    }

    function quickLink(route, label) {
        return `<a class="quick-link" data-dashboard-route="${route}" href="${esc(pathForRoute(route))}">${esc(label)} <span>&rarr;</span></a>`;
    }

    function currentRoute() {
        const pathname = window.location.pathname;
        const relativePath = pathname.startsWith(dashboardBasePath)
            ? pathname.slice(dashboardBasePath.length)
            : pathname;
        const trimmed = relativePath.replace(/^\/+|\/+$/g, "");

        if (!trimmed) {
            return { kind: "home", key: "home", canonicalPath: `${dashboardBasePath}/` };
        }

        const segments = trimmed.split("/");
        if (segments[0] !== "admin") {
            return { kind: "home", key: "home", canonicalPath: `${dashboardBasePath}/` };
        }

        if (segments[1] === "auth") {
            const view = authViews[segments[2]] ? segments[2] : "overview";
            return {
                kind: "auth",
                view,
                key: `auth-${view}`,
                canonicalPath: `${authDashboardPath}/${view}`
            };
        }

        if (segments[1] === "fga") {
            const view = fgaViews[segments[2]] ? segments[2] : "resources";
            return {
                kind: "fga",
                view,
                key: `fga-${view}`,
                canonicalPath: `${fgaDashboardPath}/${view}`
            };
        }

        return { kind: "home", key: "home", canonicalPath: `${dashboardBasePath}/` };
    }

    function updateActiveNav(routeKey) {
        document.querySelectorAll("nav a[data-route]").forEach(link => {
            link.classList.toggle("active", link.dataset.route === routeKey);
        });
        document.getElementById("sidebar")?.classList.remove("open");
    }

    function consumeFlashHtml() {
        if (!flashMessage) {
            return "";
        }

        const current = flashMessage;
        flashMessage = null;
        const className = current.type === "error" ? "error-banner" : "success-banner";
        return `<div class="${className}">${esc(current.message)}</div>`;
    }

    function setFlash(type, message) {
        flashMessage = { type, message };
    }

    function formatDate(value) {
        if (!value) {
            return "n/a";
        }

        const parsed = new Date(value);
        return Number.isNaN(parsed.getTime()) ? String(value) : parsed.toLocaleString();
    }

    function parseJsonArray(value) {
        if (!value) {
            return [];
        }

        if (Array.isArray(value)) {
            return value;
        }

        try {
            const parsed = JSON.parse(value);
            return Array.isArray(parsed) ? parsed : [];
        } catch {
            return [];
        }
    }

    function parseJsonObject(value, fallback = {}) {
        if (!value) {
            return fallback;
        }

        if (typeof value === "object" && !Array.isArray(value)) {
            return value;
        }

        try {
            const parsed = JSON.parse(value);
            return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : fallback;
        } catch {
            return fallback;
        }
    }

    function renderMetadataRows(rows) {
        return `<div class="meta-list">${rows
            .filter(row => row.html || (row.value !== null && row.value !== undefined && row.value !== ""))
            .map(row => `
                <div class="meta-row">
                    <span class="meta-key">${esc(row.label)}</span>
                    <span>${row.html ?? esc(row.value)}</span>
                </div>
            `)
            .join("")}</div>`;
    }

    function renderList(items, formatter, emptyText) {
        if (!items || items.length === 0) {
            return `<div class="empty-state-block">${esc(emptyText)}</div>`;
        }

        return `<div class="list-stack">${items.map(item => `<div class="list-item">${formatter(item)}</div>`).join("")}</div>`;
    }

    function buildOidcPayload(form) {
        const claimMappingText = String(form.get("claimMapping") || "").trim();
        return {
            providerType: form.get("providerType") || null,
            displayName: form.get("displayName"),
            clientId: form.get("clientId"),
            clientSecret: form.get("clientSecret") || null,
            allowedCallbackUris: String(form.get("allowedCallbackUris") || "")
                .split("\n")
                .map(value => value.trim())
                .filter(Boolean),
            useDiscovery: form.get("useDiscovery") === "on",
            discoveryUrl: form.get("discoveryUrl") || null,
            issuer: form.get("issuer") || null,
            authorizationEndpoint: form.get("authorizationEndpoint") || null,
            tokenEndpoint: form.get("tokenEndpoint") || null,
            userInfoEndpoint: form.get("userInfoEndpoint") || null,
            jwksUri: form.get("jwksUri") || null,
            microsoftTenant: form.get("microsoftTenant") || null,
            scopes: String(form.get("scopes") || "")
                .split("\n")
                .map(value => value.trim())
                .filter(Boolean),
            claimMapping: claimMappingText ? parseJsonObject(claimMappingText, null) : null,
            clientAuthMethod: form.get("clientAuthMethod") || null,
            useUserInfo: form.get("useUserInfo") === "on",
            appleTeamId: form.get("appleTeamId") || null,
            appleKeyId: form.get("appleKeyId") || null,
            applePrivateKeyPem: form.get("applePrivateKeyPem") || null
        };
    }

    function renderStatsGroup(title, stats, keys) {
        return `
            <section class="card">
                <h2>${esc(title)}</h2>
                <div class="stats-grid">
                    ${keys.map(key => `
                        <div class="stat-card">
                            <div class="stat-label">${esc(key.label)}</div>
                            <div class="stat-value">${esc(stats[key.key] ?? 0)}</div>
                        </div>
                    `).join("")}
                </div>
            </section>
        `;
    }

    function renderLoading(message) {
        content.innerHTML = `<div class="loading">${esc(message)}</div>`;
    }

    async function render() {
        const route = currentRoute();
        if (window.location.pathname !== route.canonicalPath) {
            history.replaceState({}, "", route.canonicalPath);
        }

        updateActiveNav(route.key);

        try {
            if (route.kind === "home") {
                await renderHome();
                return;
            }

            if (route.kind === "auth") {
                await renderAuthRoute(route.view);
                return;
            }

            await renderFgaRoute(route.view);
        } catch (error) {
            content.innerHTML = `${consumeFlashHtml()}<div class="error-banner">${esc(error.message || String(error))}</div>`;
        }
    }

    async function renderHome() {
        setHeader(
            "Dashboard",
            "SqlOS Dashboard",
            "One control plane for auth server operations and fine-grained authorization. Use real page routes in the sidebar to move between areas."
        );

        renderLoading("Loading dashboard overview...");

        const [authStats, fgaStats] = await Promise.all([
            fetchJson(`${authApiBasePath}/stats`),
            fetchJson(`${fgaApiBasePath}/stats`)
        ]);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="dashboard-grid">
                ${renderStatsGroup("Auth Server", authStats, [
                    { key: "organizations", label: "Organizations" },
                    { key: "users", label: "Users" },
                    { key: "clients", label: "Clients" },
                    { key: "oidcConnections", label: "OIDC Connections" },
                    { key: "ssoConnections", label: "SSO Connections" },
                    { key: "sessions", label: "Sessions" },
                    { key: "auditEvents", label: "Audit Events" }
                ])}
                ${renderStatsGroup("Fine-Grained Auth", fgaStats, [
                    { key: "resources", label: "Resources" },
                    { key: "subjects", label: "Subjects" },
                    { key: "users", label: "Users" },
                    { key: "agents", label: "Agents" },
                    { key: "serviceAccounts", label: "Service Accounts" },
                    { key: "userGroups", label: "User Groups" },
                    { key: "grants", label: "Grants" },
                    { key: "roles", label: "Roles" },
                    { key: "permissions", label: "Permissions" }
                ])}
                <section class="card">
                    <h2>Auth Server</h2>
                    <p>Use the direct routes for organizations, clients, SSO setup, sessions, and security settings.</p>
                    <div class="link-list">
                        ${quickLink("auth-organizations", "Organizations")}
                        ${quickLink("auth-users", "Users")}
                        ${quickLink("auth-oidc", "OIDC")}
                        ${quickLink("auth-sso", "SSO")}
                        ${quickLink("auth-security", "Security")}
                    </div>
                </section>
                <section class="card">
                    <h2>Fine-Grained Auth</h2>
                    <p>Open the authorization graph areas through the same shell.</p>
                    <div class="link-list">
                        ${quickLink("fga-resources", "Resources")}
                        ${quickLink("fga-grants", "Grants")}
                        ${quickLink("fga-roles", "Roles")}
                        ${quickLink("fga-access-tester", "Access Tester")}
                    </div>
                </section>
            </div>
        `;
    }

    async function renderAuthRoute(view) {
        if (view === "overview") {
            await renderAuthOverview();
            return;
        }

        if (view === "organizations") {
            await renderAuthOrganizations();
            return;
        }

        if (view === "users") {
            await renderAuthUsers();
            return;
        }

        if (view === "memberships") {
            await renderAuthMemberships();
            return;
        }

        if (view === "clients") {
            await renderAuthClients();
            return;
        }

        if (view === "oidc") {
            await renderAuthOidc();
            return;
        }

        if (view === "sso") {
            await renderAuthSso();
            return;
        }

        if (view === "security") {
            await renderAuthSecurity();
            return;
        }

        if (view === "sessions") {
            await renderAuthSessions();
            return;
        }

        await renderAuthAudit();
    }

    async function renderAuthOverview() {
        const config = authViews.overview;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading auth overview...");

        const [stats, settings, ssoConnections] = await Promise.all([
            fetchJson(`${authApiBasePath}/stats`),
            fetchJson(`${authApiBasePath}/settings/security`),
            fetchJson(`${authApiBasePath}/sso-connections`)
        ]);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                ${renderStatsGroup("Auth Server Overview", stats, [
                    { key: "organizations", label: "Organizations" },
                    { key: "users", label: "Users" },
                    { key: "clients", label: "Clients" },
                    { key: "oidcConnections", label: "OIDC Connections" },
                    { key: "ssoConnections", label: "SSO Connections" },
                    { key: "sessions", label: "Sessions" },
                    { key: "auditEvents", label: "Audit Events" }
                ])}
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Security Settings</h2>
                        <p>These are the current runtime values used for session and refresh handling.</p>
                        ${renderMetadataRows([
                            { label: "Refresh token lifetime", value: `${settings.refreshTokenLifetimeMinutes} minutes` },
                            { label: "Idle timeout", value: `${settings.sessionIdleTimeoutMinutes} minutes` },
                            { label: "Absolute lifetime", value: `${settings.sessionAbsoluteLifetimeMinutes} minutes` }
                        ])}
                    </section>
                    <section class="panel">
                        <h2>SSO Connections</h2>
                        <p>Use the SSO page for draft creation and metadata import.</p>
                        ${renderList(
                            ssoConnections.slice(0, 5),
                            item => `
                                <strong>${esc(item.displayName)}</strong>
                                ${renderMetadataRows([
                                    { label: "Organization", value: item.organization },
                                    { label: "Primary domain", value: item.primaryDomain || "n/a" },
                                    { label: "Status", value: `${item.setupStatus} | Enabled: ${item.isEnabled}` }
                                ])}
                            `,
                            "No SSO connections yet."
                        )}
                    </section>
                    <section class="panel">
                        <h2>OIDC Providers</h2>
                        <p>Google, Microsoft, Apple, and custom providers are configured globally for the auth server.</p>
                        <div class="link-list">
                            ${quickLink("auth-oidc", "Open OIDC")}
                        </div>
                    </section>
                </div>
            </div>
        `;
    }

    async function renderAuthOrganizations() {
        const config = authViews.organizations;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading organizations...");

        const organizations = await fetchJson(`${authApiBasePath}/organizations`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Create Organization</h2>
                    <p>Create a tenant and optionally set its primary login domain.</p>
                    <form id="create-org-form">
                        <input name="name" placeholder="Organization name" required>
                        <input name="slug" placeholder="Slug (optional)">
                        <input name="primaryDomain" placeholder="Primary domain (optional)">
                        <button type="submit">Create organization</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Organizations</h2>
                    ${renderList(
                        organizations,
                        item => `
                            <strong>${esc(item.name)}</strong>
                            ${renderMetadataRows([
                                { label: "ID", value: item.id },
                                { label: "Slug", value: item.slug },
                                { label: "Primary domain", value: item.primaryDomain || "n/a" },
                                { label: "Memberships", value: item.membershipCount },
                                { label: "Enabled SSO", value: item.enabledSsoConnections }
                            ])}
                        `,
                        "No organizations yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-org-form", async form => {
            await fetchJson(`${authApiBasePath}/organizations`, {
                method: "POST",
                body: JSON.stringify({
                    name: form.get("name"),
                    slug: form.get("slug") || null,
                    primaryDomain: form.get("primaryDomain") || null
                })
            });
            setFlash("success", "Organization created.");
        });
    }

    async function renderAuthUsers() {
        const config = authViews.users;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading users...");

        const users = await fetchJson(`${authApiBasePath}/users`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Create User</h2>
                    <p>Create a user and optionally assign a password credential immediately.</p>
                    <form id="create-user-form">
                        <input name="displayName" placeholder="Display name" required>
                        <input name="email" placeholder="Email" required>
                        <input name="password" type="password" placeholder="Password (optional)">
                        <button type="submit">Create user</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Users</h2>
                    ${renderList(
                        users,
                        item => `
                            <strong>${esc(item.displayName)}</strong>
                            ${renderMetadataRows([
                                { label: "ID", value: item.id },
                                { label: "Email", value: item.defaultEmail || "n/a" },
                                { label: "Created", value: formatDate(item.createdAt) }
                            ])}
                        `,
                        "No users yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-user-form", async form => {
            await fetchJson(`${authApiBasePath}/users`, {
                method: "POST",
                body: JSON.stringify({
                    displayName: form.get("displayName"),
                    email: form.get("email"),
                    password: form.get("password") || null
                })
            });
            setFlash("success", "User created.");
        });
    }

    async function renderAuthMemberships() {
        const config = authViews.memberships;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading memberships...");

        const memberships = await fetchJson(`${authApiBasePath}/memberships`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Create Membership</h2>
                    <p>Use IDs from the Organizations and Users pages.</p>
                    <form id="create-membership-form">
                        <input name="organizationId" placeholder="Organization ID" required>
                        <input name="userId" placeholder="User ID" required>
                        <input name="role" placeholder="Role" value="member" required>
                        <button type="submit">Create membership</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Memberships</h2>
                    ${renderList(
                        memberships,
                        item => `
                            <strong>${esc(item.organization)}</strong>
                            ${renderMetadataRows([
                                { label: "Organization ID", value: item.organizationId },
                                { label: "User", value: `${item.user} (${item.userEmail || "no email"})` },
                                { label: "User ID", value: item.userId },
                                { label: "Role", value: item.role }
                            ])}
                        `,
                        "No memberships yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-membership-form", async form => {
            await fetchJson(`${authApiBasePath}/memberships`, {
                method: "POST",
                body: JSON.stringify({
                    organizationId: form.get("organizationId"),
                    userId: form.get("userId"),
                    role: form.get("role") || "member"
                })
            });
            setFlash("success", "Membership created.");
        });
    }

    async function renderAuthClients() {
        const config = authViews.clients;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading clients...");

        const clients = await fetchJson(`${authApiBasePath}/clients`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Create Client</h2>
                    <p>Register a client ID and its allowed redirect URIs.</p>
                    <form id="create-client-form">
                        <input name="clientId" placeholder="Client ID" required>
                        <input name="name" placeholder="Name" required>
                        <input name="audience" placeholder="Audience" value="sqlos">
                        <textarea name="redirectUris" placeholder="One redirect URI per line"></textarea>
                        <button type="submit">Create client</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Clients</h2>
                    ${renderList(
                        clients,
                        item => `
                            <strong>${esc(item.name)}</strong>
                            ${renderMetadataRows([
                                { label: "Client ID", value: item.clientId },
                                { label: "Audience", value: item.audience },
                                {
                                    label: "Redirect URIs",
                                    value: parseJsonArray(item.redirectUris).length > 0 ? "" : "none",
                                    html: parseJsonArray(item.redirectUris).length > 0
                                        ? parseJsonArray(item.redirectUris).map(uri => `<div class="inline-code">${esc(uri)}</div>`).join("")
                                        : "none"
                                }
                            ])}
                        `,
                        "No clients yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-client-form", async form => {
            await fetchJson(`${authApiBasePath}/clients`, {
                method: "POST",
                body: JSON.stringify({
                    clientId: form.get("clientId"),
                    name: form.get("name"),
                    audience: form.get("audience") || "sqlos",
                    redirectUris: String(form.get("redirectUris") || "")
                        .split("\n")
                        .map(value => value.trim())
                        .filter(Boolean)
                })
            });
            setFlash("success", "Client created.");
        });
    }

    async function renderAuthOidc() {
        const config = authViews.oidc;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading OIDC connections...");

        const oidcConnections = await fetchJson(`${authApiBasePath}/oidc-connections`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Create OIDC Connection</h2>
                        <p>Preset providers use discovery by default. Custom providers can use discovery or fully manual endpoints and claim mapping.</p>
                        <form id="create-oidc-connection-form">
                            <select name="providerType" required>
                                <option value="Google">Google</option>
                                <option value="Microsoft">Microsoft</option>
                                <option value="Apple">Apple</option>
                                <option value="Custom">Custom</option>
                            </select>
                            <input name="displayName" placeholder="Display name" required>
                            <input name="clientId" placeholder="Provider client ID / service ID" required>
                            <input name="clientSecret" type="password" placeholder="Client secret (not used for Apple)">
                            <label class="checkbox-line"><input name="useDiscovery" type="checkbox" checked> Use discovery</label>
                            <input name="discoveryUrl" placeholder="Discovery URL (custom only when discovery is enabled)">
                            <input name="issuer" placeholder="Issuer (manual custom only)">
                            <input name="authorizationEndpoint" placeholder="Authorization endpoint (manual custom only)">
                            <input name="tokenEndpoint" placeholder="Token endpoint (manual custom only)">
                            <input name="userInfoEndpoint" placeholder="User info endpoint (optional)">
                            <input name="jwksUri" placeholder="JWKS URI (manual custom only)">
                            <label class="checkbox-line"><input name="useUserInfo" type="checkbox" checked> Use user info endpoint</label>
                            <select name="clientAuthMethod">
                                <option value="">Default</option>
                                <option value="ClientSecretPost">ClientSecretPost</option>
                                <option value="ClientSecretBasic">ClientSecretBasic</option>
                            </select>
                            <input name="microsoftTenant" placeholder="Microsoft tenant (optional, defaults to common)">
                            <input name="appleTeamId" placeholder="Apple team ID">
                            <input name="appleKeyId" placeholder="Apple key ID">
                            <textarea name="applePrivateKeyPem" placeholder="Apple private key PEM (.p8)"></textarea>
                            <textarea name="allowedCallbackUris" placeholder="One callback URI per line" required></textarea>
                            <textarea name="scopes" placeholder="Optional scopes, one per line"></textarea>
                            <textarea name="claimMapping" placeholder='Claim mapping JSON, for example {\"SubjectClaim\":\"sub\",\"EmailClaim\":\"email\"}'></textarea>
                            <button type="submit">Create OIDC connection</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Provider Setup Notes</h2>
                        <p>Copy the exact callback URI from each configured connection after it is created. Apple requires a public HTTPS callback and will not accept localhost.</p>
                        ${renderMetadataRows([
                            { label: "Example callback pattern", value: "http://localhost:5062/api/v1/auth/oidc/callback/{connectionId}" },
                            { label: "Google / Microsoft", value: "The example app backend owns the callback and completes OIDC through SqlOS services." },
                            { label: "Apple", value: "Use a public HTTPS callback for real testing, then paste the Team ID, Key ID, and private key into the dashboard." },
                            { label: "Custom OIDC", value: "Use discovery when possible; switch to manual mode only when the provider discovery document is incomplete." }
                        ])}
                    </section>
                </div>
                <section class="panel">
                    <h2>OIDC Connections</h2>
                    ${renderList(
                        oidcConnections,
                        item => `
                            <strong>${esc(item.displayName)}</strong>
                            ${renderMetadataRows([
                                { label: "Provider", value: item.providerType },
                                { label: "Connection ID", value: item.id },
                                { label: "Client ID", value: item.clientId },
                                { label: "Discovery", value: item.useDiscovery ? "Enabled" : "Manual" },
                                { label: "Discovery URL", value: item.discoveryUrl },
                                { label: "Issuer", value: item.issuer },
                                { label: "Authorization endpoint", value: item.authorizationEndpoint },
                                { label: "Token endpoint", value: item.tokenEndpoint },
                                { label: "User info endpoint", value: item.userInfoEndpoint },
                                { label: "JWKS URI", value: item.jwksUri },
                                { label: "Microsoft tenant", value: item.microsoftTenant || "common" },
                                { label: "Client auth method", value: item.clientAuthMethod || "Default" },
                                { label: "Use user info", value: item.useUserInfo ? "Yes" : "No" },
                                { label: "Apple team ID", value: item.appleTeamId },
                                { label: "Apple key ID", value: item.appleKeyId },
                                {
                                    label: "Callback URIs",
                                    value: parseJsonArray(item.allowedCallbackUris).length > 0 ? "" : "none",
                                    html: parseJsonArray(item.allowedCallbackUris).length > 0
                                        ? parseJsonArray(item.allowedCallbackUris).map(uri => `<div class="inline-code">${esc(uri)}</div>`).join("")
                                        : "none"
                                },
                                {
                                    label: "Scopes",
                                    value: parseJsonArray(item.scopes).length > 0 ? "" : "default",
                                    html: parseJsonArray(item.scopes).length > 0
                                        ? parseJsonArray(item.scopes).map(scope => `<div class="inline-code">${esc(scope)}</div>`).join("")
                                        : "default"
                                },
                                {
                                    label: "Claim mapping",
                                    html: `<pre>${esc(JSON.stringify(parseJsonObject(item.claimMapping), null, 2))}</pre>`
                                },
                                { label: "Enabled", value: item.isEnabled ? "Yes" : "No" }
                            ])}
                            <form id="edit-oidc-${esc(item.id)}" class="nested-form">
                                <input name="displayName" value="${esc(item.displayName)}" required>
                                <input name="clientId" value="${esc(item.clientId)}" required>
                                <input name="clientSecret" type="password" placeholder="Leave blank to keep the current secret">
                                <label class="checkbox-line"><input name="useDiscovery" type="checkbox" ${item.useDiscovery ? "checked" : ""}> Use discovery</label>
                                <input name="discoveryUrl" value="${esc(item.discoveryUrl || "")}" placeholder="Discovery URL">
                                <input name="issuer" value="${esc(item.issuer || "")}" placeholder="Issuer">
                                <input name="authorizationEndpoint" value="${esc(item.authorizationEndpoint || "")}" placeholder="Authorization endpoint">
                                <input name="tokenEndpoint" value="${esc(item.tokenEndpoint || "")}" placeholder="Token endpoint">
                                <input name="userInfoEndpoint" value="${esc(item.userInfoEndpoint || "")}" placeholder="User info endpoint">
                                <input name="jwksUri" value="${esc(item.jwksUri || "")}" placeholder="JWKS URI">
                                <label class="checkbox-line"><input name="useUserInfo" type="checkbox" ${item.useUserInfo ? "checked" : ""}> Use user info endpoint</label>
                                <select name="clientAuthMethod">
                                    <option value="" ${!item.clientAuthMethod ? "selected" : ""}>Default</option>
                                    <option value="ClientSecretPost" ${item.clientAuthMethod === "ClientSecretPost" ? "selected" : ""}>ClientSecretPost</option>
                                    <option value="ClientSecretBasic" ${item.clientAuthMethod === "ClientSecretBasic" ? "selected" : ""}>ClientSecretBasic</option>
                                </select>
                                <input name="microsoftTenant" value="${esc(item.microsoftTenant || "")}" placeholder="Microsoft tenant">
                                <input name="appleTeamId" value="${esc(item.appleTeamId || "")}" placeholder="Apple team ID">
                                <input name="appleKeyId" value="${esc(item.appleKeyId || "")}" placeholder="Apple key ID">
                                <textarea name="applePrivateKeyPem" placeholder="Leave blank to keep the current Apple private key"></textarea>
                                <textarea name="allowedCallbackUris" required>${esc(parseJsonArray(item.allowedCallbackUris).join("\n"))}</textarea>
                                <textarea name="scopes">${esc(parseJsonArray(item.scopes).join("\n"))}</textarea>
                                <textarea name="claimMapping">${esc(JSON.stringify(parseJsonObject(item.claimMapping), null, 2))}</textarea>
                                <div class="actions">
                                    <button type="submit">Save</button>
                                    <button type="button" class="secondary" data-oidc-toggle="${esc(item.id)}" data-enabled="${item.isEnabled ? "true" : "false"}">
                                        ${item.isEnabled ? "Disable" : "Enable"}
                                    </button>
                                </div>
                            </form>
                        `,
                        "No OIDC connections yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-oidc-connection-form", async form => {
            await fetchJson(`${authApiBasePath}/oidc-connections`, {
                method: "POST",
                body: JSON.stringify(buildOidcPayload(form))
            });
            setFlash("success", "OIDC connection created.");
        });

        oidcConnections.forEach(item => {
            bindForm(`edit-oidc-${item.id}`, async form => {
                await fetchJson(`${authApiBasePath}/oidc-connections/${item.id}`, {
                    method: "PUT",
                    body: JSON.stringify(buildOidcPayload(form))
                });
                setFlash("success", "OIDC connection updated.");
            });
        });

        document.querySelectorAll("[data-oidc-toggle]").forEach(button => {
            button.addEventListener("click", async () => {
                try {
                    const connectionId = button.getAttribute("data-oidc-toggle");
                    const enabled = button.getAttribute("data-enabled") === "true";
                    await fetchJson(`${authApiBasePath}/oidc-connections/${connectionId}/${enabled ? "disable" : "enable"}`, {
                        method: "POST"
                    });
                    setFlash("success", enabled ? "OIDC connection disabled." : "OIDC connection enabled.");
                    await render();
                } catch (error) {
                    setFlash("error", error.message || String(error));
                    await render();
                }
            });
        });
    }

    async function renderAuthSso() {
        const config = authViews.sso;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading SSO data...");

        const ssoConnections = await fetchJson(`${authApiBasePath}/sso-connections`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                ${latestSsoDraft ? `
                    <section class="panel">
                        <h2>Latest Draft Output</h2>
                        <div class="callout">
                            <div><strong>Draft created:</strong> ${esc(latestSsoDraft.id)}</div>
                            <div><strong>SP Entity ID</strong><br><span class="inline-code">${esc(latestSsoDraft.serviceProviderEntityId)}</span></div>
                            <div><strong>ACS URL</strong><br><span class="inline-code">${esc(latestSsoDraft.assertionConsumerServiceUrl)}</span></div>
                            <div><strong>Org primary domain</strong><br>${esc(latestSsoDraft.primaryDomain || "Set the organization primary domain before enabling SSO.")}</div>
                            <div>After the Entra enterprise application is configured, paste the federation metadata XML below.</div>
                        </div>
                    </section>
                ` : ""}
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Create SSO Draft</h2>
                        <p>Create the org-scoped draft first, then import the customer's federation metadata XML.</p>
                        <form id="create-sso-draft-form">
                            <input name="organizationId" placeholder="Organization ID" required>
                            <input name="displayName" placeholder="Display name" required>
                            <input name="primaryDomain" placeholder="Primary domain (example.com)">
                            <label class="checkbox-row"><input type="checkbox" name="autoProvisionUsers" checked> Auto provision users</label>
                            <label class="checkbox-row"><input type="checkbox" name="autoLinkByEmail"> Auto link by email</label>
                            <button type="submit">Create SSO draft</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Import Entra Metadata</h2>
                        <p>Paste the federation metadata XML returned by the customer's Entra admin.</p>
                        <form id="import-sso-metadata-form">
                            <input name="connectionId" placeholder="Connection ID" required>
                            <textarea name="metadataXml" placeholder="Paste the Entra federation metadata XML" required></textarea>
                            <button type="submit">Import metadata</button>
                        </form>
                    </section>
                </div>
                <section class="panel">
                    <h2>SAML Connections</h2>
                    ${renderList(
                        ssoConnections,
                        item => `
                            <strong>${esc(item.displayName)}</strong>
                            ${renderMetadataRows([
                                { label: "Connection ID", value: item.id },
                                { label: "Organization", value: item.organization },
                                { label: "Primary domain", value: item.primaryDomain || "n/a" },
                                { label: "Status", value: `${item.setupStatus} | Enabled: ${item.isEnabled}` },
                                { label: "SP Entity ID", value: item.serviceProviderEntityId },
                                { label: "ACS URL", value: item.assertionConsumerServiceUrl }
                            ])}
                        `,
                        "No SSO connections yet."
                    )}
                </section>
            </div>
        `;

        bindForm("create-sso-draft-form", async form => {
            const result = await fetchJson(`${authApiBasePath}/sso-connections/draft`, {
                method: "POST",
                body: JSON.stringify({
                    organizationId: form.get("organizationId"),
                    displayName: form.get("displayName"),
                    primaryDomain: form.get("primaryDomain") || null,
                    autoProvisionUsers: form.get("autoProvisionUsers") === "on",
                    autoLinkByEmail: form.get("autoLinkByEmail") === "on"
                })
            });

            latestSsoDraft = {
                ...result,
                primaryDomain: form.get("primaryDomain") || null
            };
            setFlash("success", "SSO draft created.");
        });

        bindForm("import-sso-metadata-form", async form => {
            await fetchJson(`${authApiBasePath}/sso-connections/${form.get("connectionId")}/metadata`, {
                method: "POST",
                body: JSON.stringify({
                    metadataXml: form.get("metadataXml")
                })
            });
            setFlash("success", "Federation metadata imported.");
        });
    }

    async function renderAuthSecurity() {
        const config = authViews.security;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading security settings...");

        const settings = await fetchJson(`${authApiBasePath}/settings/security`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Security Settings</h2>
                    <p>These values drive refresh token lifetime and session expiry behavior across the auth server.</p>
                    <form id="security-settings-form">
                        <input name="refreshTokenLifetimeMinutes" type="number" min="1" placeholder="Refresh token lifetime (minutes)" value="${esc(settings.refreshTokenLifetimeMinutes)}" required>
                        <input name="sessionIdleTimeoutMinutes" type="number" min="1" placeholder="Session idle timeout (minutes)" value="${esc(settings.sessionIdleTimeoutMinutes)}" required>
                        <input name="sessionAbsoluteLifetimeMinutes" type="number" min="1" placeholder="Session absolute lifetime (minutes)" value="${esc(settings.sessionAbsoluteLifetimeMinutes)}" required>
                        <button type="submit">Save settings</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Current Values</h2>
                    ${renderMetadataRows([
                        { label: "Refresh token lifetime", value: `${settings.refreshTokenLifetimeMinutes} minutes` },
                        { label: "Idle timeout", value: `${settings.sessionIdleTimeoutMinutes} minutes` },
                        { label: "Absolute lifetime", value: `${settings.sessionAbsoluteLifetimeMinutes} minutes` }
                    ])}
                </section>
            </div>
        `;

        bindForm("security-settings-form", async form => {
            await fetchJson(`${authApiBasePath}/settings/security`, {
                method: "PUT",
                body: JSON.stringify({
                    refreshTokenLifetimeMinutes: Number(form.get("refreshTokenLifetimeMinutes")),
                    sessionIdleTimeoutMinutes: Number(form.get("sessionIdleTimeoutMinutes")),
                    sessionAbsoluteLifetimeMinutes: Number(form.get("sessionAbsoluteLifetimeMinutes"))
                })
            });
            setFlash("success", "Security settings saved.");
        });
    }

    async function renderAuthSessions() {
        const config = authViews.sessions;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading sessions...");

        const sessions = await fetchJson(`${authApiBasePath}/sessions`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel">
                <h2>Sessions</h2>
                ${renderList(
                    sessions,
                    item => `
                        <strong>${esc(item.user)}</strong>
                        ${renderMetadataRows([
                            { label: "Session ID", value: item.id },
                            { label: "Authentication", value: item.authenticationMethod || "unknown" },
                            { label: "Client", value: item.clientApplicationId || "n/a" },
                            { label: "Created", value: formatDate(item.createdAt) },
                            { label: "Idle expires", value: formatDate(item.idleExpiresAt) },
                            { label: "Absolute expires", value: formatDate(item.absoluteExpiresAt) }
                        ])}
                    `,
                    "No sessions yet."
                )}
            </section>
        `;
    }

    async function renderAuthAudit() {
        const config = authViews.audit;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading audit events...");

        const auditEvents = await fetchJson(`${authApiBasePath}/audit-events`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel">
                <h2>Audit Events</h2>
                ${renderList(
                    auditEvents,
                    item => `
                        <strong>${esc(item.eventType)}</strong>
                        ${renderMetadataRows([
                            { label: "Occurred", value: formatDate(item.occurredAt) },
                            { label: "Actor", value: `${item.actorType}: ${item.actorId || "n/a"}` },
                            { label: "Entity", value: item.entityId ? `${item.entityType}: ${item.entityId}` : "n/a" }
                        ])}
                    `,
                    "No audit events yet."
                )}
            </section>
        `;
    }

    async function renderFgaRoute(view) {
        const config = fgaViews[view] || fgaViews.resources;
        setHeader("Fine-Grained Auth", config.title, config.description);
        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="embed-shell">
                <iframe class="embed-frame" src="${fgaDashboardPath}/?embed=1#${config.hash}" title="${esc(config.title)}"></iframe>
            </div>
        `;
    }

    function bindForm(formId, handler) {
        const form = document.getElementById(formId);
        if (!form) {
            return;
        }

        form.addEventListener("submit", async (event) => {
            event.preventDefault();

            try {
                await handler(new FormData(form));
                await render();
            } catch (error) {
                setFlash("error", error.message || String(error));
                await render();
            }
        });
    }
})();
