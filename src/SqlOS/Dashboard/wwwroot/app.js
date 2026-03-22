(function () {
    const dashboardBasePath = normalizeBasePath(window.__SQL_OS_BASE_PATH__ || "/sqlos");
    const dashboardAuthBasePath = `${dashboardBasePath}/dashboard-auth`;
    const authServerBasePath = `${dashboardBasePath}/auth`;
    const authDashboardPath = `${dashboardBasePath}/admin/auth`;
    const fgaDashboardPath = `${dashboardBasePath}/admin/fga`;
    const authApiBasePath = `${authDashboardPath}/api`;
    const fgaApiBasePath = `${fgaDashboardPath}/api`;

    const content = document.getElementById("content");
    const pageEyebrow = document.getElementById("page-eyebrow");
    const pageTitle = document.getElementById("page-title");
    const pageDescription = document.getElementById("page-description");
    const topbarTitle = document.getElementById("topbar-title");
    const logoutButton = document.getElementById("dashboard-logout");

    let flashMessage = null;
    let latestSsoDraft = null;
    const pagerState = new Map();

    const authViews = {
        overview: { title: "Auth Server", description: "Organizations, users, sessions, clients, and security settings." },
        organizations: { title: "Organizations", description: "Create and manage organizations and their primary domains." },
        users: { title: "Users", description: "Create users and bootstrap password credentials." },
        memberships: { title: "Memberships", description: "Assign users to organizations and manage roles." },
        clients: { title: "Clients", description: "Register clients, audiences, and redirect URIs." },
        oidc: { title: "Social Login", description: "Configure Google, Microsoft, Apple, and custom OIDC providers for authserver-owned social login." },
        security: { title: "Security", description: "Tune refresh, idle, and absolute session lifetimes." },
        authpage: { title: "Auth Page", description: "Brand the hosted authorization page and publish the login, signup, and PKCE endpoints your app exposes." },
        sessions: { title: "Sessions", description: "Inspect active sessions and authentication methods." },
        audit: { title: "Audit Events", description: "Review recent auth and admin activity." }
    };
    const oidcProviderGuideTemplates = {
        Google: {
            heading: "Google Setup",
            description: "Create a Google OAuth Web app and wire its redirect URI to SqlOS for social login.",
            docsLabel: "Google credentials",
            docsUrl: "https://console.cloud.google.com/apis/credentials",
            steps: [
                "In Google Cloud Console, create or open an OAuth 2.0 Web client.",
                "Add this callback URI: {callback}.",
                "Copy Client ID + Client Secret from Google into SqlOS, then save the connection.",
                "Keep discovery enabled so SqlOS reads discovery, scopes, and endpoints automatically."
            ],
            rows: [
                { label: "Provider type", value: "Google" },
                { label: "Discovery", value: "On (recommended)" },
                { label: "Discovery URL", value: "https://accounts.google.com/.well-known/openid-configuration" },
                { label: "User info", value: "Automatic from discovery" },
                { label: "Provider callback URI", html: "<div class=\"inline-code\">{callback}</div>" },
                { label: "Suggested scopes", value: "openid, profile, email" },
                { label: "Claim mapping", value: "Default mapping is usually enough" }
            ],
            integration: "After enabling, your app should call <span class=\"inline-code\">GET /sqlos/auth/oidc/providers</span>, then request an authorization URL with <span class=\"inline-code\">POST /sqlos/auth/oidc/authorization-url</span> and exchange the callback code with <span class=\"inline-code\">POST /sqlos/auth/oidc/exchange</span>."
        },
        Microsoft: {
            heading: "Microsoft Entra Setup",
            description: "Register an Entra app, set redirect URI and authority, then let SqlOS connect it for social login.",
            docsLabel: "Azure app registration",
            docsUrl: "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade",
            steps: [
                "Create or open an App Registration in Entra ID.",
                "Under Authentication, add this Web redirect URI: {callback}.",
                "Generate a client secret and copy the client id/secret into SqlOS, then save the connection.",
                "Set tenant to specific directory id if you want tenant locked sign-in."
            ],
            rows: [
                { label: "Provider type", value: "Microsoft" },
                { label: "Discovery", value: "On (recommended)" },
                { label: "Discovery URL", value: "https://login.microsoftonline.com/{tenant-id}/v2.0/.well-known/openid-configuration" },
                { label: "Tenant", value: "Common or specific tenant ID" },
                { label: "Provider callback URI", html: "<div class=\"inline-code\">{callback}</div>" },
                { label: "Scopes", value: "openid, profile, email" }
            ],
            integration: "Use the authserver-owned callback URI in Entra, then let your app start login through the SqlOS social login endpoints."
        },
        Apple: {
            heading: "Apple Setup",
            description: "Create a Services ID and Sign in with Apple key pair, then attach key material in SqlOS.",
            docsLabel: "Apple identifier setup",
            docsUrl: "https://developer.apple.com/account/resources/identifiers/list/serviceId",
            steps: [
                "Create a Service ID in Apple Developer and enable Sign in with Apple.",
                "Upload your .p8 private key and note Team ID and Key ID.",
                "Add callback URL in Service ID settings: {callback} (must be HTTPS).",
                "Set provider to Apple and paste Team ID, Key ID, and key PEM into SqlOS, then save the connection."
            ],
            rows: [
                { label: "Provider type", value: "Apple" },
                { label: "Discovery", value: "On (recommended)" },
                { label: "Discovery URL", value: "https://appleid.apple.com/.well-known/openid-configuration" },
                { label: "Required fields", value: "Team ID, Key ID, Apple private key (.p8)" },
                { label: "Callback requirement", value: "Public HTTPS callback URL required" },
                { label: "Provider callback URI", html: "<div class=\"inline-code\">{callback}</div>" }
            ],
            integration: "Apple redirects back to SqlOS, then SqlOS redirects back to your app callback with the final code."
        },
        Custom: {
            heading: "Custom OIDC Setup",
            description: "Use discovery when possible, otherwise configure all endpoints manually.",
            docsLabel: "OIDC discovery spec",
            docsUrl: "https://datatracker.ietf.org/doc/html/rfc8414",
            steps: [
                "Enable discovery and set a valid metadata URL if the provider exposes one.",
                "If discovery is not available, disable it and complete Issuer / endpoints manually.",
                "Add callback URLs to include the SqlOS callback: {callback}.",
                "Update claim mapping only when the provider uses non-standard claim names, then save the connection."
            ],
            rows: [
                { label: "Provider type", value: "Custom" },
                { label: "Discovery", value: "On if supported, otherwise Manual" },
                { label: "Provider callback URI", html: "<div class=\"inline-code\">{callback}</div>" },
                { label: "Sample claim mapping", value: "{\"SubjectClaim\":\"sub\",\"EmailClaim\":\"email\"}" },
                { label: "Best practice", value: "Prefer discovery and keep user info enabled unless the provider blocks it." }
            ],
            integration: "Register the fixed SqlOS callback URI with the provider, then let your app use the SqlOS social login endpoints to start and complete the flow."
        }
    };

    const organizationTabs = new Set(["general", "users", "sso"]);
    const userTabs = new Set(["general", "organizations", "sessions"]);

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

    logoutButton?.addEventListener("click", async () => {
        try {
            await fetchJson(`${dashboardAuthBasePath}/logout`, {
                method: "POST",
                skipUnauthorizedRedirect: true
            });
        } catch {
            // Ignore logout errors and force a clean login navigation.
        }

        window.location.href = `${dashboardBasePath}/login`;
    });

    render();

    function normalizeBasePath(value) {
        if (!value || value === "/") {
            return "";
        }

        return value.endsWith("/") ? value.slice(0, -1) : value;
    }

    function fetchJson(url, options = {}) {
        const { skipUnauthorizedRedirect, ...requestOptions } = options;
        return fetch(url, {
            ...requestOptions,
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                ...(requestOptions.headers || {})
            }
        }).then(async response => {
            if (response.status === 401 && !skipUnauthorizedRedirect) {
                redirectToLogin();
            }

            if (!response.ok) {
                const text = await response.text();
                const error = new Error(text || `${response.status}`);
                error.status = response.status;
                throw error;
            }

            return response.status === 204 ? null : response.json();
        });
    }

    function redirectToLogin() {
        const loginPath = `${dashboardBasePath}/login`;
        if (window.location.pathname === loginPath) {
            return;
        }

        const next = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
        window.location.href = `${loginPath}?next=${next}`;
    }

    function resolveNextPath() {
        const params = new URLSearchParams(window.location.search);
        const nextRaw = params.get("next");
        if (!nextRaw) {
            return `${dashboardBasePath}/`;
        }

        try {
            const parsed = new URL(nextRaw, window.location.origin);
            if (parsed.origin !== window.location.origin) {
                return `${dashboardBasePath}/`;
            }

            if (!parsed.pathname.startsWith(dashboardBasePath || "/")) {
                return `${dashboardBasePath}/`;
            }

            return `${parsed.pathname}${parsed.search}`;
        } catch {
            return `${dashboardBasePath}/`;
        }
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

    function organizationDetailPath(organizationId, tab = "general") {
        const normalizedTab = organizationTabs.has(tab) ? tab : "general";
        return `${authDashboardPath}/organizations/${encodeURIComponent(organizationId)}/${normalizedTab}`;
    }

    function userDetailPath(userId, tab = "general") {
        const normalizedTab = userTabs.has(tab) ? tab : "general";
        return `${authDashboardPath}/users/${encodeURIComponent(userId)}/${normalizedTab}`;
    }

    function decodeRouteSegment(value) {
        try {
            return decodeURIComponent(value);
        } catch {
            return value;
        }
    }

    function currentRoute() {
        const pathname = window.location.pathname;
        const relativePath = pathname.startsWith(dashboardBasePath)
            ? pathname.slice(dashboardBasePath.length)
            : pathname;
        const trimmed = relativePath.replace(/^\/+|\/+$/g, "");

        if (trimmed === "login") {
            return { kind: "login", key: "", canonicalPath: `${dashboardBasePath}/login` };
        }

        if (!trimmed) {
            return { kind: "home", key: "home", canonicalPath: `${dashboardBasePath}/` };
        }

        const segments = trimmed.split("/");
        if (segments[0] !== "admin") {
            return { kind: "home", key: "home", canonicalPath: `${dashboardBasePath}/` };
        }

        if (segments[1] === "auth") {
            const view = authViews[segments[2]] ? segments[2] : "overview";
            if (view === "organizations" && segments[3]) {
                const organizationId = decodeRouteSegment(segments[3]);
                const organizationTab = organizationTabs.has(segments[4]) ? segments[4] : "general";
                return {
                    kind: "auth",
                    view,
                    organizationId,
                    organizationTab,
                    key: "auth-organizations",
                    canonicalPath: organizationDetailPath(organizationId, organizationTab)
                };
            }

            if (view === "users" && segments[3]) {
                const userId = decodeRouteSegment(segments[3]);
                const userTab = userTabs.has(segments[4]) ? segments[4] : "general";
                return {
                    kind: "auth",
                    view,
                    userId,
                    userTab,
                    key: "auth-users",
                    canonicalPath: userDetailPath(userId, userTab)
                };
            }

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

    function setLoginMode(enabled) {
        document.body.classList.toggle("login-mode", enabled);
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

    function normalizeOidcProviderType(value) {
        const raw = String(value || "Custom").toLowerCase();
        if (raw === "google") {
            return "Google";
        }

        if (raw === "microsoft") {
            return "Microsoft";
        }

        if (raw === "apple") {
            return "Apple";
        }

        return "Custom";
    }

    function getMonogram(value) {
        const words = String(value || "")
            .trim()
            .split(/\s+/)
            .filter(Boolean);

        if (words.length === 0) {
            return "?";
        }

        return words
            .slice(0, 2)
            .map(word => word.charAt(0).toUpperCase())
            .join("");
    }

    function renderOidcProviderLogo(logoDataUrl, displayName, className = "oidc-provider-logo") {
        const baseClass = esc(className);
        if (logoDataUrl) {
            return `
                <span class="${baseClass}" aria-hidden="true">
                    <img src="${esc(logoDataUrl)}" alt="">
                </span>
            `;
        }

        return `
            <span class="${baseClass} oidc-provider-logo--fallback" aria-hidden="true">
                ${esc(getMonogram(displayName))}
            </span>
        `;
    }

    function bindDataUrlFileInputs(root = document) {
        root.querySelectorAll("input[type=\"file\"][data-dataurl-target]").forEach(input => {
            if (input.dataset.logoBound === "true") {
                return;
            }

            input.dataset.logoBound = "true";
            input.addEventListener("change", () => {
                const file = input.files?.[0];
                const targetName = input.getAttribute("data-dataurl-target");
                const form = input.form;
                if (!file || !targetName || !form || !form.elements[targetName]) {
                    return;
                }

                const reader = new FileReader();
                reader.onload = () => {
                    form.elements[targetName].value = String(reader.result || "");
                };
                reader.readAsDataURL(file);
            });
        });
    }

    function renderOidcProviderGuide(providerType, callbackTemplate) {
        const normalized = normalizeOidcProviderType(providerType);
        const callbackUri = callbackTemplate || `${window.location.origin}/api/v1/auth/oidc/callback`;
        const callback = esc(callbackUri);
        const template = oidcProviderGuideTemplates[normalized] || oidcProviderGuideTemplates.Custom;
        const renderedRows = (template.rows || []).map((row) => ({
            ...row,
            value: row.value ? row.value.replaceAll("{tenant-id}", "your tenant id") : row.value,
            html: row.html ? row.html.replaceAll("{callback}", callback) : row.html
        }));
        const steps = template.steps || [];

        return `
            <div class="provider-guide">
                <div class="provider-guide-header">
                    <div>
                        <h3>${esc(template.heading || "OIDC Setup")}</h3>
                        <p>${esc(template.description || "Follow provider-specific social login setup steps and register this app with SqlOS.")}</p>
                    </div>
                    <a class="inline-link" href="${esc(template.docsUrl || "#")}" target="_blank" rel="noreferrer">${esc(template.docsLabel || "Read docs")}</a>
                </div>
                <ol class="provider-guide-steps">
                    ${steps.map(step => `<li>${step.replaceAll("{callback}", callback)}</li>`).join("")}
                </ol>
                <div class="provider-guide-grid">
                    ${renderMetadataRows(renderedRows)}
                    <div class="callout">
                        <strong>Social login integration:</strong> ${template.integration || "Enable the connection and point your app at SqlOS OIDC start endpoint for auth."}
                    </div>
                    <div class="callout">
                        <strong>Callback URI:</strong> This route is stable for the environment and does not depend on a generated connection ID.
                    </div>
                </div>
            </div>
        `;
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
            applePrivateKeyPem: form.get("applePrivateKeyPem") || null,
            logoDataUrl: form.get("logoDataUrl") || null
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

    function getPagerState(key, defaultPageSize = 10) {
        if (!pagerState.has(key)) {
            pagerState.set(key, { page: 1, pageSize: defaultPageSize });
        }

        return pagerState.get(key);
    }

    function setPagerPage(key, page) {
        const current = getPagerState(key);
        current.page = Math.max(1, page);
    }

    function renderPagination(page, totalPages, totalCount) {
        const safePage = Math.max(1, page || 1);
        const safeTotalPages = Math.max(1, totalPages || 1);
        let html = '<div class="pagination">';
        html += `<button class="pg-btn" data-page="${safePage - 1}" ${safePage <= 1 ? 'disabled' : ''}>Prev</button>`;

        const pages = buildPageNumbers(safePage, safeTotalPages);
        pages.forEach(item => {
            if (item === "...") {
                html += '<span class="pg-ellipsis">...</span>';
            } else {
                html += `<button class="pg-btn ${item === safePage ? 'pg-active' : ''}" data-page="${item}">${item}</button>`;
            }
        });

        html += `<button class="pg-btn" data-page="${safePage + 1}" ${safePage >= safeTotalPages ? 'disabled' : ''}>Next</button>`;
        html += `<span class="pg-info">${totalCount ?? 0} item${(totalCount ?? 0) === 1 ? "" : "s"}</span>`;
        html += '</div>';
        return html;
    }

    function buildPageNumbers(current, total) {
        if (total <= 7) {
            return Array.from({ length: total }, (_, index) => index + 1);
        }

        const pages = [1];
        if (current > 3) {
            pages.push("...");
        }

        for (let page = Math.max(2, current - 1); page <= Math.min(total - 1, current + 1); page += 1) {
            pages.push(page);
        }

        if (current < total - 2) {
            pages.push("...");
        }

        pages.push(total);
        return pages;
    }

    function bindPagination(containerSelector, callback) {
        document.querySelectorAll(`${containerSelector} .pg-btn:not([disabled])`).forEach(button => {
            button.addEventListener("click", () => callback(Number(button.dataset.page)));
        });
    }

    async function render() {
        const route = currentRoute();
        if (window.location.pathname !== route.canonicalPath) {
            history.replaceState({}, "", route.canonicalPath);
        }

        setLoginMode(route.kind === "login");
        updateActiveNav(route.key);

        try {
            if (route.kind === "login") {
                await renderLoginRoute();
                return;
            }

            if (route.kind === "home") {
                await renderHome();
                return;
            }

            if (route.kind === "auth") {
                await renderAuthRoute(route);
                return;
            }

            await renderFgaRoute(route.view);
        } catch (error) {
            content.innerHTML = `${consumeFlashHtml()}<div class="error-banner">${esc(error.message || String(error))}</div>`;
        }
    }

    async function renderLoginRoute() {
        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="login-card">
                <h2>Dashboard login</h2>
                <p>Enter the dashboard password to continue.</p>
                <form id="dashboard-login-form" class="login-form">
                    <input name="password" type="password" autocomplete="current-password" placeholder="Dashboard password" required>
                    <button type="submit">Sign in</button>
                </form>
                <div id="dashboard-login-error" class="error-banner" style="display:none;"></div>
                <div class="login-help">The password is configured by the host app and validated server-side.</div>
            </section>
        `;

        try {
            const session = await fetchJson(`${dashboardAuthBasePath}/session`, { skipUnauthorizedRedirect: true });
            if (session?.authenticated) {
                window.location.href = resolveNextPath();
                return;
            }
        } catch {
            // Ignore session probes and keep the login form available.
        }

        const form = document.getElementById("dashboard-login-form");
        const errorElement = document.getElementById("dashboard-login-error");
        form?.addEventListener("submit", async event => {
            event.preventDefault();

            const payload = new FormData(form);
            const password = String(payload.get("password") || "");
            if (!password.trim()) {
                if (errorElement) {
                    errorElement.textContent = "Password is required.";
                    errorElement.style.display = "block";
                }
                return;
            }

            try {
                await fetchJson(`${dashboardAuthBasePath}/login`, {
                    method: "POST",
                    body: JSON.stringify({ password }),
                    skipUnauthorizedRedirect: true
                });
                window.location.href = resolveNextPath();
            } catch (error) {
                if (errorElement) {
                    errorElement.textContent = error.status === 401
                        ? "Invalid password."
                        : (error.message || "Could not sign in.");
                    errorElement.style.display = "block";
                }
            }
        });
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
                    <p>Use the direct routes for organizations, clients, sessions, and security settings.</p>
                    <div class="link-list">
                        ${quickLink("auth-organizations", "Organizations")}
                        ${quickLink("auth-users", "Users")}
                        ${quickLink("auth-oidc", "OIDC")}
                        ${quickLink("auth-security", "Security")}
                        ${quickLink("auth-authpage", "Auth Page")}
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

    async function renderAuthRoute(route) {
        const view = route.view;
        if (view === "overview") {
            await renderAuthOverview();
            return;
        }

        if (view === "organizations") {
            if (route.organizationId) {
                await renderAuthOrganizationDetail(route.organizationId, route.organizationTab || "general");
            } else {
                await renderAuthOrganizations();
            }
            return;
        }

        if (view === "users") {
            if (route.userId) {
                await renderAuthUserDetail(route.userId, route.userTab || "general");
            } else {
                await renderAuthUsers();
            }
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

        if (view === "security") {
            await renderAuthSecurity();
            return;
        }

        if (view === "authpage") {
            await renderAuthPage();
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

        const [stats, settings] = await Promise.all([
            fetchJson(`${authApiBasePath}/stats`),
            fetchJson(`${authApiBasePath}/settings/security`)
        ]);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                ${renderStatsGroup("Auth Server Overview", stats, [
                    { key: "organizations", label: "Organizations" },
                    { key: "users", label: "Users" },
                    { key: "clients", label: "Clients" },
                    { key: "oidcConnections", label: "OIDC Connections" },
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

        const pager = getPagerState("auth-organizations");
        const organizations = await fetchJson(`${authApiBasePath}/organizations?page=${pager.page}&pageSize=${pager.pageSize}`);

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
                    <div class="panel-actions">
                        <h2>Organizations</h2>
                        <div id="organizations-pagination-top">${renderPagination(organizations.page, organizations.totalPages, organizations.totalCount)}</div>
                    </div>
                    ${renderList(
                        organizations.data,
                        item => `
                            <div class="list-item-header">
                                <strong>${esc(item.name)}</strong>
                                <a class="inline-link" href="${esc(organizationDetailPath(item.id, "general"))}">Open</a>
                            </div>
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

        bindPagination("#organizations-pagination-top", async page => {
            setPagerPage("auth-organizations", page);
            await render();
        });
    }

    async function renderAuthOrganizationDetail(organizationId, tab) {
        const config = authViews.organizations;
        setHeader("Auth Server", config.title, "Manage organization details, memberships, and SSO in one place.");
        renderLoading("Loading organization details...");

        const usersPager = getPagerState(`auth-org-${organizationId}-users`);
        const ssoPager = getPagerState(`auth-org-${organizationId}-sso`);
        const [organization, users, memberships, ssoConnections] = await Promise.all([
            fetchJson(`${authApiBasePath}/organizations/${organizationId}`),
            fetchJson(`${authApiBasePath}/users?page=1&pageSize=500`),
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/memberships?page=${usersPager.page}&pageSize=${usersPager.pageSize}`),
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/sso-connections?page=${ssoPager.page}&pageSize=${ssoPager.pageSize}`)
        ]);
        const organizationSsoConnections = Array.isArray(ssoConnections?.data) ? ssoConnections.data : [];
        const latestOrganizationDraft = latestSsoDraft && latestSsoDraft.organizationId === organizationId ? latestSsoDraft : null;

        const summaryHtml = `
            <div class="detail-summary-grid">
                <div class="summary-card">
                    <div class="summary-label">Primary domain</div>
                    <div class="summary-value">${esc(organization.primaryDomain || "n/a")}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Members</div>
                    <div class="summary-value">${esc(organization.membershipCount || memberships.totalCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">SSO connections</div>
                    <div class="summary-value">${esc(organization.ssoConnectionCount || ssoConnections.totalCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Enabled SSO</div>
                    <div class="summary-value">${esc(organization.enabledSsoConnections ?? 0)}</div>
                </div>
            </div>
        `;

        const tabNav = `
            <div class="tab-strip">
                ${renderTabLink("general", "General", tab, organizationId)}
                ${renderTabLink("users", "Users", tab, organizationId)}
                ${renderTabLink("sso", "SSO", tab, organizationId)}
            </div>
        `;

        let tabContent = "";
        if (tab === "general") {
            tabContent = `
                <div class="panel-grid">
                    <section class="panel">
                        <div class="panel-actions">
                            <div>
                                <h2>General Info</h2>
                                <p>Update the organization profile and primary login domain.</p>
                            </div>
                            <a class="inline-link" href="${esc(pathForRoute("auth-organizations"))}">Back to organizations</a>
                        </div>
                        <form id="update-org-form">
                            <input name="name" value="${esc(organization.name)}" placeholder="Organization name" required>
                            <input name="slug" value="${esc(organization.slug)}" placeholder="Slug">
                            <input name="primaryDomain" value="${esc(organization.primaryDomain || "")}" placeholder="Primary domain">
                            <label class="checkbox-row"><input name="isActive" type="checkbox" ${organization.isActive ? "checked" : ""}> Organization is active</label>
                            <button type="submit">Save organization</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Organization Summary</h2>
                        ${renderMetadataRows([
                            { label: "ID", value: organization.id },
                            { label: "Slug", value: organization.slug },
                            { label: "Primary domain", value: organization.primaryDomain || "n/a" },
                            { label: "Active", value: organization.isActive ? "Yes" : "No" },
                            { label: "Members", value: organization.membershipCount || memberships.totalCount || 0 },
                            { label: "Enabled SSO", value: organization.enabledSsoConnections ?? 0 }
                        ])}
                    </section>
                </div>
            `;
        } else if (tab === "users") {
            tabContent = `
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Add User To Organization</h2>
                        <p>Create or update a membership for this organization.</p>
                        <form id="create-org-membership-form">
                            <select name="userId" required>
                                <option value="">Select a user</option>
                                ${users.data.map(user => `<option value="${esc(user.id)}">${esc(user.displayName)}${user.defaultEmail ? ` (${esc(user.defaultEmail)})` : ""}</option>`).join("")}
                            </select>
                            <input name="role" placeholder="Role" value="member" required>
                            <button type="submit">Add membership</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Organization Users</h2>
                        <div id="organization-users-pagination-top">${renderPagination(memberships.page, memberships.totalPages, memberships.totalCount)}</div>
                        ${renderList(
                            memberships.data,
                            item => `
                                <div class="list-item-header">
                                    <strong>${esc(item.user)}</strong>
                                    <a class="inline-link" href="${esc(userDetailPath(item.userId, "general"))}">Open</a>
                                </div>
                                ${renderMetadataRows([
                                    { label: "User ID", value: item.userId },
                                    { label: "Email", value: item.userEmail || "n/a" },
                                    { label: "Role", value: item.role },
                                    { label: "Active", value: item.isActive ? "Yes" : "No" }
                                ])}
                            `,
                            "No memberships yet."
                        )}
                    </section>
                </div>
            `;
        } else {
            tabContent = `
                <div class="panel-stack">
                    ${latestOrganizationDraft ? `
                        <section class="panel">
                            <h2>Latest Draft Output</h2>
                            <div class="callout">
                                <div><strong>Draft created:</strong> ${esc(latestOrganizationDraft.id)}</div>
                                <div><strong>SP Entity ID</strong><br><span class="inline-code">${esc(latestOrganizationDraft.serviceProviderEntityId)}</span></div>
                                <div><strong>ACS URL</strong><br><span class="inline-code">${esc(latestOrganizationDraft.assertionConsumerServiceUrl)}</span></div>
                                <div><strong>Primary domain</strong><br>${esc(latestOrganizationDraft.primaryDomain || organization.primaryDomain || "Set the organization primary domain before enabling SSO.")}</div>
                            </div>
                        </section>
                    ` : ""}
                    <div class="panel-grid">
                        <section class="panel">
                            <h2>Create SSO Draft</h2>
                            <p>Create the SAML draft directly from this organization, then import Entra metadata on the resulting connection.</p>
                            <form id="create-org-sso-draft-form">
                                <input name="displayName" placeholder="Display name" value="${esc(organization.name)} SSO" required>
                                <input name="primaryDomain" placeholder="Primary domain" value="${esc(organization.primaryDomain || "")}">
                                <label class="checkbox-row"><input type="checkbox" name="autoProvisionUsers" checked> Auto provision users</label>
                                <label class="checkbox-row"><input type="checkbox" name="autoLinkByEmail"> Auto link by email</label>
                                <button type="submit">Create SSO draft</button>
                            </form>
                        </section>
                        <section class="panel">
                            <h2>Current SSO State</h2>
                            ${renderMetadataRows([
                                { label: "Primary domain", value: organization.primaryDomain || "n/a" },
                                { label: "Total connections", value: organization.ssoConnectionCount || ssoConnections.totalCount || 0 },
                                { label: "Enabled connections", value: organization.enabledSsoConnections ?? 0 }
                            ])}
                        </section>
                    </div>
                    <section class="panel">
                        <h2>Organization SSO Connections</h2>
                        <div id="organization-sso-pagination-top">${renderPagination(ssoConnections.page, ssoConnections.totalPages, ssoConnections.totalCount)}</div>
                        ${renderList(
                            organizationSsoConnections,
                            item => `
                                <div class="list-item-header">
                                    <strong>${esc(item.displayName)}</strong>
                                    <span class="inline-code">${esc(item.setupStatus)}</span>
                                </div>
                                ${renderMetadataRows([
                                    { label: "Connection ID", value: item.id },
                                    { label: "Primary domain", value: item.primaryDomain || "n/a" },
                                    { label: "Enabled", value: item.isEnabled ? "Yes" : "No" },
                                    { label: "SP Entity ID", value: item.serviceProviderEntityId },
                                    { label: "ACS URL", value: item.assertionConsumerServiceUrl }
                                ])}
                                <form id="import-sso-metadata-${esc(item.id)}" class="nested-form">
                                    <textarea name="metadataXml" placeholder="Paste the Entra federation metadata XML" required></textarea>
                                    <button type="submit">Import metadata</button>
                                </form>
                            `,
                            "No SSO connections yet."
                        )}
                    </section>
                </div>
            `;
        }

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel detail-hero">
                <div class="panel-actions">
                    <div>
                        <div class="page-eyebrow">Organization Detail</div>
                        <h2>${esc(organization.name)}</h2>
                        <p>Manage the organization profile, memberships, and SAML SSO from one detail view.</p>
                    </div>
                    <a class="inline-link" href="${esc(pathForRoute("auth-organizations"))}">All organizations</a>
                </div>
                ${summaryHtml}
                ${tabNav}
            </section>
            ${tabContent}
        `;

        if (tab === "general") {
            bindForm("update-org-form", async form => {
                await fetchJson(`${authApiBasePath}/organizations/${organizationId}`, {
                    method: "PUT",
                    body: JSON.stringify({
                        name: form.get("name"),
                        slug: form.get("slug") || null,
                        primaryDomain: form.get("primaryDomain") || null,
                        isActive: form.get("isActive") === "on"
                    })
                });
                setFlash("success", "Organization updated.");
            });
        } else if (tab === "users") {
            bindForm("create-org-membership-form", async form => {
                await fetchJson(`${authApiBasePath}/organizations/${organizationId}/memberships`, {
                    method: "POST",
                    body: JSON.stringify({
                        userId: form.get("userId"),
                        role: form.get("role") || "member"
                    })
                });
                setFlash("success", "Organization membership saved.");
            });

            bindPagination("#organization-users-pagination-top", async page => {
                setPagerPage(`auth-org-${organizationId}-users`, page);
                await render();
            });
        } else {
            bindForm("create-org-sso-draft-form", async form => {
                const result = await fetchJson(`${authApiBasePath}/sso-connections/draft`, {
                    method: "POST",
                    body: JSON.stringify({
                        organizationId,
                        displayName: form.get("displayName"),
                        primaryDomain: form.get("primaryDomain") || null,
                        autoProvisionUsers: form.get("autoProvisionUsers") === "on",
                        autoLinkByEmail: form.get("autoLinkByEmail") === "on"
                    })
                });

                latestSsoDraft = {
                    ...result,
                    organizationId,
                    primaryDomain: form.get("primaryDomain") || organization.primaryDomain || null
                };
                setFlash("success", "SSO draft created.");
            });

            organizationSsoConnections.forEach(item => {
                bindForm(`import-sso-metadata-${item.id}`, async form => {
                    await fetchJson(`${authApiBasePath}/sso-connections/${item.id}/metadata`, {
                        method: "POST",
                        body: JSON.stringify({
                            metadataXml: form.get("metadataXml")
                        })
                    });
                    setFlash("success", "Federation metadata imported.");
                });
            });

            bindPagination("#organization-sso-pagination-top", async page => {
                setPagerPage(`auth-org-${organizationId}-sso`, page);
                await render();
            });
        }
    }

    function renderTabLink(tab, label, activeTab, organizationId) {
        const activeClass = tab === activeTab ? "active" : "";
        return `<a class="tab-link ${activeClass}" href="${esc(organizationDetailPath(organizationId, tab))}">${esc(label)}</a>`;
    }

    async function renderAuthUsers() {
        const config = authViews.users;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading users...");

        const pager = getPagerState("auth-users");
        const users = await fetchJson(`${authApiBasePath}/users?page=${pager.page}&pageSize=${pager.pageSize}`);

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
                    <div class="panel-actions">
                        <h2>Users</h2>
                        <div id="users-pagination-top">${renderPagination(users.page, users.totalPages, users.totalCount)}</div>
                    </div>
                    ${renderList(
                        users.data,
                        item => `
                            <div class="list-item-header">
                                <strong>${esc(item.displayName)}</strong>
                                <a class="inline-link" href="${esc(userDetailPath(item.id, "general"))}">Open</a>
                            </div>
                            ${renderMetadataRows([
                                { label: "ID", value: item.id },
                                { label: "Email", value: item.defaultEmail || "n/a" },
                                { label: "Memberships", value: item.membershipCount ?? 0 },
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

        bindPagination("#users-pagination-top", async page => {
            setPagerPage("auth-users", page);
            await render();
        });
    }

    async function renderAuthUserDetail(userId, tab) {
        const config = authViews.users;
        setHeader("Auth Server", config.title, "Inspect the user profile, organization memberships, and recent sessions.");
        renderLoading("Loading user details...");

        const membershipsPager = getPagerState(`auth-user-${userId}-memberships`);
        const sessionsPager = getPagerState(`auth-user-${userId}-sessions`);
        const [user, memberships, sessions] = await Promise.all([
            fetchJson(`${authApiBasePath}/users/${userId}`),
            fetchJson(`${authApiBasePath}/users/${userId}/memberships?page=${membershipsPager.page}&pageSize=${membershipsPager.pageSize}`),
            fetchJson(`${authApiBasePath}/users/${userId}/sessions?page=${sessionsPager.page}&pageSize=${sessionsPager.pageSize}`)
        ]);

        const summaryHtml = `
            <div class="detail-summary-grid">
                <div class="summary-card">
                    <div class="summary-label">Default email</div>
                    <div class="summary-value">${esc(user.defaultEmail || "n/a")}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Organizations</div>
                    <div class="summary-value">${esc(user.membershipCount || memberships.totalCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Active sessions</div>
                    <div class="summary-value">${esc(user.sessionCount || sessions.totalCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">External identities</div>
                    <div class="summary-value">${esc(user.externalIdentityCount || 0)}</div>
                </div>
            </div>
        `;

        const tabNav = `
            <div class="tab-strip">
                ${renderUserTabLink("general", "General", tab, userId)}
                ${renderUserTabLink("organizations", "Organizations", tab, userId)}
                ${renderUserTabLink("sessions", "Sessions", tab, userId)}
            </div>
        `;

        let tabContent = "";
        if (tab === "general") {
            tabContent = `
                <div class="panel-grid">
                    <section class="panel">
                        <div class="panel-actions">
                            <div>
                                <h2>User Profile</h2>
                                <p>This user detail page is the starting point for memberships and session inspection.</p>
                            </div>
                            <a class="inline-link" href="${esc(pathForRoute("auth-users"))}">All users</a>
                        </div>
                        ${renderMetadataRows([
                            { label: "User ID", value: user.id },
                            { label: "Display name", value: user.displayName },
                            { label: "Default email", value: user.defaultEmail || "n/a" },
                            { label: "Active", value: user.isActive ? "Yes" : "No" },
                            { label: "Created", value: formatDate(user.createdAt) },
                            { label: "Updated", value: formatDate(user.updatedAt) }
                        ])}
                    </section>
                    <section class="panel">
                        <h2>Identity Summary</h2>
                        ${renderMetadataRows([
                            { label: "Organizations", value: user.membershipCount || memberships.totalCount || 0 },
                            { label: "Active sessions", value: user.sessionCount || sessions.totalCount || 0 },
                            { label: "External identities", value: user.externalIdentityCount || 0 }
                        ])}
                    </section>
                </div>
            `;
        } else if (tab === "organizations") {
            tabContent = `
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Organization Memberships</h2>
                        <div id="user-memberships-pagination-top">${renderPagination(memberships.page, memberships.totalPages, memberships.totalCount)}</div>
                    </div>
                    ${renderList(
                        memberships.data,
                        item => `
                            <div class="list-item-header">
                                <strong>${esc(item.organization)}</strong>
                                <a class="inline-link" href="${esc(organizationDetailPath(item.organizationId, "general"))}">Open org</a>
                            </div>
                            ${renderMetadataRows([
                                { label: "Organization ID", value: item.organizationId },
                                { label: "Role", value: item.role },
                                { label: "Active", value: item.isActive ? "Yes" : "No" },
                                { label: "Added", value: formatDate(item.createdAt) }
                            ])}
                        `,
                        "No memberships yet."
                    )}
                </section>
            `;
        } else {
            tabContent = `
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Sessions</h2>
                        <div id="user-sessions-pagination-top">${renderPagination(sessions.page, sessions.totalPages, sessions.totalCount)}</div>
                    </div>
                    ${renderList(
                        sessions.data,
                        item => `
                            <strong>${esc(item.id)}</strong>
                            ${renderMetadataRows([
                                { label: "Authentication", value: item.authenticationMethod || "unknown" },
                                { label: "Client", value: item.clientApplicationId || "n/a" },
                                { label: "Created", value: formatDate(item.createdAt) },
                                { label: "Last seen", value: formatDate(item.lastSeenAt) },
                                { label: "Revoked", value: formatDate(item.revokedAt) }
                            ])}
                        `,
                        "No sessions yet."
                    )}
                </section>
            `;
        }

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel detail-hero">
                <div class="panel-actions">
                    <div>
                        <div class="page-eyebrow">User Detail</div>
                        <h2>${esc(user.displayName)}</h2>
                        <p>Follow the user through organizations and sessions without leaving the auth dashboard shell.</p>
                    </div>
                    <a class="inline-link" href="${esc(pathForRoute("auth-users"))}">All users</a>
                </div>
                ${summaryHtml}
                ${tabNav}
            </section>
            ${tabContent}
        `;

        if (tab === "organizations") {
            bindPagination("#user-memberships-pagination-top", async page => {
                setPagerPage(`auth-user-${userId}-memberships`, page);
                await render();
            });
        } else if (tab === "sessions") {
            bindPagination("#user-sessions-pagination-top", async page => {
                setPagerPage(`auth-user-${userId}-sessions`, page);
                await render();
            });
        }
    }

    function renderUserTabLink(tab, label, activeTab, userId) {
        const activeClass = tab === activeTab ? "active" : "";
        return `<a class="tab-link ${activeClass}" href="${esc(userDetailPath(userId, tab))}">${esc(label)}</a>`;
    }

    async function renderAuthMemberships() {
        const config = authViews.memberships;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading memberships...");

        const pager = getPagerState("auth-memberships");
        const memberships = await fetchJson(`${authApiBasePath}/memberships?page=${pager.page}&pageSize=${pager.pageSize}`);

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
                    <div class="panel-actions">
                        <h2>Memberships</h2>
                        <div id="memberships-pagination-top">${renderPagination(memberships.page, memberships.totalPages, memberships.totalCount)}</div>
                    </div>
                    ${renderList(
                        memberships.data,
                        item => `
                            <div class="list-item-header">
                                <strong>${esc(item.organization)}</strong>
                                <a class="inline-link" href="${esc(userDetailPath(item.userId, "general"))}">Open user</a>
                            </div>
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

        bindPagination("#memberships-pagination-top", async page => {
            setPagerPage("auth-memberships", page);
            await render();
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
                    <p>Register a client ID and its allowed redirect URIs. Browser PKCE clients must include at least one redirect URI.</p>
                    <form id="create-client-form">
                        <input name="clientId" placeholder="Client ID" required>
                        <input name="name" placeholder="Name" required>
                        <input name="audience" placeholder="Audience" value="sqlos">
                        <textarea name="redirectUris" placeholder="One redirect URI per line" required></textarea>
                        <button type="submit">Create client</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Clients</h2>
                    <div class="callout">
                        <strong>Startup seed guidance:</strong> Clients marked as startup managed are defined in application code and will be restored on restart. Dashboard-created clients remain editable.
                    </div>
                    ${renderList(
                        clients,
                        item => `
                            <strong>${esc(item.name)}</strong>
                            ${renderMetadataRows([
                                { label: "Client ID", value: item.clientId },
                                { label: "Audience", value: item.audience },
                                { label: "Startup managed", value: item.managedByStartupSeed ? "Yes" : "No" },
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
        const callbackTemplate = `${window.location.origin}${dashboardBasePath}/auth/oidc/callback`;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading OIDC connections...");

        const oidcConnections = await fetchJson(`${authApiBasePath}/oidc-connections`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Configure Social Provider</h2>
                        <p>SqlOS owns the provider callback for social login. Register this exact callback URI with Google, Microsoft, Apple, or your custom OIDC provider, then save the provider configuration here.</p>
                        <form id="create-oidc-connection-form">
                            <select id="oidc-provider-type" name="providerType" required>
                                <option value="Google">Google</option>
                                <option value="Microsoft">Microsoft</option>
                                <option value="Apple">Apple</option>
                                <option value="Custom">Custom</option>
                            </select>
                            <input name="displayName" placeholder="Display name" required>
                            <label>Logo upload<input type="file" accept="image/*,.svg" data-dataurl-target="logoDataUrl"></label>
                            <textarea name="logoDataUrl" placeholder="Optional custom logo data URL. Leave blank to use the built-in provider logo when available."></textarea>
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
                            <textarea name="allowedCallbackUris" required readonly>${esc(callbackTemplate)}</textarea>
                            <textarea name="scopes" placeholder="Optional scopes, one per line"></textarea>
                            <textarea name="claimMapping" placeholder='Claim mapping JSON, for example {\"SubjectClaim\":\"sub\",\"EmailClaim\":\"email\"}'></textarea>
                            <button type="submit">Create OIDC connection</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Social Login Guide</h2>
                        <p>Pick a provider type in the form; this section updates to show the most relevant integration checklist.</p>
                        <div id="oidc-provider-guide"></div>
                    </section>
                </div>
                <section class="panel">
                    <h2>Configured Providers</h2>
                    ${renderList(
                        oidcConnections,
                        item => `
                            <div class="list-item-header">
                                <div class="oidc-provider-summary">
                                    ${renderOidcProviderLogo(item.effectiveLogoDataUrl || item.logoDataUrl, item.displayName)}
                                    <div>
                                        <strong>${esc(item.displayName)}</strong>
                                        <div class="oidc-provider-subtitle">${esc(item.providerType)} social login</div>
                                    </div>
                                </div>
                            </div>
                            ${renderMetadataRows([
                                { label: "Provider", value: item.providerType },
                                { label: "Connection ID", value: item.id },
                                {
                                    label: "Effective logo",
                                    html: renderOidcProviderLogo(item.effectiveLogoDataUrl || item.logoDataUrl, item.displayName, "oidc-provider-logo oidc-provider-logo--meta")
                                },
                                {
                                    label: "Logo source",
                                    value: item.logoDataUrl ? "Custom upload" : item.effectiveLogoDataUrl ? "Built-in provider logo" : "Initials fallback"
                                },
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
                                    label: "Provider callback URI",
                                    html: `<div class="inline-code">${esc(callbackTemplate)}</div>`
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
                                <label>Logo upload<input type="file" accept="image/*,.svg" data-dataurl-target="logoDataUrl"></label>
                                <textarea name="logoDataUrl" placeholder="Optional custom logo data URL">${esc(item.logoDataUrl || "")}</textarea>
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
                                <textarea name="allowedCallbackUris" required readonly>${esc(callbackTemplate)}</textarea>
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

        const guideContainer = content.querySelector("#oidc-provider-guide");
        const guideProviderSelect = content.querySelector("#oidc-provider-type");
        const updateGuide = () => {
            if (!guideContainer) {
                return;
            }

            const selectedProvider = guideProviderSelect ? guideProviderSelect.value : "Google";
            guideContainer.innerHTML = renderOidcProviderGuide(selectedProvider, callbackTemplate);
        };
        updateGuide();
        guideProviderSelect?.addEventListener("change", updateGuide);
        bindDataUrlFileInputs(content);

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
                        <p>Create the org-scoped draft first, then import the customer's federation metadata XML. For day-to-day setup, prefer the SSO tab on each organization detail page.</p>
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
                organizationId: form.get("organizationId"),
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

    async function renderAuthPage() {
        const config = authViews.authpage;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading Auth Page settings...");

        const [settings, metadata] = await Promise.all([
            fetchJson(`${authApiBasePath}/settings/auth-page`),
            fetchJson(`${authServerBasePath}/.well-known/oauth-authorization-server`)
        ]);

        const enabledCredentialTypes = Array.isArray(settings.enabledCredentialTypes)
            ? settings.enabledCredentialTypes.join(", ")
            : "";
        const loginUrl = new URL(`${authServerBasePath}/login`, window.location.origin).toString();
        const signupUrl = new URL(`${authServerBasePath}/signup`, window.location.origin).toString();

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Auth Page Settings</h2>
                        <p>These values control the hosted login and signup experience. The page is served directly from the SqlOS auth server, so changes show up without app-specific frontend work.</p>
                        ${settings.managedByStartupSeed ? `<div class="callout"><strong>Startup managed:</strong> These values are seeded from application startup and will be reapplied on restart.</div>` : ""}
                        <form id="auth-page-settings-form">
                            <input name="pageTitle" placeholder="Page title" value="${esc(settings.pageTitle || "")}" required>
                            <input name="pageSubtitle" placeholder="Subtitle" value="${esc(settings.pageSubtitle || "")}" required>
                            <div class="panel-grid">
                                <input name="primaryColor" placeholder="Primary color (#2563eb)" value="${esc(settings.primaryColor || "")}" required>
                                <input name="accentColor" placeholder="Accent color (#0f172a)" value="${esc(settings.accentColor || "")}" required>
                            </div>
                            <div class="panel-grid">
                                <input name="backgroundColor" placeholder="Background color (#f8fafc)" value="${esc(settings.backgroundColor || "")}" required>
                                <select name="layout">
                                    <option value="split" ${settings.layout === "split" ? "selected" : ""}>Split</option>
                                    <option value="stacked" ${settings.layout === "stacked" ? "selected" : ""}>Stacked</option>
                                </select>
                            </div>
                            ${settings.headlessCapabilityRegistered
                                ? `<div class="callout"><strong>Headless auth is enabled.</strong> <code>/authorize</code> redirects into your app because <code>UseHeadlessAuthPage()</code> registered a UI callback.</div>`
                                : `<div class="callout"><strong>Hosted auth is enabled.</strong> SqlOS serves the login and signup pages because no headless UI callback is registered.</div>`}
                            <label><input type="checkbox" name="enablePasswordSignup" ${settings.enablePasswordSignup ? "checked" : ""}> Allow password signup</label>
                            <input name="enabledCredentialTypes" placeholder="Enabled credential types" value="${esc(enabledCredentialTypes || "password")}" required>
                            <label>Logo upload<input id="auth-page-logo-file" type="file" accept="image/*"></label>
                            <textarea name="logoBase64" placeholder="Optional base64 image payload or data URL">${esc(settings.logoBase64 || "")}</textarea>
                            <button type="submit">Save Auth Page</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Hosted Endpoints</h2>
                        <p>These are the direct URLs admins and application teams can deep link to when they want to send users straight into the hosted auth experience.</p>
                        ${renderMetadataRows([
                            { label: "Login URL", html: `<a class="inline-link" href="${esc(loginUrl)}" target="_blank" rel="noreferrer">${esc(loginUrl)}</a>` },
                            { label: "Signup URL", html: `<a class="inline-link" href="${esc(signupUrl)}" target="_blank" rel="noreferrer">${esc(signupUrl)}</a>` },
                            { label: "Issuer", value: metadata.issuer },
                            { label: "Authorization endpoint", value: metadata.authorizationEndpoint },
                            { label: "Token endpoint", value: metadata.tokenEndpoint },
                            { label: "JWKS URI", value: metadata.jwksUri },
                            { label: "PKCE methods", value: (metadata.codeChallengeMethodsSupported || []).join(", ") },
                            { label: "Grant types", value: (metadata.grantTypesSupported || []).join(", ") }
                        ])}
                        <div class="callout">
                            <strong>Admin guidance:</strong> Use this page to set the title, logo, colors, and layout. Password is the only first-party credential type enabled in v1, but OIDC and SAML providers still appear below it when configured.
                        </div>
                    </section>
                </div>
            </div>
        `;

        bindForm("auth-page-settings-form", async form => {
            await fetchJson(`${authApiBasePath}/settings/auth-page`, {
                method: "PUT",
                body: JSON.stringify({
                    logoBase64: form.get("logoBase64") || null,
                    pageTitle: form.get("pageTitle"),
                    pageSubtitle: form.get("pageSubtitle"),
                    primaryColor: form.get("primaryColor"),
                    accentColor: form.get("accentColor"),
                    backgroundColor: form.get("backgroundColor"),
                    layout: form.get("layout"),
                    enablePasswordSignup: form.get("enablePasswordSignup") === "on",
                    enabledCredentialTypes: String(form.get("enabledCredentialTypes") || "password")
                        .split(/[,\s]+/)
                        .map(value => value.trim())
                        .filter(Boolean)
                })
            });
            setFlash("success", "Auth Page settings saved.");
        });

        const fileInput = document.getElementById("auth-page-logo-file");
        const form = document.getElementById("auth-page-settings-form");
        fileInput?.addEventListener("change", () => {
            const file = fileInput.files?.[0];
            if (!file || !form) {
                return;
            }

            const reader = new FileReader();
            reader.onload = () => {
                form.elements.logoBase64.value = String(reader.result || "");
            };
            reader.readAsDataURL(file);
        });
    }

    async function renderAuthSessions() {
        const config = authViews.sessions;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading sessions...");

        const pager = getPagerState("auth-sessions");
        const sessions = await fetchJson(`${authApiBasePath}/sessions?page=${pager.page}&pageSize=${pager.pageSize}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel">
                <div class="panel-actions">
                    <h2>Sessions</h2>
                    <div id="sessions-pagination-top">${renderPagination(sessions.page, sessions.totalPages, sessions.totalCount)}</div>
                </div>
                ${renderList(
                    sessions.data,
                    item => `
                        <div class="list-item-header">
                            <strong>${esc(item.user)}</strong>
                            ${item.userId ? `<a class="inline-link" href="${esc(userDetailPath(item.userId, "sessions"))}">Open user</a>` : ""}
                        </div>
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

        bindPagination("#sessions-pagination-top", async page => {
            setPagerPage("auth-sessions", page);
            await render();
        });
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
