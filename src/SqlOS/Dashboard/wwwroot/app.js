(function () {
    const dashboardBasePath = normalizeBasePath(window.__SQL_OS_BASE_PATH__ || "/sqlos");
    const dashboardAuthBasePath = `${dashboardBasePath}/dashboard-auth`;
    const authServerBasePath = `${dashboardBasePath}/auth`;
    const authDashboardPath = `${dashboardBasePath}/admin/auth`;
    const fgaDashboardPath = `${dashboardBasePath}/admin/fga`;
    const emailDashboardPath = `${dashboardBasePath}/admin/email`;
    const auditDashboardPath = `${dashboardBasePath}/admin/audit`;
    const calendarDashboardPath = `${dashboardBasePath}/admin/calendar`;
    const authApiBasePath = `${authDashboardPath}/api`;
    const fgaApiBasePath = `${fgaDashboardPath}/api`;
    const emailApiBasePath = `${emailDashboardPath}/api`;
    const auditApiBasePath = `${auditDashboardPath}/api`;
    const calendarApiBasePath = `${calendarDashboardPath}/api`;
    const clientOnboardingDocsUrl = "https://sqlos.dev/docs/authserver/preregistration-vs-cimd-vs-dcr";
    const dashboardCapabilities = window.__SQL_OS_CAPABILITIES__ || {};
    const scimEnabled = dashboardCapabilities.scimEnabled === true;

    const content = document.getElementById("content");
    const pageEyebrow = document.getElementById("page-eyebrow");
    const pageTitle = document.getElementById("page-title");
    const pageDescription = document.getElementById("page-description");
    const topbarTitle = document.getElementById("topbar-title");
    const logoutButton = document.getElementById("dashboard-logout");

    let flashMessage = null;
    let latestSsoDraft = null;
    let latestSsoPortalSession = null;
    let latestScimToken = null;
    let latestMachineClientSecret = null;
    let latestClientSecret = null;
    let activeFgaDashboard = null;
    const pagerState = new Map();
    let selectedClientId = null;
    let clientDraftState = null;
    let suppressClientDraftSync = false;
    let focusClientFormAfterPreset = false;
    const clientViewState = {
        preset: "owned-web",
        source: "all",
        status: "all",
        search: ""
    };
    const emailMessageFilters = {
        status: "all",
        templateKey: "",
        recipient: "",
        from: "",
        to: ""
    };
    const auditFilters = {
        organizationId: "",
        application: "",
        source: "",
        action: "",
        actorType: "",
        actorId: "",
        targetType: "",
        targetId: "",
        result: "",
        from: "",
        to: "",
        search: ""
    };
    let selectedAuditEventId = null;
    let selectedCalendarConnectionId = null;
    const calendarConnectionFilters = {
        search: "",
        includeRevoked: true
    };
    const listFilters = {
        organizations: "",
        users: "",
        memberships: "",
        orgUsers: "",
        orgUsersOrganizationId: ""
    };

    const authViews = {
        overview: { title: "Auth Server", description: "Organizations, users, sessions, applications, and security settings." },
        organizations: { title: "Organizations", description: "Create and manage organizations and their primary domains." },
        users: { title: "Users", description: "Create users and bootstrap password credentials." },
        memberships: { title: "Memberships", description: "Assign users to organizations and manage roles." },
        clients: { title: "Applications", description: "Manage owned apps, client metadata, access assignments, and lifecycle actions." },
        "machine-clients": { title: "Machine Clients", description: "Provision OAuth client credentials and FGA service accounts as one operational identity." },
        oidc: { title: "Social Login", description: "Configure Google, Microsoft, Apple, GitHub, and custom providers for authserver-owned social login." },
        security: { title: "Security", description: "Tune refresh, idle, and absolute session lifetimes." },
        mfa: { title: "MFA", description: "Configure authenticator app enrollment and second-factor requirements." },
        authpage: { title: "Auth Page", description: "Brand the hosted authorization page and publish the login, signup, and PKCE endpoints your app exposes." },
        sessions: { title: "Sessions", description: "Inspect active sessions and authentication methods." },
        audit: { title: "Audit Events", description: "Review recent auth and admin activity." }
    };
    const clientPresetDefinitions = {
        "owned-web": {
            title: "Owned SPA / Web",
            description: "Best for your own browser app. SqlOS will create a normal first-party public PKCE client.",
            name: "Owned Web App",
            audience: "sqlos",
            redirectHint: "https://app.example.com/auth/callback",
            clientIdHint: "my-web-app",
            allowedScopes: ["openid", "profile", "email"],
            isFirstParty: true
        },
        "owned-server-web": {
            title: "Owned Server Web",
            description: "Best for a server-rendered or backend-for-frontend app that can protect a client secret.",
            name: "Owned Server Web App",
            audience: "sqlos",
            redirectHint: "https://app.example.com/auth/callback",
            clientIdHint: "my-server-web-app",
            allowedScopes: ["openid", "profile", "email", "offline_access"],
            isFirstParty: true,
            confidential: true
        },
        "owned-native": {
            title: "Owned Native / Mobile",
            description: "Best for your own mobile or desktop app. Use PKCE and a native redirect scheme.",
            name: "Owned Native App",
            audience: "sqlos",
            redirectHint: "myapp://auth/callback",
            clientIdHint: "my-mobile-app",
            allowedScopes: ["openid", "profile", "email"],
            isFirstParty: true
        },
        "cli-device": {
            title: "CLI / Device OAuth",
            description: "Best for terminal apps that should show a browser sign-in URL instead of running a localhost redirect listener.",
            name: "CLI App",
            audience: "sqlos",
            redirectHint: "",
            clientIdHint: "my-cli",
            allowedScopes: ["openid", "profile", "email", "offline_access"],
            isFirstParty: true,
            allowDeviceAuthorization: true,
            requirePkce: false
        },
        "portable-mcp": {
            title: "Portable MCP Client",
            description: "Use this when you want a manual client record today but you are really designing for portable public clients. Prefer CIMD when possible.",
            name: "Portable MCP Client",
            audience: "sqlos",
            redirectHint: "https://client.example.com/oauth/callback",
            clientIdHint: "portable-mcp-client",
            allowedScopes: ["openid", "profile"],
            isFirstParty: false
        },
        "chatgpt-compat": {
            title: "ChatGPT Compatibility",
            description: "Use this when you need a manual compatibility client for testing. Real ChatGPT onboarding usually arrives via DCR.",
            name: "ChatGPT Compatibility Client",
            audience: "sqlos",
            redirectHint: "https://chat.openai.com/aip/callback",
            clientIdHint: "chatgpt-compat-client",
            allowedScopes: ["openid", "profile"],
            isFirstParty: false
        },
        "vscode-compat": {
            title: "VS Code Compatibility",
            description: "Use this when you need a manual compatibility client for testing. Real VS Code public clients often arrive via DCR.",
            name: "VS Code Compatibility Client",
            audience: "sqlos",
            redirectHint: "http://127.0.0.1:3000/callback",
            clientIdHint: "vscode-compat-client",
            allowedScopes: ["openid", "profile"],
            isFirstParty: false
        },
        "advanced": {
            title: "Advanced / Custom",
            description: "Use this when you want to fill the raw fields yourself. All advanced settings stay editable below.",
            name: "",
            audience: "sqlos",
            redirectHint: "https://client.example.com/callback",
            clientIdHint: "custom-client-id",
            allowedScopes: [],
            isFirstParty: false
        }
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
        GitHub: {
            heading: "GitHub Setup",
            description: "Create a GitHub OAuth app and let SqlOS fetch the verified primary email through GitHub's user APIs.",
            docsLabel: "GitHub OAuth app",
            docsUrl: "https://github.com/settings/developers",
            steps: [
                "Create or open a GitHub OAuth App.",
                "Set Authorization callback URL to: {callback}.",
                "Copy Client ID + Client Secret from GitHub into SqlOS, then save the connection.",
                "Use the default scopes so SqlOS can read the profile and verified primary email."
            ],
            rows: [
                { label: "Provider type", value: "GitHub" },
                { label: "Protocol", value: "OAuth profile" },
                { label: "Authorization endpoint", value: "https://github.com/login/oauth/authorize" },
                { label: "Token endpoint", value: "https://github.com/login/oauth/access_token" },
                { label: "Profile endpoints", value: "/user and /user/emails" },
                { label: "Provider callback URI", html: "<div class=\"inline-code\">{callback}</div>" },
                { label: "Suggested scopes", value: "read:user, user:email" }
            ],
            integration: "GitHub returns an OAuth access token. SqlOS uses it to load GitHub profile/email data, links by numeric GitHub user id, and then completes the normal social login redirect."
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

    const organizationTabs = new Set([
        "general",
        "users",
        "invitations",
        "sso",
        ...(scimEnabled ? ["scim"] : [])
    ]);
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
    const fgaDetailViews = new Set(["resources", "roles", "users", "agents", "service-accounts", "user-groups"]);
    const emailViews = {
        templates: { title: "Email Templates", description: "Create, edit, activate, deactivate, and preview transactional email templates." },
        messages: { title: "Email Messages", description: "Review recent transactional email deliveries and provider outcomes." }
    };
    const calendarViews = {
        connections: { title: "Calendar Connections", description: "Google and Microsoft calendar connections, sync health, and token status per user or organization." }
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

    document.getElementById("dashboard-modal-close")?.addEventListener("click", closeCreateModal);
    document.getElementById("dashboard-modal")?.addEventListener("click", event => {
        if (event.target.id === "dashboard-modal") {
            closeCreateModal();
        }
    });
    document.addEventListener("keydown", event => {
        if (event.key === "Escape") {
            closeCreateModal();
        }
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
                const contentType = response.headers.get("content-type") || "";
                let payload = null;
                let message = `${response.status}`;

                if (contentType.includes("application/json")) {
                    try {
                        payload = await response.json();
                    } catch {
                        payload = null;
                    }
                } else {
                    const text = await response.text();
                    if (text) {
                        try {
                            payload = JSON.parse(text);
                        } catch {
                            payload = text;
                        }
                    }
                }

                if (payload && typeof payload === "object" && !Array.isArray(payload)) {
                    message = payload.message || payload.error_description || payload.error || JSON.stringify(payload);
                } else if (typeof payload === "string" && payload.trim()) {
                    message = payload;
                }

                const error = new Error(message || `${response.status}`);
                error.status = response.status;
                error.payload = payload;
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

            const isDashboardPath = dashboardBasePath === "/"
                ? parsed.pathname.startsWith("/")
                : parsed.pathname === dashboardBasePath || parsed.pathname.startsWith(`${dashboardBasePath}/`);
            if (!isDashboardPath) {
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

    function confirmScimConnectionDisable() {
        return window.confirm(
            "Disable this SCIM connection? Its bearer token will stop working immediately, and SqlOS will immediately revoke the FGA grants managed by this connection. Re-enabling does not restore those grants until the IdP pushes or resynchronizes the affected groups."
        );
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

        if (route.startsWith("email-")) {
            return `${emailDashboardPath}/${route.slice(6)}`;
        }

        if (route.startsWith("audit-")) {
            return `${auditDashboardPath}/${route.slice(6)}`;
        }

        if (route.startsWith("calendar-")) {
            return `${calendarDashboardPath}/${route.slice(9)}`;
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

    function clientDetailPath(clientApplicationId) {
        return `${authDashboardPath}/clients/${encodeURIComponent(clientApplicationId)}`;
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

            if (view === "clients" && segments[3]) {
                const clientApplicationId = decodeRouteSegment(segments[3]);
                return {
                    kind: "auth",
                    view,
                    clientApplicationId,
                    key: "auth-clients",
                    canonicalPath: clientDetailPath(clientApplicationId)
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
            const detailId = fgaDetailViews.has(view) && segments[3]
                ? decodeRouteSegment(segments[3])
                : null;
            const componentRoute = detailId
                ? `/${view}/${encodeURIComponent(detailId)}`
                : `/${view}`;
            return {
                kind: "fga",
                view,
                componentRoute,
                key: `fga-${view}`,
                canonicalPath: `${fgaDashboardPath}${componentRoute}`
            };
        }

        if (segments[1] === "email") {
            const view = emailViews[segments[2]] ? segments[2] : "templates";
            return {
                kind: "email",
                view,
                key: `email-${view}`,
                canonicalPath: `${emailDashboardPath}/${view}`
            };
        }

        if (segments[1] === "audit") {
            const view = segments[2] === "logs" ? "logs" : "logs";
            return {
                kind: "audit",
                view,
                key: "audit-logs",
                canonicalPath: `${auditDashboardPath}/${view}`
            };
        }

        if (segments[1] === "calendar") {
            const view = calendarViews[segments[2]] ? segments[2] : "connections";
            return {
                kind: "calendar",
                view,
                key: `calendar-${view}`,
                canonicalPath: `${calendarDashboardPath}/${view}`
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

    function formatJson(value) {
        if (!value) {
            return "n/a";
        }

        if (typeof value === "object") {
            return JSON.stringify(value, null, 2);
        }

        try {
            return JSON.stringify(JSON.parse(value), null, 2);
        } catch {
            return String(value);
        }
    }

    function renderClientBadge(label, tone = "neutral") {
        return `<span class="client-badge client-badge--${esc(tone)}">${esc(label)}</span>`;
    }

    function renderClientSourceBadges(client) {
        const badges = [
            renderClientBadge(client.sourceLabel || client.registrationSource || "Unknown", "source"),
            renderClientBadge(client.lifecycleState === "disabled" ? "Disabled" : "Active", client.lifecycleState === "disabled" ? "danger" : "success")
        ];

        if (client.managedByStartupSeed) {
            badges.push(renderClientBadge("Code owned", "muted"));
        }
        if (client.ownership?.isOrphaned) {
            badges.push(renderClientBadge("Seed missing", "warning"));
        }

        if (client.metadataCacheState === "fresh") {
            badges.push(renderClientBadge("Metadata fresh", "info"));
        }

        if (client.metadataCacheState === "stale") {
            badges.push(renderClientBadge("Metadata stale", "warning"));
        }

        if (client.duplicateCount > 1) {
            badges.push(renderClientBadge(`${client.duplicateCount} similar DCR clients`, "warning"));
        }

        return badges.join("");
    }

    function currentClientPreset() {
        return clientPresetDefinitions[clientViewState.preset] || clientPresetDefinitions["owned-web"];
    }

    function createClientDraftFromPreset(presetKey = clientViewState.preset) {
        const preset = clientPresetDefinitions[presetKey] || clientPresetDefinitions["owned-web"];
        return {
            clientId: "",
            name: preset.name || "",
            audience: preset.audience || "sqlos",
            redirectUris: preset.redirectHint || "",
            description: "",
            allowedScopes: (preset.allowedScopes || []).join("\n"),
            requirePkce: preset.requirePkce !== false,
            isFirstParty: !!preset.isFirstParty,
            allowDeviceAuthorization: !!preset.allowDeviceAuthorization,
            confidential: !!preset.confidential
        };
    }

    function ensureClientDraftState() {
        if (!clientDraftState) {
            clientDraftState = createClientDraftFromPreset();
        }

        return clientDraftState;
    }

    function syncClientDraftFromForm() {
        const form = document.getElementById("create-client-form");
        if (!form) {
            return;
        }

        const data = new FormData(form);
        const previousDraft = ensureClientDraftState();
        clientDraftState = {
            clientId: String(data.get("clientId") || "").trim(),
            name: String(data.get("name") || "").trim(),
            audience: String(data.get("audience") || "").trim(),
            redirectUris: String(data.get("redirectUris") || ""),
            description: String(data.get("description") || ""),
            allowedScopes: String(data.get("allowedScopes") || ""),
            requirePkce: data.get("requirePkce") === "on",
            allowDeviceAuthorization: data.get("allowDeviceAuthorization") === "on",
            confidential: data.get("confidential") === "on",
            isFirstParty: previousDraft.isFirstParty
        };
    }

    function applyClientPreset(presetKey) {
        clientViewState.preset = clientPresetDefinitions[presetKey] ? presetKey : "owned-web";
        clientDraftState = createClientDraftFromPreset(clientViewState.preset);
        suppressClientDraftSync = true;
        focusClientFormAfterPreset = true;
    }

    function describeFeatureStatus(value) {
        if (value === true) {
            return { label: "Enabled", tone: "success" };
        }

        if (value === false) {
            return { label: "Disabled", tone: "muted" };
        }

        return { label: "Unavailable", tone: "warning" };
    }

    function describePresetOwnership(presetKey, preset) {
        if (presetKey === "advanced") {
            return {
                label: "Custom manual client",
                tone: "muted",
                description: "Use this when you want to control the manual client fields yourself before saving."
            };
        }

        if (preset.isFirstParty) {
            return {
                label: "First-party app client",
                tone: "success",
                description: "Use this for apps your team owns. These are usually seeded in AddSqlOS(...) or created here for local and dev use."
            };
        }

        return {
            label: "Manual third-party test client",
            tone: "warning",
            description: "Use this for local compatibility testing. Real third-party clients usually appear automatically as discovered or registered records."
        };
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

        if (raw === "github") {
            return "GitHub";
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

    function renderIdChip(id) {
        if (!id) {
            return "";
        }
        return `<span class="id-chip" title="${esc(id)}">${esc(id)}</span>`;
    }

    function renderChip(text, tone) {
        if (text === null || text === undefined || text === "" || text === "n/a") {
            return "";
        }
        const cls = tone ? `chip chip-${tone}` : "chip";
        return `<span class="${cls}">${esc(String(text))}</span>`;
    }

    function renderListRows(items, rowFn, emptyText) {
        if (!items || items.length === 0) {
            return `<div class="empty-state-block">${esc(emptyText)}</div>`;
        }
        return `<div class="list-rows">${items.map(rowFn).join("")}</div>`;
    }

    function renderListRow({ href, title, subtitle, metaHtml, actionsHtml }) {
        const tag = href ? "a" : "div";
        const hrefAttr = href ? ` href="${esc(href)}"` : "";
        return `
            <${tag} class="list-row${href ? " list-row-link" : ""}"${hrefAttr}>
                <div class="list-row-main">
                    <div class="list-row-title">${esc(title)}</div>
                    ${subtitle ? `<div class="list-row-sub">${subtitle}</div>` : ""}
                </div>
                <div class="list-row-meta">${metaHtml || ""}</div>
                ${actionsHtml || (href ? `<span class="list-row-chevron" aria-hidden="true">›</span>` : "")}
            </${tag}>`;
    }

    function renderListToolbar({ title, searchId, searchPlaceholder, searchValue, createLabel, pagerHtml }) {
        return `
            <div class="list-toolbar">
                <div class="list-toolbar-start">
                    <h2>${esc(title)}</h2>
                    ${searchId ? `<input type="search" class="list-search" id="${esc(searchId)}" placeholder="${esc(searchPlaceholder || "Search")}" value="${esc(searchValue || "")}" autocomplete="off">` : ""}
                </div>
                <div class="list-toolbar-end">
                    ${createLabel ? `<button type="button" class="btn-primary" id="open-create-modal">${esc(createLabel)}</button>` : ""}
                    ${pagerHtml || ""}
                </div>
            </div>`;
    }

    function closeCreateModal() {
        const overlay = document.getElementById("dashboard-modal");
        const body = document.getElementById("dashboard-modal-body");
        if (overlay) {
            overlay.hidden = true;
        }
        if (body) {
            body.innerHTML = "";
        }
    }

    function openCreateModal(title, bodyHtml) {
        const overlay = document.getElementById("dashboard-modal");
        const titleEl = document.getElementById("dashboard-modal-title");
        const body = document.getElementById("dashboard-modal-body");
        if (!overlay || !titleEl || !body) {
            return;
        }
        titleEl.textContent = title;
        body.innerHTML = bodyHtml;
        overlay.hidden = false;
        body.querySelector("input, select, textarea")?.focus();
    }

    function bindCreateModal(openButtonId, title, bodyHtml, bindFn) {
        document.getElementById(openButtonId)?.addEventListener("click", () => {
            openCreateModal(title, bodyHtml);
            bindFn?.();
        });
    }

    function bindListSearch(inputId, onSearch) {
        const input = document.getElementById(inputId);
        if (!input) {
            return;
        }
        let debounce;
        input.addEventListener("input", () => {
            clearTimeout(debounce);
            debounce = setTimeout(() => onSearch(String(input.value || "").trim()), 300);
        });
    }

    function listQuery(pagerKey, pageSize, search) {
        const pager = resetPager(pagerKey, pageSize, search || "");
        const params = new URLSearchParams(pagerQuery(pager));
        if (search) {
            params.set("search", search);
        }
        return { pager, query: params.toString() };
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
        closeCreateModal();
        content.innerHTML = `<div class="loading">${esc(message)}</div>`;
    }

    function createPagerState(defaultPageSize, filterKey = "") {
        return {
            pageSize: defaultPageSize,
            cursors: [null],
            index: 0,
            filterKey
        };
    }

    function getPagerState(key, defaultPageSize = 10) {
        if (!pagerState.has(key)) {
            pagerState.set(key, createPagerState(defaultPageSize));
        }

        const current = pagerState.get(key);
        if (!Array.isArray(current.cursors)) {
            pagerState.set(key, createPagerState(defaultPageSize, current.filterKey || ""));
        }

        return pagerState.get(key);
    }

    function resetPager(key, defaultPageSize, filterKey) {
        const normalized = filterKey ?? "";
        const current = pagerState.get(key);
        if (current
            && Array.isArray(current.cursors)
            && current.filterKey === normalized
            && current.pageSize === defaultPageSize) {
            return current;
        }

        const pager = createPagerState(defaultPageSize, normalized);
        pagerState.set(key, pager);
        return pager;
    }

    function restartPagerWindow(key) {
        const pager = pagerState.get(key);
        if (!pager || !Array.isArray(pager.cursors)) {
            return;
        }

        pager.cursors = [null];
        pager.index = 0;
        pager.filterKey = "";
    }

    function pagerQuery(pager) {
        const params = new URLSearchParams();
        params.set("pageSize", String(pager.pageSize));
        const cursor = pager.cursors[pager.index];
        if (cursor) {
            params.set("cursor", cursor);
        }
        return params.toString();
    }

    function renderPagination(pager, result) {
        const atStart = !pager || pager.index === 0;
        const hasNext = !!(result && result.hasNextPage);
        const count = Array.isArray(result?.data) ? result.data.length : 0;
        const showing = count === 0 ? "No results" : `Showing ${count}`;
        return `
            <div class="pagination">
                <button type="button" class="pg-btn" data-pager="prev" ${atStart ? "disabled" : ""}>Previous</button>
                <button type="button" class="pg-btn" data-pager="next" ${hasNext ? "" : "disabled"}>Next</button>
                <span class="pg-info">${showing}</span>
            </div>
        `;
    }

    function bindPagination(containerSelector, pagerKey, result, reloadFn) {
        const pager = getPagerState(pagerKey);
        document.querySelectorAll(`${containerSelector} [data-pager="prev"]:not([disabled])`).forEach(button => {
            button.addEventListener("click", async () => {
                if (pager.index > 0) {
                    pager.index -= 1;
                    await reloadFn();
                }
            });
        });
        document.querySelectorAll(`${containerSelector} [data-pager="next"]:not([disabled])`).forEach(button => {
            button.addEventListener("click", async () => {
                if (!result?.hasNextPage) {
                    return;
                }
                if (pager.cursors[pager.index + 1] == null && result.nextCursor) {
                    pager.cursors.push(result.nextCursor);
                }
                pager.index += 1;
                await reloadFn();
            });
        });
    }

    function appendListItems(containerSelector, items, formatter) {
        const container = document.querySelector(containerSelector);
        if (!container || !items || items.length === 0) {
            return;
        }

        container.querySelector(".empty-state-block")?.remove();
        let stack = container.querySelector(".list-stack");
        if (!stack) {
            stack = document.createElement("div");
            stack.className = "list-stack";
            container.insertBefore(stack, container.firstChild);
        }

        items.forEach(item => {
            const row = document.createElement("div");
            row.className = "list-item";
            row.innerHTML = formatter(item);
            stack.appendChild(row);
        });
    }

    function renderLoadMoreButton(id, hasNextPage) {
        return `<button type="button" class="pg-btn js-load-more" id="${esc(id)}" ${hasNextPage ? "" : "hidden"}>Load more</button>`;
    }

    function renderRemotePicker(options) {
        return `
            <input type="search" id="${esc(options.searchId)}" placeholder="${esc(options.searchPlaceholder)}" autocomplete="off">
            <select name="${esc(options.selectName)}" id="${esc(options.selectId)}" ${options.required ? "required" : ""}>
                <option value="">${esc(options.emptyLabel)}</option>
                ${(options.items || []).map(item => `<option value="${esc(options.itemValue(item))}">${esc(options.itemLabel(item))}</option>`).join("")}
            </select>
            ${renderLoadMoreButton(options.loadMoreId, options.hasNextPage)}
        `;
    }

    function bindRemotePicker(options) {
        const searchInput = document.getElementById(options.searchId);
        const select = document.getElementById(options.selectId);
        const loadMore = document.getElementById(options.loadMoreId);
        const pageSize = options.pageSize || 25;
        let latest = options.initialResult || { data: [], hasNextPage: false, nextCursor: null };
        let debounceId = 0;
        let requestSeq = 0;

        const fillOptions = (result, append) => {
            latest = result;
            const items = result.data || [];
            if (!append && select) {
                const emptyLabel = select.querySelector("option[value='']")?.textContent || options.emptyLabel || "Select";
                select.innerHTML = `<option value="">${esc(emptyLabel)}</option>`;
            }
            items.forEach(item => {
                const option = document.createElement("option");
                option.value = options.itemValue(item);
                option.textContent = options.itemLabel(item);
                select?.appendChild(option);
            });
            if (loadMore) {
                loadMore.hidden = !result.hasNextPage;
            }
        };

        searchInput?.addEventListener("input", () => {
            const token = ++debounceId;
            window.setTimeout(async () => {
                if (token !== debounceId) {
                    return;
                }

                const search = String(searchInput.value || "").trim();
                const pager = resetPager(options.pagerKey, pageSize, search);
                const seq = ++requestSeq;
                try {
                    const result = await options.fetchPage(pager, search);
                    if (seq !== requestSeq) {
                        return;
                    }
                    fillOptions(result, false);
                } catch (error) {
                    if (seq !== requestSeq) {
                        return;
                    }
                    setFlash("error", error.message || String(error));
                }
            }, 250);
        });

        loadMore?.addEventListener("click", async () => {
            const pager = getPagerState(options.pagerKey, pageSize);
            if (!latest.hasNextPage) {
                return;
            }
            if (pager.cursors[pager.index + 1] == null && latest.nextCursor) {
                pager.cursors.push(latest.nextCursor);
            }
            pager.index += 1;
            const search = String(searchInput?.value || "").trim();
            const seq = ++requestSeq;
            try {
                const result = await options.fetchPage(pager, search);
                if (seq !== requestSeq) {
                    return;
                }
                fillOptions(result, true);
            } catch (error) {
                if (seq !== requestSeq) {
                    return;
                }
                setFlash("error", error.message || String(error));
            }
        });
    }

    async function render() {
        const route = currentRoute();
        if (window.location.pathname !== route.canonicalPath) {
            history.replaceState({}, "", route.canonicalPath);
        }

        setLoginMode(route.kind === "login");
        updateActiveNav(route.key);

        activeFgaDashboard?.destroy();
        activeFgaDashboard = null;

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

            if (route.kind === "email") {
                await renderEmailRoute(route.view);
                return;
            }

            if (route.kind === "audit") {
                await renderAuditLogs();
                return;
            }

            if (route.kind === "calendar") {
                await renderCalendarConnections();
                return;
            }

            await renderFgaRoute(route);
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
                        ${quickLink("auth-mfa", "MFA")}
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
                <section class="card">
                    <h2>Communications</h2>
                    <p>Manage operational email templates and inspect delivery history.</p>
                    <div class="link-list">
                        ${quickLink("email-templates", "Email Templates")}
                        ${quickLink("email-messages", "Email Messages")}
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
            await renderAuthClients(route);
            return;
        }

        if (view === "machine-clients") {
            await renderAuthMachineClients();
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

        if (view === "mfa") {
            await renderAuthMfa();
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
                            { label: "Absolute lifetime", value: `${settings.sessionAbsoluteLifetimeMinutes} minutes` },
                            { label: "Refresh grace window", value: settings.refreshTokenGraceWindowSeconds === 0 ? "Disabled" : `${settings.refreshTokenGraceWindowSeconds} seconds` }
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

        const search = listFilters.organizations;
        const { pager, query } = listQuery("auth-organizations", 10, search);
        const organizations = await fetchJson(`${authApiBasePath}/organizations?${query}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel list-page">
                ${renderListToolbar({
                    title: "Organizations",
                    searchId: "organizations-search",
                    searchPlaceholder: "Search name, slug, or domain",
                    searchValue: search,
                    createLabel: "New organization",
                    pagerHtml: `<div id="organizations-pagination-top">${renderPagination(pager, organizations)}</div>`
                })}
                ${renderListRows(
                    organizations.data,
                    item => renderListRow({
                        href: organizationDetailPath(item.id, "general"),
                        title: item.name,
                        subtitle: [item.slug, item.primaryDomain].filter(Boolean).join(" · "),
                        metaHtml: [
                            renderIdChip(item.id),
                            renderChip(item.membershipCount ? `${item.membershipCount} members` : "", "neutral"),
                            item.enabledSsoConnections ? renderChip("SSO", "amber") : "",
                            item.isActive === false ? renderChip("Disabled") : ""
                        ].join("")
                    }),
                    "No organizations yet."
                )}
            </section>
        `;

        bindCreateModal("open-create-modal", "New organization", `
            <p>Create a tenant and optionally set its primary login domain.</p>
            <form id="create-org-form">
                <input name="name" placeholder="Organization name" required>
                <input name="slug" placeholder="Slug (optional)">
                <input name="primaryDomain" placeholder="Primary domain (optional)">
                <div class="modal-actions">
                    <button type="button" class="btn-secondary" id="cancel-create-modal">Cancel</button>
                    <button type="submit">Create organization</button>
                </div>
            </form>
        `, () => {
            document.getElementById("cancel-create-modal")?.addEventListener("click", closeCreateModal);
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
        });

        bindListSearch("organizations-search", value => {
            listFilters.organizations = value;
            render();
        });
        bindPagination("#organizations-pagination-top", "auth-organizations", organizations, () => render());
    }

    async function renderAuthOrganizationDetail(organizationId, tab) {
        tab = organizationTabs.has(tab) ? tab : "general";
        const config = authViews.organizations;
        setHeader("Auth Server", config.title, "Manage organization details, memberships, and SSO in one place.");
        renderLoading("Loading organization details...");

        if (listFilters.orgUsersOrganizationId !== organizationId) {
            listFilters.orgUsers = "";
            listFilters.orgUsersOrganizationId = organizationId;
        }
        const orgUserSearch = listFilters.orgUsers;
        const orgUsersList = listQuery(`auth-org-${organizationId}-users`, 10, orgUserSearch);
        const usersPager = orgUsersList.pager;
        const invitationsPager = getPagerState(`auth-org-${organizationId}-invitations`, 50);
        const ssoPager = getPagerState(`auth-org-${organizationId}-sso`);
        const ssoPortalPager = getPagerState(`auth-org-${organizationId}-sso-portal`);
        const scimPager = getPagerState(`auth-org-${organizationId}-scim`);
        restartPagerWindow(`auth-org-${organizationId}-user-picker`);
        const userPickerPager = getPagerState(`auth-org-${organizationId}-user-picker`, 25);
        const scimConnectionsRequest = scimEnabled
            ? fetchJson(`${authApiBasePath}/organizations/${organizationId}/scim-connections?${pagerQuery(scimPager)}`)
            : Promise.resolve({ data: [], pageSize: 10, nextCursor: null, hasNextPage: false });
        const usersRequest = tab === "users"
            ? fetchJson(`${authApiBasePath}/users?${pagerQuery(userPickerPager)}`)
            : Promise.resolve({ data: [], pageSize: 25, nextCursor: null, hasNextPage: false });
        const [organization, users, memberships, invitations, ssoConnections, ssoPortalSessions, scimConnections] = await Promise.all([
            fetchJson(`${authApiBasePath}/organizations/${organizationId}`),
            usersRequest,
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/memberships?${orgUsersList.query}`),
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/invitations?${pagerQuery(invitationsPager)}`),
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/sso-connections?${pagerQuery(ssoPager)}`),
            fetchJson(`${authApiBasePath}/organizations/${organizationId}/sso-portal/sessions?${pagerQuery(ssoPortalPager)}`),
            scimConnectionsRequest
        ]);
        const organizationSsoConnections = Array.isArray(ssoConnections?.data) ? ssoConnections.data : [];
        const organizationSsoPortalSessions = Array.isArray(ssoPortalSessions?.data) ? ssoPortalSessions.data : [];
        const organizationScimConnections = Array.isArray(scimConnections?.data) ? scimConnections.data : [];
        const scimDetails = scimEnabled && tab === "scim"
            ? await Promise.all(organizationScimConnections.map(async connection => {
                restartPagerWindow(`auth-scim-${connection.id}-mappings`);
                restartPagerWindow(`auth-scim-${connection.id}-sync-events`);
                const [detail, mappings, syncEvents] = await Promise.all([
                    fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(connection.id)}`),
                    fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(connection.id)}/mappings?${pagerQuery(getPagerState(`auth-scim-${connection.id}-mappings`, 50))}`),
                    fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(connection.id)}/sync-events?${pagerQuery(getPagerState(`auth-scim-${connection.id}-sync-events`, 10))}`)
                ]);
                return { connection: detail, mappings, syncEvents };
            }))
            : [];
        const organizationInvitations = Array.isArray(invitations?.data) ? invitations.data : [];
        const pendingInvitations = organizationInvitations.filter(invitation => invitation.status === "pending").length;
        const latestOrganizationDraft = latestSsoDraft && latestSsoDraft.organizationId === organizationId ? latestSsoDraft : null;
        const latestOrganizationPortalSession = latestSsoPortalSession && latestSsoPortalSession.organizationId === organizationId ? latestSsoPortalSession : null;
        const latestOrganizationScimToken = latestScimToken && latestScimToken.organizationId === organizationId ? latestScimToken : null;
        const latestTokenConnection = latestOrganizationScimToken
            ? scimDetails.find(item => item.connection.id === latestOrganizationScimToken.connectionId)?.connection
            : null;
        const latestScimSetup = latestOrganizationScimToken
            ? {
                ...latestTokenConnection,
                ...latestOrganizationScimToken
            }
            : null;
        const scimProviderUrlReady = value => /^https:\/\//i.test(value || "");
        const scimNeedsPublicOrigin = latestScimSetup
            ? !scimProviderUrlReady(latestScimSetup.baseUrl)
            : scimDetails.some(item => !scimProviderUrlReady(item.connection.baseUrl));

        const summaryHtml = `
            <div class="detail-summary-grid">
                <div class="summary-card">
                    <div class="summary-label">Primary domain</div>
                    <div class="summary-value">${esc(organization.primaryDomain || "n/a")}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Members</div>
                    <div class="summary-value">${esc(organization.membershipCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Pending invites</div>
                    <div class="summary-value">${esc(pendingInvitations)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">SSO connections</div>
                    <div class="summary-value">${esc(organization.ssoConnectionCount || 0)}</div>
                </div>
            </div>
        `;

        const tabNav = `
            <div class="tab-strip">
                ${renderTabLink("general", "General", tab, organizationId)}
                ${renderTabLink("users", "Users", tab, organizationId)}
                ${renderTabLink("invitations", "Invitations", tab, organizationId)}
                ${renderTabLink("sso", "SSO", tab, organizationId)}
                ${scimEnabled ? renderTabLink("scim", "SCIM", tab, organizationId) : ""}
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
                            { label: "Members", value: organization.membershipCount || 0 },
                            { label: "Pending invitations", value: pendingInvitations },
                            { label: "Enabled SSO", value: organization.enabledSsoConnections ?? 0 }
                        ])}
                        <button type="button" id="revoke-organization-sessions">Revoke organization sessions</button>
                    </section>
                </div>
            `;
        } else if (tab === "users") {
            tabContent = `
                <section class="panel list-page">
                    ${renderListToolbar({
                        title: "Organization users",
                        searchId: "org-users-search",
                        searchPlaceholder: "Search name or email",
                        searchValue: orgUserSearch,
                        createLabel: "Add user",
                        pagerHtml: `<div id="organization-users-pagination-top">${renderPagination(usersPager, memberships)}</div>`
                    })}
                    ${renderListRows(
                        memberships.data,
                        item => renderListRow({
                            href: userDetailPath(item.userId, "general"),
                            title: item.user,
                            subtitle: item.userEmail || "",
                            metaHtml: [
                                renderChip(item.role, "amber"),
                                item.isActive === false ? renderChip("Inactive") : "",
                                renderIdChip(item.userId)
                            ].join("")
                        }),
                        "No memberships yet."
                    )}
                </section>
            `;
        } else if (tab === "invitations") {
            tabContent = `
                <section class="panel list-page">
                    ${renderListToolbar({
                        title: "Invitations",
                        createLabel: "Invite by email",
                        pagerHtml: `<div id="organization-invitations-pagination-top">${renderPagination(invitationsPager, invitations)}</div>`
                    })}
                    ${renderListRows(
                        organizationInvitations,
                        item => renderListRow({
                            title: item.email,
                            subtitle: [item.role, item.expiresAt ? `Expires ${formatDate(item.expiresAt)}` : ""].filter(Boolean).join(" · "),
                            metaHtml: [
                                renderChip(item.status, item.status === "pending" ? "amber" : item.status === "accepted" ? "green" : ""),
                                item.lastSendError ? renderChip("Delivery error") : "",
                                renderIdChip(item.id)
                            ].join(""),
                            actionsHtml: `<div class="form-actions">${item.inviteUrl ? `<button type="button" class="btn-secondary js-copy-invite" data-url="${esc(item.inviteUrl)}">Copy link</button>` : ""}${item.status === "pending" ? `<button type="button" class="btn-secondary js-resend-invite" data-id="${esc(item.id)}">Resend</button><button type="button" class="js-revoke-invite" data-id="${esc(item.id)}">Revoke</button>` : ""}</div>`
                        }),
                        "No invitations yet."
                    )}
                </section>
            `;
        } else if (tab === "sso") {
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
                    ${latestOrganizationPortalSession?.setupUrl ? `
                        <section class="panel">
                            <h2>Latest Delegated Setup Link</h2>
                            <div class="callout">
                                <div><strong>Portal session:</strong> ${esc(latestOrganizationPortalSession.id)}</div>
                                <div><strong>Expires</strong><br>${esc(formatDate(latestOrganizationPortalSession.expiresAt))}</div>
                                <div><strong>Setup URL</strong><br><span class="inline-code">${esc(latestOrganizationPortalSession.setupUrl)}</span></div>
                            </div>
                            <div class="form-actions">
                                <button type="button" class="js-copy-sso-portal-link" data-url="${esc(latestOrganizationPortalSession.setupUrl)}">Copy setup link</button>
                                <a class="button-link" href="${esc(latestOrganizationPortalSession.setupUrl)}" target="_blank" rel="noreferrer">Open portal</a>
                            </div>
                        </section>
                    ` : ""}
                    <div class="panel-grid">
                        <section class="panel">
                            <h2>Invite IT Admin</h2>
                            <p>Create a one-time setup link scoped to this organization. The first open establishes a server-side portal session.</p>
                            <form id="create-sso-portal-session-form">
                                <select name="provider">
                                    <option value="">Let admin choose provider</option>
                                    <option value="microsoft-entra">Microsoft Entra</option>
                                    <option value="okta">Okta</option>
                                    <option value="google-workspace">Google Workspace</option>
                                    <option value="generic-saml">Generic SAML</option>
                                </select>
                                <input name="createdByUserId" placeholder="Optional platform user id">
                                <button type="submit">Create setup link</button>
                            </form>
                        </section>
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
                                { label: "Total connections", value: organization.ssoConnectionCount || 0 },
                                { label: "Enabled connections", value: organization.enabledSsoConnections ?? 0 }
                            ])}
                        </section>
                    </div>
                    <section class="panel">
                        <h2>Delegated Portal Sessions</h2>
                        <div id="organization-sso-portal-pagination-top">${renderPagination(ssoPortalPager, ssoPortalSessions)}</div>
                        ${renderList(
                            organizationSsoPortalSessions,
                            item => `
                                <div class="list-item-header">
                                    <strong>${esc(item.id)}</strong>
                                    <span class="inline-code">${esc(item.status)}</span>
                                </div>
                                ${renderMetadataRows([
                                    { label: "Provider", value: item.provider || "Admin chooses" },
                                    { label: "Connection ID", value: item.connectionId || "n/a" },
                                    { label: "Created", value: formatDate(item.createdAt) },
                                    { label: "Expires", value: formatDate(item.expiresAt) },
                                    { label: "Opened", value: item.openedAt ? formatDate(item.openedAt) : "No" },
                                    { label: "Revoked", value: item.revokedAt ? formatDate(item.revokedAt) : "No" }
                                ])}
                                <div class="form-actions">
                                    ${item.status !== "revoked" && item.status !== "expired" ? `<button type="button" class="js-revoke-sso-portal-session" data-id="${esc(item.id)}">Revoke</button>` : ""}
                                </div>
                            `,
                            "No delegated portal sessions yet."
                        )}
                    </section>
                    <section class="panel">
                        <h2>Organization SSO Connections</h2>
                        <div id="organization-sso-pagination-top">${renderPagination(ssoPager, ssoConnections)}</div>
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
                                    { label: "Configuration owner", value: item.ownership?.owner || "dashboard" },
                                    { label: "Source key", value: item.ownership?.sourceKey || "n/a" },
                                    { label: "SP Entity ID", value: item.serviceProviderEntityId },
                                    { label: "ACS URL", value: item.assertionConsumerServiceUrl }
                                ])}
                                ${item.ownership && !item.ownership.isEditable ? `<div class="callout"><strong>Code owned:</strong> Update federation metadata and policy in the SAML seed. Emergency enable/disable remains available.</div>` : ""}
                                <form id="import-sso-metadata-${esc(item.id)}" class="nested-form">
                                    <textarea name="metadataXml" placeholder="Paste the Entra federation metadata XML" required></textarea>
                                    <button type="submit" ${item.ownership && !item.ownership.isEditable ? "disabled" : ""}>Import metadata</button>
                                </form>
                                <button type="button" data-saml-toggle="${esc(item.id)}" data-enabled="${item.isEnabled ? "true" : "false"}">${item.isEnabled ? "Emergency disable" : "Enable"}</button>
                            `,
                            "No SSO connections yet."
                        )}
                    </section>
                </div>
            `;
        } else {
            tabContent = `
                <div class="panel-stack">
                    ${latestScimSetup ? `
                        <section class="panel">
                            <h2>IdP setup values — copy now</h2>
                            <p>The bearer token is shown in plaintext only in this browser session. SqlOS stores only its hash, so copy it before reloading or leaving this page.</p>
                            ${!scimProviderUrlReady(latestScimSetup.baseUrl) ? `
                                <div class="callout">
                                    <strong>Set a public HTTPS origin before provider setup.</strong>
                                    The relative URL below works for same-origin curl requests, but Entra and Okta cannot reach it. Configure <span class="inline-code">AuthServer.PublicOrigin</span>, restart SqlOS, and then copy the refreshed Base URL.
                                </div>
                            ` : ""}
                            <div class="callout">
                                <div><strong>Connection:</strong> ${esc(latestScimSetup.connectionId)}</div>
                                <div><strong>Token prefix:</strong> ${esc(latestScimSetup.tokenPrefix || "n/a")}</div>
                                ${latestScimSetup.baseUrl ? `
                                    <div>
                                        <strong>Tenant / Base URL</strong><br>
                                        <span class="inline-code">${esc(latestScimSetup.baseUrl)}</span>
                                        <button type="button" class="js-copy-scim-value" data-label="Tenant / Base URL" data-value="${esc(latestScimSetup.baseUrl)}">Copy</button>
                                    </div>
                                ` : ""}
                                ${latestScimSetup.usersUrl ? `
                                    <div>
                                        <strong>Users URL</strong><br>
                                        <span class="inline-code">${esc(latestScimSetup.usersUrl)}</span>
                                        <button type="button" class="js-copy-scim-value" data-label="Users URL" data-value="${esc(latestScimSetup.usersUrl)}">Copy</button>
                                    </div>
                                ` : ""}
                                ${latestScimSetup.groupsUrl ? `
                                    <div>
                                        <strong>Groups URL</strong><br>
                                        <span class="inline-code">${esc(latestScimSetup.groupsUrl)}</span>
                                        <button type="button" class="js-copy-scim-value" data-label="Groups URL" data-value="${esc(latestScimSetup.groupsUrl)}">Copy</button>
                                    </div>
                                ` : ""}
                                <div>
                                    <strong>Secret / Bearer token</strong><br>
                                    <span class="inline-code">${esc(latestScimSetup.token)}</span>
                                    <button type="button" class="js-copy-scim-value" data-label="Secret token" data-value="${esc(latestScimSetup.token)}">Copy</button>
                                </div>
                            </div>
                        </section>
                    ` : ""}
                    <div class="panel-grid">
                        <section class="panel">
                            <h2>Create SCIM Connection</h2>
                            <p>Create an organization-scoped SCIM endpoint and initial bearer token in one step. Copy the returned setup values directly into your identity provider.</p>
                            <form id="create-scim-connection-form">
                                <input name="displayName" placeholder="Display name" value="${esc(organization.name)} SCIM" required>
                                <label class="checkbox-row"><input name="enabled" type="checkbox" checked> Connection is enabled</label>
                                <button type="submit">Create connection</button>
                            </form>
                        </section>
                        <section class="panel">
                            <h2>SCIM State</h2>
                            ${renderMetadataRows([
                                { label: "Enabled connections", value: organizationScimConnections.filter(item => item.isEnabled).length },
                                { label: "Last sync", value: formatDate(organizationScimConnections.map(item => item.lastSyncAt).filter(Boolean).sort().at(-1)) }
                            ])}
                        </section>
                        <section class="panel">
                            <h2>IdP Setup Values</h2>
                            <p>Use the connection base URL and bearer token in the provider app. SqlOS resolves the organization from the token.</p>
                            ${scimNeedsPublicOrigin ? `
                                <div class="callout">
                                    <strong>Provider URL is not public yet.</strong>
                                    Configure <span class="inline-code">AuthServer.PublicOrigin</span> with an HTTPS origin and restart before connecting Entra or Okta.
                                </div>
                            ` : ""}
                            ${renderMetadataRows([
                                { label: "Okta", value: "Provisioning > Integration: SCIM connector base URL, Bearer token, enable Push Groups." },
                                { label: "Microsoft Entra", value: "Enterprise App > Provisioning: Tenant URL, Secret Token, mappings for users and groups." },
                                { label: "Generic SCIM 2.0", value: "Use bearer auth, Users and Groups resources, and stable externalId values." }
                            ])}
                        </section>
                    </div>
                    <section class="panel">
                        <div class="panel-actions">
                            <div>
                                <h2>SCIM Connections</h2>
                                <p>Use one connection per identity-provider directory. Tokens are shown only once after creation or rotation.</p>
                            </div>
                            <div id="organization-scim-pagination-top">${renderPagination(scimPager, scimConnections)}</div>
                        </div>
                        ${renderList(
                            scimDetails,
                            item => `
                                <div class="list-item-header">
                                    <strong>${esc(item.connection.displayName)}</strong>
                                    <span class="inline-code">${item.connection.isEnabled ? "enabled" : "disabled"}</span>
                                </div>
                                ${renderMetadataRows([
                                    { label: "Connection ID", value: item.connection.id },
                                    { label: "Source", value: item.connection.source || "dashboard" },
                                    { label: "Configuration owner", value: item.connection.ownership?.owner || item.connection.source || "dashboard" },
                                    { label: "Source key", value: item.connection.ownership?.sourceKey },
                                    { label: "Base URL", html: `<span class="inline-code">${esc(item.connection.baseUrl || "n/a")}</span>` },
                                    { label: "Users URL", html: `<span class="inline-code">${esc(item.connection.usersUrl || "n/a")}</span>` },
                                    { label: "Groups URL", html: `<span class="inline-code">${esc(item.connection.groupsUrl || "n/a")}</span>` },
                                    { label: "Token prefix", value: item.connection.tokenPrefix || "No token rotated yet" },
                                    { label: "Token rotated", value: formatDate(item.connection.tokenRotatedAt) },
                                    { label: "Token last used", value: formatDate(item.connection.tokenLastUsedAt) },
                                    { label: "Last sync", value: formatDate(item.connection.lastSyncAt) }
                                ])}
                                ${item.connection.source === "seeded" ? `
                                    <div class="callout">
                                        <strong>Code owned.</strong>
                                        Change this connection's fields or token secret in <span class="inline-code">SeedScimConnection</span>. Emergency enable/disable remains available here and is preserved across restart.
                                    </div>
                                    <div class="form-actions">
                                        ${item.connection.isEnabled
                                            ? `<button type="button" class="js-disable-scim-connection" data-id="${esc(item.connection.id)}">Disable</button>`
                                            : `<button type="button" class="js-enable-scim-connection" data-id="${esc(item.connection.id)}">Enable</button>`}
                                    </div>
                                ` : `
                                    <form id="update-scim-connection-${esc(item.connection.id)}" class="nested-form">
                                        <input name="displayName" placeholder="Display name" value="${esc(item.connection.displayName)}" required>
                                        <label class="checkbox-row"><input name="enabled" type="checkbox" ${item.connection.isEnabled ? "checked" : ""}> Connection is enabled</label>
                                        <button type="submit">Save connection</button>
                                    </form>
                                    <div class="form-actions">
                                        <button type="button" class="js-rotate-scim-token" data-id="${esc(item.connection.id)}">Rotate token</button>
                                        ${item.connection.isEnabled
                                            ? `<button type="button" class="js-disable-scim-connection" data-id="${esc(item.connection.id)}">Disable</button>`
                                            : `<button type="button" class="js-enable-scim-connection" data-id="${esc(item.connection.id)}">Enable</button>`}
                                    </div>
                                `}
                                <details class="client-explainer" open>
                                    <summary>Group mapping rules</summary>
                                    <form id="create-scim-mapping-${esc(item.connection.id)}" class="nested-form">
                                        <select name="matchType">
                                            <option value="display_name">Display name</option>
                                            <option value="external_id">External ID</option>
                                            <option value="pattern">Pattern</option>
                                        </select>
                                        <input name="groupDisplayName" placeholder="Group display name">
                                        <input name="groupExternalId" placeholder="Group external ID">
                                        <input name="groupPattern" placeholder="Regex pattern with named captures">
                                        <input name="roleKey" placeholder="FGA role key" required>
                                        <input name="resourceId" placeholder="FGA resource ID">
                                        <input name="resourceIdTemplate" placeholder="Resource ID template, e.g. store_{storeId}">
                                        <input name="description" placeholder="Grant description">
                                        <label class="checkbox-row"><input name="enabled" type="checkbox" checked> Mapping is enabled</label>
                                        <button type="submit">Create mapping</button>
                                    </form>
                                    <div id="scim-mappings-${esc(item.connection.id)}">
                                        ${renderList(
                                            Array.isArray(item.mappings?.data) ? item.mappings.data : [],
                                            renderScimMappingItem,
                                            "No mapping rules yet."
                                        )}
                                        ${renderLoadMoreButton(`scim-mappings-more-${item.connection.id}`, !!item.mappings?.hasNextPage)}
                                    </div>
                                </details>
                                <details class="client-explainer">
                                    <summary>Recent sync events</summary>
                                    <div id="scim-sync-events-${esc(item.connection.id)}">
                                        ${renderList(
                                            Array.isArray(item.syncEvents?.data) ? item.syncEvents.data : [],
                                            renderScimSyncEventItem,
                                            "No sync events yet."
                                        )}
                                        ${renderLoadMoreButton(`scim-sync-events-more-${item.connection.id}`, !!item.syncEvents?.hasNextPage)}
                                    </div>
                                </details>
                            `,
                            "No SCIM connections yet."
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
            document.getElementById("revoke-organization-sessions")?.addEventListener("click", async () => {
                try {
                    const reason = window.prompt("Why are you revoking sessions for this organization?", "organization_revoked");
                    if (reason === null) return;
                    if (await revokeSessionsWithPreview({ organizationId, reason })) {
                        setFlash("success", "Organization sessions revoked.");
                    }
                } catch (error) {
                    setFlash("error", error.message || String(error));
                }
                await render();
            });
        } else if (tab === "users") {
            bindCreateModal("open-create-modal", "Add user", `
                <p>Create or update a membership for this organization.</p>
                <form id="create-org-membership-form">
                    ${renderRemotePicker({
                        searchId: "org-user-picker-search",
                        selectName: "userId",
                        selectId: "org-user-picker",
                        loadMoreId: "org-user-picker-more",
                        searchPlaceholder: "Search users by name or email",
                        emptyLabel: "Select a user",
                        required: true,
                        items: users.data || [],
                        hasNextPage: !!users.hasNextPage,
                        itemValue: user => user.id,
                        itemLabel: user => `${user.displayName}${user.defaultEmail ? ` (${user.defaultEmail})` : ""}`
                    })}
                    <input name="role" placeholder="Role" value="member" required>
                    <div class="modal-actions">
                        <button type="button" class="btn-secondary" id="cancel-create-modal">Cancel</button>
                        <button type="submit">Add membership</button>
                    </div>
                </form>
            `, () => {
                document.getElementById("cancel-create-modal")?.addEventListener("click", closeCreateModal);
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
                bindRemotePicker({
                    searchId: "org-user-picker-search",
                    selectId: "org-user-picker",
                    loadMoreId: "org-user-picker-more",
                    pagerKey: `auth-org-${organizationId}-user-picker`,
                    pageSize: 25,
                    emptyLabel: "Select a user",
                    initialResult: users,
                    itemValue: user => user.id,
                    itemLabel: user => `${user.displayName}${user.defaultEmail ? ` (${user.defaultEmail})` : ""}`,
                    fetchPage: (pager, search) => {
                        const params = new URLSearchParams(pagerQuery(pager));
                        if (search) {
                            params.set("search", search);
                        }
                        return fetchJson(`${authApiBasePath}/users?${params.toString()}`);
                    }
                });
            });
            bindListSearch("org-users-search", value => {
                listFilters.orgUsers = value;
                render();
            });
            bindPagination("#organization-users-pagination-top", `auth-org-${organizationId}-users`, memberships, () => render());
        } else if (tab === "invitations") {
            bindCreateModal("open-create-modal", "Invite by email", `
                <p>Send a one-time invitation link. The invited email must verify through OTP, SSO, existing login, or invite-backed signup before membership is activated.</p>
                <form id="create-org-invitation-form">
                    <input name="email" type="email" placeholder="Email address" required>
                    <input name="role" placeholder="Role" value="member" required>
                    <input name="clientId" placeholder="Optional client id">
                    <input name="redirectUri" placeholder="Optional redirect URI">
                    <label class="checkbox-row"><input name="sendEmail" type="checkbox" checked> Send invitation email now</label>
                    <div class="modal-actions">
                        <button type="button" class="btn-secondary" id="cancel-create-modal">Cancel</button>
                        <button type="submit">Send invitation</button>
                    </div>
                </form>
            `, () => {
                document.getElementById("cancel-create-modal")?.addEventListener("click", closeCreateModal);
                bindForm("create-org-invitation-form", async form => {
                await fetchJson(`${authApiBasePath}/organizations/${organizationId}/invitations`, {
                    method: "POST",
                    body: JSON.stringify({
                        email: form.get("email"),
                        role: form.get("role") || "member",
                        clientId: form.get("clientId") || null,
                        redirectUri: form.get("redirectUri") || null,
                        scope: null,
                        resource: null,
                        expiresAt: null,
                        customFields: null,
                        invitedByUserId: null,
                        sendEmail: form.get("sendEmail") === "on"
                    })
                });
                setFlash("success", "Invitation created.");
            });
            });

            document.querySelectorAll(".js-copy-invite").forEach(button => {
                button.addEventListener("click", async () => {
                    const url = button.dataset.url;
                    if (!url) {
                        return;
                    }

                    if (navigator.clipboard?.writeText) {
                        await navigator.clipboard.writeText(url);
                        setFlash("success", "Invitation link copied.");
                    } else {
                        window.prompt("Invitation link", url);
                    }
                    await render();
                });
            });

            document.querySelectorAll(".js-resend-invite").forEach(button => {
                button.addEventListener("click", async () => {
                    await fetchJson(`${authApiBasePath}/invitations/${encodeURIComponent(button.dataset.id)}/resend`, {
                        method: "POST",
                        body: JSON.stringify({})
                    });
                    setFlash("success", "Invitation resent.");
                    await render();
                });
            });

            document.querySelectorAll(".js-revoke-invite").forEach(button => {
                button.addEventListener("click", async () => {
                    await fetchJson(`${authApiBasePath}/invitations/${encodeURIComponent(button.dataset.id)}/revoke`, {
                        method: "POST",
                        body: JSON.stringify({ reason: "revoked_from_dashboard" })
                    });
                    setFlash("success", "Invitation revoked.");
                    await render();
                });
            });

            bindPagination("#organization-invitations-pagination-top", `auth-org-${organizationId}-invitations`, invitations, () => render());
        } else if (tab === "sso") {
            document.querySelectorAll(".js-copy-sso-portal-link").forEach(button => {
                button.addEventListener("click", async () => {
                    const url = button.dataset.url;
                    if (!url) {
                        return;
                    }

                    if (navigator.clipboard?.writeText) {
                        await navigator.clipboard.writeText(url);
                        setFlash("success", "SSO setup link copied.");
                    } else {
                        window.prompt("SSO setup link", url);
                    }
                    await render();
                });
            });

            bindForm("create-sso-portal-session-form", async form => {
                const result = await fetchJson(`${authApiBasePath}/organizations/${organizationId}/sso-portal/sessions`, {
                    method: "POST",
                    body: JSON.stringify({
                        organizationId,
                        provider: form.get("provider") || null,
                        createdByUserId: form.get("createdByUserId") || null,
                        expiresAt: null,
                        returnUrl: null
                    })
                });

                latestSsoPortalSession = result;
                if (result.setupUrl && navigator.clipboard?.writeText) {
                    await navigator.clipboard.writeText(result.setupUrl);
                }
                setFlash("success", "SSO setup link created.");
            });

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
                if (item.ownership && !item.ownership.isEditable) return;
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

            document.querySelectorAll("[data-saml-toggle]").forEach(button => {
                button.addEventListener("click", async () => {
                    const enabled = button.dataset.enabled === "true";
                    if (enabled && !window.confirm("Disable this SAML connection? New SSO sign-ins will stop until it is enabled again.")) return;
                    await fetchJson(`${authApiBasePath}/sso-connections/${encodeURIComponent(button.dataset.samlToggle)}/${enabled ? "disable" : "enable"}`, { method: "POST" });
                    setFlash("success", enabled ? "SAML connection disabled." : "SAML connection enabled.");
                    await render();
                });
            });

            document.querySelectorAll(".js-revoke-sso-portal-session").forEach(button => {
                button.addEventListener("click", async () => {
                    await fetchJson(`${authApiBasePath}/sso-portal/sessions/${encodeURIComponent(button.dataset.id)}/revoke`, {
                        method: "POST",
                        body: JSON.stringify({ reason: "revoked_from_dashboard" })
                    });
                    setFlash("success", "SSO portal session revoked.");
                    await render();
                });
            });

            bindPagination("#organization-sso-pagination-top", `auth-org-${organizationId}-sso`, ssoConnections, () => render());
            bindPagination("#organization-sso-portal-pagination-top", `auth-org-${organizationId}-sso-portal`, ssoPortalSessions, () => render());
        } else if (scimEnabled && tab === "scim") {
            document.querySelectorAll(".js-copy-scim-value").forEach(button => {
                button.addEventListener("click", async () => {
                    const value = button.dataset.value;
                    const label = button.dataset.label || "SCIM value";
                    if (!value) {
                        return;
                    }

                    try {
                        if (!navigator.clipboard?.writeText) {
                            throw new Error("Clipboard API unavailable");
                        }
                        await navigator.clipboard.writeText(value);
                        setFlash("success", `${label} copied.`);
                    } catch {
                        window.prompt(`Copy ${label}`, value);
                    }
                    await render();
                });
            });

            bindForm("create-scim-connection-form", async form => {
                const result = await fetchJson(`${authApiBasePath}/organizations/${organizationId}/scim-connections`, {
                    method: "POST",
                    body: JSON.stringify({
                        displayName: form.get("displayName"),
                        enabled: form.get("enabled") === "on"
                    })
                });
                latestScimToken = { ...result, organizationId };
                setFlash("success", "SCIM connection created. Copy the one-time IdP setup values now.");
            });

            document.querySelectorAll(".js-rotate-scim-token").forEach(button => {
                button.addEventListener("click", async () => {
                    if (!window.confirm("Rotate this SCIM token? The token currently configured in the IdP will stop working immediately. You must copy the replacement and update the IdP before its next provisioning request.")) {
                        return;
                    }
                    const result = await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(button.dataset.id)}/token/rotate`, {
                        method: "POST",
                        body: JSON.stringify({})
                    });
                    latestScimToken = { ...result, organizationId };
                    setFlash("success", "SCIM token rotated. Copy the replacement into the IdP now.");
                    await render();
                });
            });

            document.querySelectorAll(".js-enable-scim-connection, .js-disable-scim-connection").forEach(button => {
                button.addEventListener("click", async () => {
                    const action = button.classList.contains("js-enable-scim-connection") ? "enable" : "disable";
                    if (action === "disable" && !confirmScimConnectionDisable()) {
                        return;
                    }
                    await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(button.dataset.id)}/${action}`, {
                        method: "POST",
                        body: JSON.stringify({})
                    });
                    setFlash("success", `SCIM connection ${action}d.`);
                    await render();
                });
            });

            scimDetails.forEach(item => {
                bindForm(`update-scim-connection-${item.connection.id}`, async form => {
                    const enabled = form.get("enabled") === "on";
                    if (item.connection.isEnabled && !enabled && !confirmScimConnectionDisable()) {
                        return;
                    }
                    await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(item.connection.id)}`, {
                        method: "PUT",
                        body: JSON.stringify({
                            displayName: form.get("displayName"),
                            enabled
                        })
                    });
                    setFlash("success", "SCIM connection updated.");
                });

                bindForm(`create-scim-mapping-${item.connection.id}`, async form => {
                    await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(item.connection.id)}/mappings`, {
                        method: "POST",
                        body: JSON.stringify({
                            matchType: form.get("matchType"),
                            groupDisplayName: form.get("groupDisplayName") || null,
                            groupExternalId: form.get("groupExternalId") || null,
                            groupPattern: form.get("groupPattern") || null,
                            roleKey: form.get("roleKey"),
                            resourceId: form.get("resourceId") || null,
                            resourceIdTemplate: form.get("resourceIdTemplate") || null,
                            description: form.get("description") || null,
                            enabled: form.get("enabled") === "on"
                        })
                    });
                    setFlash("success", "SCIM mapping created.");
                });

                (Array.isArray(item.mappings?.data) ? item.mappings.data : []).forEach(bindScimMappingEditor);
            });

            bindPagination("#organization-scim-pagination-top", `auth-org-${organizationId}-scim`, scimConnections, () => render());

            scimDetails.forEach(item => {
                const mappingsButton = document.getElementById(`scim-mappings-more-${item.connection.id}`);
                mappingsButton?.addEventListener("click", async () => {
                    const pager = getPagerState(`auth-scim-${item.connection.id}-mappings`, 50);
                    if (!item.mappings?.hasNextPage) {
                        return;
                    }
                    if (pager.cursors[pager.index + 1] == null && item.mappings.nextCursor) {
                        pager.cursors.push(item.mappings.nextCursor);
                    }
                    pager.index += 1;
                    const next = await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(item.connection.id)}/mappings?${pagerQuery(pager)}`);
                    item.mappings = next;
                    appendListItems(`#scim-mappings-${item.connection.id}`, next.data || [], renderScimMappingItem);
                    (next.data || []).forEach(bindScimMappingEditor);
                    if (!next.hasNextPage) {
                        mappingsButton.remove();
                    }
                });

                const eventsButton = document.getElementById(`scim-sync-events-more-${item.connection.id}`);
                eventsButton?.addEventListener("click", async () => {
                    const pager = getPagerState(`auth-scim-${item.connection.id}-sync-events`, 10);
                    if (!item.syncEvents?.hasNextPage) {
                        return;
                    }
                    if (pager.cursors[pager.index + 1] == null && item.syncEvents.nextCursor) {
                        pager.cursors.push(item.syncEvents.nextCursor);
                    }
                    pager.index += 1;
                    const next = await fetchJson(`${authApiBasePath}/scim-connections/${encodeURIComponent(item.connection.id)}/sync-events?${pagerQuery(pager)}`);
                    item.syncEvents = next;
                    appendListItems(`#scim-sync-events-${item.connection.id}`, next.data || [], renderScimSyncEventItem);
                    if (!next.hasNextPage) {
                        eventsButton.remove();
                    }
                });
            });
        }
    }

    function bindScimMappingEditor(mapping) {
        if (!mapping || mapping.source === "seeded") {
            return;
        }

        bindForm(`update-scim-mapping-${mapping.id}`, async form => {
            await fetchJson(`${authApiBasePath}/scim-mappings/${encodeURIComponent(mapping.id)}`, {
                method: "PUT",
                body: JSON.stringify({
                    matchType: form.get("matchType"),
                    groupDisplayName: form.get("groupDisplayName") || null,
                    groupExternalId: form.get("groupExternalId") || null,
                    groupPattern: form.get("groupPattern") || null,
                    roleKey: form.get("roleKey"),
                    resourceId: form.get("resourceId") || null,
                    resourceIdTemplate: form.get("resourceIdTemplate") || null,
                    description: form.get("description") || null,
                    enabled: form.get("enabled") === "on"
                })
            });
            setFlash("success", "SCIM mapping updated.");
        });

        document.querySelectorAll(`[data-id="${mapping.id}"].js-enable-scim-mapping, [data-id="${mapping.id}"].js-disable-scim-mapping`).forEach(button => {
            button.addEventListener("click", async () => {
                const action = button.classList.contains("js-enable-scim-mapping") ? "enable" : "disable";
                await fetchJson(`${authApiBasePath}/scim-mappings/${encodeURIComponent(mapping.id)}/${action}`, {
                    method: "POST",
                    body: JSON.stringify({})
                });
                setFlash("success", `SCIM mapping ${action}d.`);
                await render();
            });
        });
    }

    function renderScimMappingItem(mapping) {
        return `
            <div class="list-item-header">
                <strong>${esc(mapping.roleKey)} -> ${esc(mapping.resourceId || mapping.resourceIdTemplate || "resource")}</strong>
                <span class="inline-code">${mapping.isEnabled ? "enabled" : "disabled"}</span>
            </div>
            ${renderMetadataRows([
                { label: "Mapping ID", value: mapping.id },
                { label: "Match type", value: mapping.matchType },
                { label: "Group display name", value: mapping.groupDisplayName || "n/a" },
                { label: "Group external ID", value: mapping.groupExternalId || "n/a" },
                { label: "Group pattern", value: mapping.groupPattern || "n/a" },
                { label: "Managed grants", value: mapping.activeGrantCount || 0 },
                { label: "Source", value: mapping.source || "dashboard" }
            ])}
            ${mapping.source === "seeded" ? `
                <div class="callout">
                    This mapping is managed by <span class="inline-code">SeedScimConnection</span>. Edit the seed definition and restart SqlOS to change or disable it.
                </div>
            ` : `<form id="update-scim-mapping-${esc(mapping.id)}" class="nested-form">
                <select name="matchType">
                    <option value="display_name" ${mapping.matchType === "display_name" ? "selected" : ""}>Display name</option>
                    <option value="external_id" ${mapping.matchType === "external_id" ? "selected" : ""}>External ID</option>
                    <option value="pattern" ${mapping.matchType === "pattern" ? "selected" : ""}>Pattern</option>
                </select>
                <input name="groupDisplayName" placeholder="Group display name" value="${esc(mapping.groupDisplayName || "")}">
                <input name="groupExternalId" placeholder="Group external ID" value="${esc(mapping.groupExternalId || "")}">
                <input name="groupPattern" placeholder="Regex pattern with named captures" value="${esc(mapping.groupPattern || "")}">
                <input name="roleKey" placeholder="FGA role key" value="${esc(mapping.roleKey || "")}" required>
                <input name="resourceId" placeholder="FGA resource ID" value="${esc(mapping.resourceId || "")}">
                <input name="resourceIdTemplate" placeholder="Resource ID template" value="${esc(mapping.resourceIdTemplate || "")}">
                <input name="description" placeholder="Grant description" value="${esc(mapping.description || "")}">
                <label class="checkbox-row"><input name="enabled" type="checkbox" ${mapping.isEnabled ? "checked" : ""}> Mapping is enabled</label>
                <button type="submit">Save mapping</button>
            </form>
            <div class="form-actions">
                ${mapping.isEnabled
                    ? `<button type="button" class="js-disable-scim-mapping" data-id="${esc(mapping.id)}">Disable</button>`
                    : `<button type="button" class="js-enable-scim-mapping" data-id="${esc(mapping.id)}">Enable</button>`}
            </div>`}
        `;
    }

    function renderScimSyncEventItem(event) {
        return `
            <div class="list-item-header">
                <strong>${esc(event.action)}</strong>
                <span class="inline-code">${esc(event.result)}</span>
            </div>
            ${renderMetadataRows([
                { label: "When", value: formatDate(event.occurredAt) },
                { label: "Resource", value: `${event.resourceType || "n/a"} ${event.resourceId || ""}`.trim() },
                { label: "External ID", value: event.externalId || "n/a" },
                { label: "Error", value: event.error || "n/a" },
                { label: "Request ID", value: event.requestId || "n/a" }
            ])}
        `;
    }

    function renderTabLink(tab, label, activeTab, organizationId) {
        const activeClass = tab === activeTab ? "active" : "";
        return `<a class="tab-link ${activeClass}" href="${esc(organizationDetailPath(organizationId, tab))}">${esc(label)}</a>`;
    }

    async function renderAuthUsers() {
        const config = authViews.users;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading users...");

        const search = listFilters.users;
        const { pager, query } = listQuery("auth-users", 10, search);
        const users = await fetchJson(`${authApiBasePath}/users?${query}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel list-page">
                ${renderListToolbar({
                    title: "Users",
                    searchId: "users-search",
                    searchPlaceholder: "Search name or email",
                    searchValue: search,
                    createLabel: "New user",
                    pagerHtml: `<div id="users-pagination-top">${renderPagination(pager, users)}</div>`
                })}
                ${renderListRows(
                    users.data,
                    item => renderListRow({
                        href: userDetailPath(item.id, "general"),
                        title: item.displayName,
                        subtitle: item.defaultEmail || "",
                        metaHtml: [
                            renderIdChip(item.id),
                            renderChip(item.membershipCount ? `${item.membershipCount} orgs` : ""),
                            item.isActive === false ? renderChip("Disabled") : ""
                        ].join("")
                    }),
                    "No users yet."
                )}
            </section>
        `;

        bindCreateModal("open-create-modal", "New user", `
            <p>Create a user and optionally assign a password credential immediately.</p>
            <form id="create-user-form">
                <input name="displayName" placeholder="Display name" required>
                <input name="email" placeholder="Email" required>
                <input name="password" type="password" placeholder="Password (optional)">
                <div class="modal-actions">
                    <button type="button" class="btn-secondary" id="cancel-create-modal">Cancel</button>
                    <button type="submit">Create user</button>
                </div>
            </form>
        `, () => {
            document.getElementById("cancel-create-modal")?.addEventListener("click", closeCreateModal);
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
        });

        bindListSearch("users-search", value => {
            listFilters.users = value;
            render();
        });
        bindPagination("#users-pagination-top", "auth-users", users, () => render());
    }

    async function renderAuthUserDetail(userId, tab) {
        const config = authViews.users;
        setHeader("Auth Server", config.title, "Inspect the user profile, organization memberships, and recent sessions.");
        renderLoading("Loading user details...");

        const membershipsPager = getPagerState(`auth-user-${userId}-memberships`);
        const sessionsPager = getPagerState(`auth-user-${userId}-sessions`);
        const [user, memberships, sessions] = await Promise.all([
            fetchJson(`${authApiBasePath}/users/${userId}`),
            fetchJson(`${authApiBasePath}/users/${userId}/memberships?${pagerQuery(membershipsPager)}`),
            fetchJson(`${authApiBasePath}/users/${userId}/sessions?${pagerQuery(sessionsPager)}`)
        ]);

        const summaryHtml = `
            <div class="detail-summary-grid">
                <div class="summary-card">
                    <div class="summary-label">Default email</div>
                    <div class="summary-value">${esc(user.defaultEmail || "n/a")}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Organizations</div>
                    <div class="summary-value">${esc(user.membershipCount || 0)}</div>
                </div>
                <div class="summary-card">
                    <div class="summary-label">Active sessions</div>
                    <div class="summary-value">${esc(user.sessionCount || 0)}</div>
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
                            { label: "Organizations", value: user.membershipCount || 0 },
                            { label: "Active sessions", value: user.sessionCount || 0 },
                            { label: "External identities", value: user.externalIdentityCount || 0 }
                        ])}
                    </section>
                    <section class="panel">
                        <h2>Account Actions</h2>
                        <p>Send a password reset email using the built-in password reset template.</p>
                        <form id="send-password-reset-email-form">
                            <input name="resetUrlTemplate" placeholder="Optional reset URL template with {token}">
                            <button type="submit" ${user.defaultEmail ? "" : "disabled"}>Send password reset email</button>
                        </form>
                        <button type="button" id="revoke-user-sessions">Sign out all user sessions</button>
                    </section>
                </div>
            `;
        } else if (tab === "organizations") {
            tabContent = `
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Organization Memberships</h2>
                        <div id="user-memberships-pagination-top">${renderPagination(membershipsPager, memberships)}</div>
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
                        <div id="user-sessions-pagination-top">${renderPagination(sessionsPager, sessions)}</div>
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
                                { label: "Revoked", value: item.revokedAt ? formatDate(item.revokedAt) : "Active" },
                                { label: "Revocation reason", value: item.revocationReason || "n/a" }
                            ])}
                            ${item.revokedAt ? "" : `<button type="button" class="js-revoke-user-session" data-session-id="${esc(item.id)}">Revoke session</button>`}
                            ${item.revokedAt ? `<a class="inline-link" href="${esc(pathForRoute("auth-audit"))}">Open audit history</a>` : ""}
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

        if (tab === "general") {
            bindForm("send-password-reset-email-form", async form => {
                await fetchJson(`${authApiBasePath}/users/${encodeURIComponent(userId)}/password-reset-email`, {
                    method: "POST",
                    body: JSON.stringify({
                        resetUrlTemplate: String(form.get("resetUrlTemplate") || "").trim() || null
                    })
                });
                setFlash("success", "Password reset email queued.");
            });
            document.getElementById("revoke-user-sessions")?.addEventListener("click", async () => {
                try {
                    const reason = window.prompt("Why are you signing out this user?", "user_sessions_revoked");
                    if (reason === null) return;
                    if (await revokeSessionsWithPreview({ userId, reason })) setFlash("success", "User sessions revoked.");
                } catch (error) {
                    setFlash("error", error.message || String(error));
                }
                await render();
            });
        } else if (tab === "organizations") {
            bindPagination("#user-memberships-pagination-top", `auth-user-${userId}-memberships`, memberships, () => render());
        } else if (tab === "sessions") {
            bindPagination("#user-sessions-pagination-top", `auth-user-${userId}-sessions`, sessions, () => render());
            document.querySelectorAll(".js-revoke-user-session").forEach(button => {
                button.addEventListener("click", async () => {
                    try {
                        const reason = window.prompt("Why are you revoking this session?", "admin_revoked");
                        if (reason === null) return;
                        if (await revokeSessionsWithPreview({ sessionId: button.dataset.sessionId, userId, reason })) {
                            setFlash("success", "Session revoked.");
                        }
                    } catch (error) {
                        setFlash("error", error.message || String(error));
                    }
                    await render();
                });
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

        const search = listFilters.memberships;
        const { pager, query } = listQuery("auth-memberships", 10, search);
        const memberships = await fetchJson(`${authApiBasePath}/memberships?${query}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel list-page">
                ${renderListToolbar({
                    title: "Memberships",
                    searchId: "memberships-search",
                    searchPlaceholder: "Search organization, user, or email",
                    searchValue: search,
                    createLabel: "New membership",
                    pagerHtml: `<div id="memberships-pagination-top">${renderPagination(pager, memberships)}</div>`
                })}
                ${renderListRows(
                    memberships.data,
                    item => renderListRow({
                        href: userDetailPath(item.userId, "general"),
                        title: item.user,
                        subtitle: [item.organization, item.userEmail].filter(Boolean).join(" · "),
                        metaHtml: [
                            renderChip(item.role, "amber"),
                            renderIdChip(item.organizationId)
                        ].join("")
                    }),
                    "No memberships yet."
                )}
            </section>
        `;

        bindCreateModal("open-create-modal", "New membership", `
            <p>Use IDs from the Organizations and Users pages.</p>
            <form id="create-membership-form">
                <input name="organizationId" placeholder="Organization ID" required>
                <input name="userId" placeholder="User ID" required>
                <input name="role" placeholder="Role" value="member" required>
                <div class="modal-actions">
                    <button type="button" class="btn-secondary" id="cancel-create-modal">Cancel</button>
                    <button type="submit">Create membership</button>
                </div>
            </form>
        `, () => {
            document.getElementById("cancel-create-modal")?.addEventListener("click", closeCreateModal);
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
        });

        bindListSearch("memberships-search", value => {
            listFilters.memberships = value;
            render();
        });
        bindPagination("#memberships-pagination-top", "auth-memberships", memberships, () => render());
    }

    async function renderAuthClients(route) {
        const config = authViews.clients;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading clients...");

        if (!suppressClientDraftSync) {
            syncClientDraftFromForm();
        }
        suppressClientDraftSync = false;
        selectedClientId = route?.clientApplicationId || null;
        const draft = ensureClientDraftState();
        let clientRuntimeConfig = {
            cimdEnabled: null,
            dcrEnabled: null,
            resourceIndicatorsEnabled: null,
            registrationEndpoint: null,
            issuer: null,
            authorizationEndpoint: null,
            tokenEndpoint: null,
            metadataUrl: `${authServerBasePath}/.well-known/oauth-authorization-server`
        };
        try {
            const metadata = await fetchJson(`${authServerBasePath}/.well-known/oauth-authorization-server`);
            clientRuntimeConfig = {
                cimdEnabled: metadata.client_id_metadata_document_supported === true,
                dcrEnabled: !!metadata.registration_endpoint,
                resourceIndicatorsEnabled: metadata.resource_parameter_supported === true,
                registrationEndpoint: metadata.registration_endpoint || null,
                issuer: metadata.issuer || null,
                authorizationEndpoint: metadata.authorization_endpoint || null,
                tokenEndpoint: metadata.token_endpoint || null,
                metadataUrl: `${authServerBasePath}/.well-known/oauth-authorization-server`
            };
        } catch {
            // Keep the clients page usable even if runtime metadata is not available.
        }
        const clientFilterKey = `${clientViewState.source}|${clientViewState.status}|${clientViewState.search}`;
        const pager = resetPager("auth-clients", 25, clientFilterKey);
        const params = new URLSearchParams(pagerQuery(pager));
        if (clientViewState.source !== "all") {
            params.set("source", clientViewState.source);
        }
        if (clientViewState.status !== "all") {
            params.set("status", clientViewState.status);
        }
        if (clientViewState.search) {
            params.set("search", clientViewState.search);
        }

        const clients = await fetchJson(`${authApiBasePath}/clients?${params.toString()}`);
        const clientItems = Array.isArray(clients.data) ? clients.data : [];
        if (pager.index === 0 && clients.summary) {
            pager.summary = clients.summary;
        }
        let clientDetail = null;
        let clientAccess = null;
        let clientCredentials = { data: [], hasNextPage: false, nextCursor: null };
        if (selectedClientId) {
            try {
                restartPagerWindow(`auth-client-${selectedClientId}-assignments`);
                restartPagerWindow(`auth-client-${selectedClientId}-credentials`);
                const assignmentPager = getPagerState(`auth-client-${selectedClientId}-assignments`, 10);
                const credentialPager = getPagerState(`auth-client-${selectedClientId}-credentials`, 10);
                clientDetail = await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(selectedClientId)}`);
                clientAccess = await fetchJson(`${authApiBasePath}/applications/${encodeURIComponent(clientDetail.id)}/assignments?includeRevoked=true&${pagerQuery(assignmentPager)}`);
                clientCredentials = await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientDetail.id)}/credentials?${pagerQuery(credentialPager)}`);
                selectedClientId = clientDetail.id;
            } catch {
                if (route?.clientApplicationId) {
                    history.replaceState({}, "", pathForRoute("auth-clients"));
                }
                selectedClientId = null;
            }
        }

        const preset = currentClientPreset();
        const summary = pager.summary || clients.summary || {};
        const activeCount = summary.activeCount ?? clientItems.filter(item => item.isActive && !item.disabledAt).length;
        const discoveredCount = summary.discoveredCount ?? clientItems.filter(item => item.registrationSource === "cimd").length;
        const registeredCount = summary.registeredCount ?? clientItems.filter(item => item.registrationSource === "dcr").length;
        const disabledCount = summary.disabledCount ?? clientItems.filter(item => !item.isActive || item.disabledAt).length;
        const selectedClientVisible = !!selectedClientId && clientItems.some(item => item.id === selectedClientId);
        const cimdStatus = describeFeatureStatus(clientRuntimeConfig.cimdEnabled);
        const dcrStatus = describeFeatureStatus(clientRuntimeConfig.dcrEnabled);
        const resourceIndicatorStatus = describeFeatureStatus(clientRuntimeConfig.resourceIndicatorsEnabled);
        const presetOwnership = describePresetOwnership(clientViewState.preset, preset);

        content.innerHTML = `
            ${consumeFlashHtml()}
            ${latestClientSecret ? `<section class="panel callout"><h2>Copy this client secret now</h2><p>SqlOS stores only a slow hash and cannot show this value again.</p><div class="inline-code">${esc(latestClientSecret.secret)}</div><p><strong>Client ID:</strong> ${esc(latestClientSecret.clientId)}</p><button id="client-secret-ack" type="button">I copied it</button></section>` : ""}
            <div class="panel-stack">
                <section class="panel">
                    <div class="panel-actions">
                        <div>
                            <h2>Runtime Client Onboarding Settings</h2>
                            <p>The dashboard reports these runtime settings, but you change them in <span class="inline-code">AddSqlOS(...)</span> startup code, not here.</p>
                        </div>
                        <a class="inline-link" href="${esc(clientOnboardingDocsUrl)}" target="_blank" rel="noreferrer">Open docs</a>
                    </div>
                    <div class="client-summary-grid">
                        <div class="client-summary-card">
                            <strong>CIMD</strong>
                            <span>Portable client metadata documents</span>
                            <div class="client-badge-row">${renderClientBadge(cimdStatus.label, cimdStatus.tone)}</div>
                        </div>
                        <div class="client-summary-card">
                            <strong>DCR</strong>
                            <span>Compatibility registration endpoint</span>
                            <div class="client-badge-row">${renderClientBadge(dcrStatus.label, dcrStatus.tone)}</div>
                        </div>
                        <div class="client-summary-card">
                            <strong>Resource indicators</strong>
                            <span>Resource-bound token audience support</span>
                            <div class="client-badge-row">${renderClientBadge(resourceIndicatorStatus.label, resourceIndicatorStatus.tone)}</div>
                        </div>
                    </div>
                    ${renderMetadataRows([
                        clientRuntimeConfig.issuer
                            ? { label: "Authorization Server Issuer", html: `<span class="inline-code">${esc(clientRuntimeConfig.issuer)}</span>` }
                            : null,
                        { label: "Discovery URL", html: `<span class="inline-code">${esc(clientRuntimeConfig.metadataUrl)}</span>` },
                        clientRuntimeConfig.authorizationEndpoint
                            ? { label: "Authorization endpoint", html: `<span class="inline-code">${esc(clientRuntimeConfig.authorizationEndpoint)}</span>` }
                            : null,
                        clientRuntimeConfig.tokenEndpoint
                            ? { label: "Token endpoint", html: `<span class="inline-code">${esc(clientRuntimeConfig.tokenEndpoint)}</span>` }
                            : null
                    ].filter(Boolean))}
                    <div class="callout">
                        <strong>Change these in startup code:</strong>
                        <div><strong>For app integrations:</strong> Use the discovery URL above or copy the authorization server issuer directly into your client configuration.</div>
                        <div><span class="inline-code">options.AuthServer.EnablePortableMcpClients(...)</span> or <span class="inline-code">options.AuthServer.ClientRegistration.Cimd.Enabled</span> for CIMD.</div>
                        <div><span class="inline-code">options.AuthServer.EnableChatGptCompatibility(...)</span> or <span class="inline-code">options.AuthServer.ClientRegistration.Dcr.Enabled = true</span> for DCR.</div>
                        ${clientRuntimeConfig.registrationEndpoint ? `<div><strong>DCR endpoint:</strong> <span class="inline-code">${esc(clientRuntimeConfig.registrationEndpoint)}</span></div>` : ""}
                        <div><strong>Repo doc:</strong> <span class="inline-code">docs/CONFIGURATION.md</span></div>
                    </div>
                    <details class="client-explainer">
                        <summary>What are first-party and third-party clients?</summary>
                        <div class="client-explainer-grid">
                            <div class="client-explainer-card">
                                <strong>First-party clients</strong>
                                <p>Apps your team owns. They usually come from startup seeding in <span class="inline-code">AddSqlOS(...)</span> or are created here for local and development workflows.</p>
                            </div>
                            <div class="client-explainer-card">
                                <strong>Third-party clients</strong>
                                <p>External apps talking to your auth server. They usually appear automatically as <em>Discovered</em> (CIMD) or <em>Registered</em> (DCR) clients.</p>
                            </div>
                            <div class="client-explainer-card">
                                <strong>Source labels</strong>
                                <p><em>Seeded</em> comes from startup code. <em>Manual</em> comes from this dashboard. <em>Discovered</em> comes from CIMD. <em>Registered</em> comes from DCR.</p>
                            </div>
                        </div>
                    </details>
                </section>
                <section class="panel">
                    <h2>Choose a Starter Template</h2>
                    <p>Click a template to populate the manual client form below. Nothing is created until you submit the form.</p>
                    <div class="client-preset-grid">
                        ${Object.entries(clientPresetDefinitions).map(([key, value]) => `
                            <button type="button" class="client-preset-card ${clientViewState.preset === key ? "client-preset-card--active" : ""}" data-client-preset="${esc(key)}">
                                <strong>${esc(value.title)}</strong>
                                <span>${esc(value.description)}</span>
                                <em>${clientViewState.preset === key ? "Selected below" : "Click to fill form"}</em>
                            </button>
                        `).join("")}
                    </div>
                </section>
                <section class="panel">
                    <h2>Client Overview</h2>
                    <div class="client-summary-grid">
                        <div class="client-summary-card"><strong>${esc(String(activeCount))}</strong><span>Active</span></div>
                        <div class="client-summary-card"><strong>${esc(String(discoveredCount))}</strong><span>Discovered</span></div>
                        <div class="client-summary-card"><strong>${esc(String(registeredCount))}</strong><span>Registered</span></div>
                        <div class="client-summary-card"><strong>${esc(String(disabledCount))}</strong><span>Disabled</span></div>
                    </div>
                </section>
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Create Manual Client</h2>
                        <p>Use the selected template as a starting point, then edit any field you need before saving.</p>
                        <div class="client-badge-row">
                            ${renderClientBadge(`Preset: ${preset.title}`, "info")}
                            ${renderClientBadge(presetOwnership.label, presetOwnership.tone)}
                        </div>
                        <p class="client-form-help">${esc(presetOwnership.description)}</p>
                        <form id="create-client-form">
                            <input name="clientId" placeholder="${esc(preset.clientIdHint)}" value="${esc(draft.clientId)}" required>
                            <input name="name" placeholder="${esc(preset.name || "Display name")}" value="${esc(draft.name)}" required>
                            <input name="audience" placeholder="Audience" value="${esc(draft.audience)}">
                            <textarea name="redirectUris" placeholder="One redirect URI per line">${esc(draft.redirectUris)}</textarea>
                            <details>
                                <summary>Advanced fields</summary>
                                <div class="client-advanced-grid">
                                    <textarea name="description" placeholder="Optional description">${esc(draft.description)}</textarea>
                                    <textarea name="allowedScopes" placeholder="Optional scopes, one per line">${esc(draft.allowedScopes)}</textarea>
                                    <label class="checkbox-row"><input name="requirePkce" type="checkbox" ${draft.requirePkce ? "checked" : ""}> Require PKCE</label>
                                    <label class="checkbox-row"><input name="allowDeviceAuthorization" type="checkbox" ${draft.allowDeviceAuthorization ? "checked" : ""}> Allow device authorization</label>
                                    <label class="checkbox-row"><input name="confidential" type="checkbox" ${draft.confidential ? "checked" : ""}> Confidential client (issue a client secret)</label>
                                </div>
                            </details>
                            <button type="submit">Create manual client</button>
                        </form>
                        <div class="callout">
                            <strong>This form creates manual client records.</strong>
                            <div>Use owned templates for first-party apps you control. Use portable or compatibility templates for manual local testing of third-party-style clients.</div>
                            <div>Seeded clients come from application code. Discovered and registered clients usually appear automatically when CIMD or DCR is enabled.</div>
                        </div>
                    </section>
                    <section class="panel">
                        <h2>Inspect Client</h2>
                        ${clientDetail ? `
                            <div class="client-detail-stack">
                                <div class="client-list-header">
                                    <div>
                                        <strong>${esc(clientDetail.name)}</strong>
                                        <div class="client-badge-row">${renderClientSourceBadges(clientDetail)}</div>
                                    </div>
                                    <div class="client-action-row">
                                        <a class="inline-link" href="${esc(pathForRoute("auth-clients"))}" data-dashboard-route="auth-clients">Close inspect</a>
                                        <button type="button" data-client-action="${clientDetail.isActive && !clientDetail.disabledAt ? "disable" : "enable"}" data-client-id="${esc(clientDetail.id)}">
                                            ${clientDetail.isActive && !clientDetail.disabledAt ? "Disable" : "Enable"}
                                        </button>
                                        <button type="button" data-client-action="revoke" data-client-id="${esc(clientDetail.id)}">Revoke sessions</button>
                                    </div>
                                </div>
                                ${!selectedClientVisible ? `
                                    <div class="callout">
                                        <strong>Selected client is outside the current list view.</strong>
                                        Current filters or pagination may hide it from the list below.
                                    </div>
                                ` : ""}
                                ${renderMetadataRows([
                                    { label: "Internal ID", value: clientDetail.id },
                                    { label: "Client ID", value: clientDetail.clientId },
                                    { label: "Description", value: clientDetail.description || "n/a" },
                                    { label: "Audience", value: clientDetail.audience },
                                    { label: "Access mode", value: clientDetail.accessMode || "all_organizations" },
                                    { label: "Source", value: clientDetail.sourceLabel },
                                    { label: "Lifecycle", value: clientDetail.lifecycleState },
                                    { label: "Require PKCE", value: clientDetail.requirePkce ? "Yes" : "No" },
                                    { label: "First-party", value: clientDetail.isFirstParty ? "Yes" : "No" },
                                    { label: "Device OAuth", value: clientDetail.allowDeviceAuthorization ? "Enabled" : "Disabled" },
                                    { label: "Token auth method", value: clientDetail.tokenEndpointAuthMethod },
                                    { label: "Core metadata editable", value: clientDetail.coreMetadataEditable ? "Yes" : "No" },
                                    { label: "Last seen", value: formatDate(clientDetail.lastSeenAt) },
                                    { label: "Disabled reason", value: clientDetail.disabledReason || "n/a" },
                                    { label: "Metadata document", value: clientDetail.metadataDocumentUrl || "n/a" },
                                    { label: "Metadata cache", value: clientDetail.metadataCacheState || "n/a" },
                                    { label: "Fetched", value: formatDate(clientDetail.metadataFetchedAt) },
                                    { label: "Expires", value: formatDate(clientDetail.metadataExpiresAt) },
                                    { label: "Client URI", value: clientDetail.clientUri || "n/a" },
                                    { label: "Logo URI", value: clientDetail.logoUri || "n/a" },
                                    { label: "Software ID", value: clientDetail.softwareId || "n/a" },
                                    { label: "Software version", value: clientDetail.softwareVersion || "n/a" },
                                    { label: "Duplicate fingerprint", value: clientDetail.duplicateFingerprint || "n/a" },
                                    { label: "Duplicate count", value: clientDetail.duplicateCount || 0 },
                                    {
                                        label: "Redirect URIs",
                                        value: clientDetail.redirectUris.length ? "" : "none",
                                        html: clientDetail.redirectUris.length
                                            ? clientDetail.redirectUris.map(uri => `<div class="inline-code">${esc(uri)}</div>`).join("")
                                            : "none"
                                    },
                                    {
                                        label: "Grant types",
                                        value: clientDetail.grantTypes.length ? clientDetail.grantTypes.join(", ") : "n/a"
                                    },
                                    {
                                        label: "Response types",
                                        value: clientDetail.responseTypes.length ? clientDetail.responseTypes.join(", ") : "n/a"
                                    },
                                    {
                                        label: "Allowed scopes",
                                        value: clientDetail.allowedScopes.length ? clientDetail.allowedScopes.join(", ") : "n/a"
                                    }
                                ])}
                                <div>
                                    <h3>Access</h3>
                                    ${clientDetail.ownership && !clientDetail.ownership.isEditable ? `<div class="callout"><strong>Code owned access mode:</strong> Change <span class="inline-code">AccessMode</span> in the client seed. Dashboard-created assignments remain available and survive reconciliation.</div>` : ""}
                                    <form id="application-access-mode-form" class="client-filter-form">
                                        <select name="accessMode" ${clientDetail.ownership && !clientDetail.ownership.isEditable ? "disabled" : ""}>
                                            ${["all_organizations", "selected_organizations", "selected_users_groups_roles", "internal_only", "disabled"].map(mode => `
                                                <option value="${esc(mode)}" ${(clientAccess?.accessMode || clientDetail.accessMode || "all_organizations") === mode ? "selected" : ""}>${esc(mode)}</option>
                                            `).join("")}
                                        </select>
                                        <button type="submit" ${clientDetail.ownership && !clientDetail.ownership.isEditable ? "disabled" : ""}>Save access mode</button>
                                    </form>
                                    <form id="create-application-assignment-form" class="client-assignment-form">
                                        <select name="principalType">
                                            <option value="organization">Organization</option>
                                            <option value="user">User</option>
                                            <option value="group">Group</option>
                                            <option value="role">Role</option>
                                            <option value="service_account">Service account</option>
                                            <option value="agent">Agent</option>
                                        </select>
                                        <input name="organizationId" placeholder="Organization ID">
                                        <input name="principalId" placeholder="User or group ID">
                                        <input name="roleKey" placeholder="Role key">
                                        <select name="access">
                                            <option value="allowed">Allowed</option>
                                            <option value="denied">Denied</option>
                                        </select>
                                        <input name="reason" placeholder="Reason">
                                        <button type="submit">Add assignment</button>
                                    </form>
                                    <div id="client-assignments-list">
                                    ${renderList(
                                        clientAccess?.data || [],
                                        renderClientAssignmentItem,
                                        "No assignments for this application."
                                    )}
                                    ${renderLoadMoreButton("client-assignments-more", !!clientAccess?.hasNextPage)}
                                    </div>
                                </div>
                                <div>
                                    <h3>Client credentials</h3>
                                    <p>Credentials are write-only. Creating another credential allows an overlap window while callers move to the new secret.</p>
                                    ${clientDetail.tokenEndpointAuthMethod === "client_secret_basic" && clientDetail.ownership?.isEditable ? `
                                        <form id="create-client-credential-form">
                                            <input name="displayName" placeholder="Credential label, for example July rotation">
                                            <input name="expiresAt" type="datetime-local">
                                            <button type="submit">Create credential</button>
                                        </form>
                                    ` : ""}
                                    <div id="client-credentials-list">
                                    ${renderList(
                                        clientCredentials.data || [],
                                        renderClientCredentialItem,
                                        "No client credentials configured."
                                    )}
                                    ${renderLoadMoreButton("client-credentials-more", !!clientCredentials.hasNextPage)}
                                    </div>
                                </div>
                                <details>
                                    <summary>Raw metadata</summary>
                                    <pre class="json-preview">${esc(formatJson(clientDetail.metadataJson))}</pre>
                                </details>
                                <div>
                                    <h3>Recent client audit</h3>
                                    ${renderList(
                                        clientDetail.recentAuditEvents || [],
                                        item => `
                                            <strong>${esc(item.eventType)}</strong>
                                            ${renderMetadataRows([
                                                { label: "When", value: formatDate(item.occurredAt) },
                                                { label: "Actor", value: item.actorType },
                                                { label: "Actor ID", value: item.actorId || "n/a" }
                                            ])}
                                        `,
                                        "No recent client audit events."
                                    )}
                                </div>
                            </div>
                        ` : `<div class="empty-state-block">Select a client from the list to inspect metadata, lifecycle state, and recent audit activity.</div>`}
                    </section>
                </div>
                <section class="panel">
                    <h2>Clients</h2>
                    <p>Filter by source or lifecycle state, inspect discovered metadata, and perform operator actions without leaving the dashboard.</p>
                    <form id="client-filter-form" class="client-filter-form">
                        <select name="source">
                            <option value="all" ${clientViewState.source === "all" ? "selected" : ""}>All sources</option>
                            <option value="seeded" ${clientViewState.source === "seeded" ? "selected" : ""}>Seeded</option>
                            <option value="manual" ${clientViewState.source === "manual" ? "selected" : ""}>Manual</option>
                            <option value="cimd" ${clientViewState.source === "cimd" ? "selected" : ""}>Discovered</option>
                            <option value="dcr" ${clientViewState.source === "dcr" ? "selected" : ""}>Registered</option>
                        </select>
                        <select name="status">
                            <option value="all" ${clientViewState.status === "all" ? "selected" : ""}>All states</option>
                            <option value="active" ${clientViewState.status === "active" ? "selected" : ""}>Active</option>
                            <option value="disabled" ${clientViewState.status === "disabled" ? "selected" : ""}>Disabled</option>
                        </select>
                        <input name="search" placeholder="Search name, client ID, audience, software, metadata URL, or description" value="${esc(clientViewState.search)}">
                        <button type="submit">Apply filters</button>
                        <button type="button" id="client-filter-reset">Reset</button>
                    </form>
                    <div id="clients-pagination-top">${renderPagination(pager, clients)}</div>
                    ${renderList(
                        clientItems,
                        item => `
                            <div class="client-list-row ${selectedClientId === item.id ? "client-list-row--selected" : ""}">
                                <div class="client-list-header">
                                    <div>
                                        <strong>${esc(item.name)}</strong>
                                        <div class="client-badge-row">${renderClientSourceBadges(item)}</div>
                                    </div>
                                    <div class="client-action-row">
                                        <button type="button" data-client-action="inspect" data-client-id="${esc(item.id)}">Inspect</button>
                                        <button type="button" data-client-action="${item.isActive && !item.disabledAt ? "disable" : "enable"}" data-client-id="${esc(item.id)}">
                                            ${item.isActive && !item.disabledAt ? "Disable" : "Enable"}
                                        </button>
                                        <button type="button" data-client-action="revoke" data-client-id="${esc(item.id)}">Revoke sessions</button>
                                    </div>
                                </div>
                                ${renderMetadataRows([
                                    { label: "Client ID", value: item.clientId },
                                    { label: "Audience", value: item.audience },
                                    { label: "Source", value: item.sourceLabel },
                                    { label: "Last seen", value: formatDate(item.lastSeenAt) },
                                    { label: "Token auth method", value: item.tokenEndpointAuthMethod },
                                    { label: "Metadata document", value: item.metadataDocumentUrl || "n/a" },
                                    { label: "Software", value: item.softwareId ? `${item.softwareId}${item.softwareVersion ? ` (${item.softwareVersion})` : ""}` : "n/a" }
                                ])}
                            </div>
                        `,
                        "No clients match the current filter."
                    )}
                </section>
            </div>
        `;

        bindForm("client-filter-form", async form => {
            clientViewState.source = form.get("source") || "all";
            clientViewState.status = form.get("status") || "all";
            clientViewState.search = String(form.get("search") || "").trim();
        });

        document.querySelectorAll("[data-client-preset]").forEach(button => {
            button.addEventListener("click", async () => {
                applyClientPreset(button.getAttribute("data-client-preset") || "owned-web");
                await render();
            });
        });

        document.getElementById("client-filter-reset")?.addEventListener("click", async () => {
            clientViewState.source = "all";
            clientViewState.status = "all";
            clientViewState.search = "";
            await render();
        });

        bindForm("create-client-form", async form => {
            const confidential = form.get("confidential") === "on";
            const created = await fetchJson(`${authApiBasePath}/clients`, {
                method: "POST",
                body: JSON.stringify({
                    clientId: form.get("clientId"),
                    name: form.get("name"),
                    audience: form.get("audience") || "sqlos",
                    redirectUris: String(form.get("redirectUris") || "")
                        .split("\n")
                        .map(value => value.trim())
                        .filter(Boolean),
                    description: form.get("description") || null,
                    allowedScopes: String(form.get("allowedScopes") || "")
                        .split("\n")
                        .map(value => value.trim())
                        .filter(Boolean),
                    requirePkce: form.get("requirePkce") === "on",
                    isFirstParty: draft.isFirstParty,
                    allowDeviceAuthorization: form.get("allowDeviceAuthorization") === "on",
                    clientType: confidential ? "confidential" : form.get("allowDeviceAuthorization") === "on" ? "public_cli" : "public_pkce"
                })
            });
            if (confidential) {
                const credential = await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(created.id)}/credentials`, {
                    method: "POST",
                    body: JSON.stringify({ displayName: "Initial client credential", expiresAt: null })
                });
                latestClientSecret = { clientId: created.clientId, secret: credential.clientSecret };
            }
            setFlash("success", confidential ? "Confidential client created. Copy its secret now." : "Manual client created.");
            clientDraftState = createClientDraftFromPreset(clientViewState.preset);
            suppressClientDraftSync = true;
            restartPagerWindow("auth-clients");
        });

        document.getElementById("client-secret-ack")?.addEventListener("click", async () => {
            latestClientSecret = null;
            await render();
        });

        bindForm("create-client-credential-form", async form => {
            if (!clientDetail) {
                return;
            }
            const result = await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientDetail.id)}/credentials`, {
                method: "POST",
                body: JSON.stringify({
                    displayName: form.get("displayName") || null,
                    expiresAt: form.get("expiresAt") ? new Date(form.get("expiresAt")).toISOString() : null
                })
            });
            latestClientSecret = { clientId: clientDetail.clientId, secret: result.clientSecret };
            setFlash("success", "Client credential created. Existing active credentials remain valid until revoked.");
        });

        bindClientCredentialRevokeButtons(clientDetail);

        bindPagination("#clients-pagination-top", "auth-clients", clients, () => render());

        document.getElementById("client-assignments-more")?.addEventListener("click", async () => {
            if (!clientDetail || !clientAccess?.hasNextPage) {
                return;
            }
            const assignmentPager = getPagerState(`auth-client-${clientDetail.id}-assignments`, 10);
            if (assignmentPager.cursors[assignmentPager.index + 1] == null && clientAccess.nextCursor) {
                assignmentPager.cursors.push(clientAccess.nextCursor);
            }
            assignmentPager.index += 1;
            const next = await fetchJson(`${authApiBasePath}/applications/${encodeURIComponent(clientDetail.id)}/assignments?includeRevoked=true&${pagerQuery(assignmentPager)}`);
            clientAccess = next;
            appendListItems("#client-assignments-list", next.data || [], renderClientAssignmentItem);
            bindClientAssignmentRevokeButtons(clientDetail);
            if (!next.hasNextPage) {
                document.getElementById("client-assignments-more")?.remove();
            }
        });

        document.getElementById("client-credentials-more")?.addEventListener("click", async () => {
            if (!clientDetail || !clientCredentials?.hasNextPage) {
                return;
            }
            const credentialPager = getPagerState(`auth-client-${clientDetail.id}-credentials`, 10);
            if (credentialPager.cursors[credentialPager.index + 1] == null && clientCredentials.nextCursor) {
                credentialPager.cursors.push(clientCredentials.nextCursor);
            }
            credentialPager.index += 1;
            const next = await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientDetail.id)}/credentials?${pagerQuery(credentialPager)}`);
            clientCredentials = next;
            appendListItems("#client-credentials-list", next.data || [], renderClientCredentialItem);
            bindClientCredentialRevokeButtons(clientDetail);
            if (!next.hasNextPage) {
                document.getElementById("client-credentials-more")?.remove();
            }
        });

        bindForm("application-access-mode-form", async form => {
            if (!clientDetail) {
                return;
            }

            await fetchJson(`${authApiBasePath}/applications/${encodeURIComponent(clientDetail.id)}/access-mode`, {
                method: "POST",
                body: JSON.stringify({ accessMode: form.get("accessMode") })
            });
            setFlash("success", "Application access mode updated.");
        });

        bindForm("create-application-assignment-form", async form => {
            if (!clientDetail) {
                return;
            }

            await fetchJson(`${authApiBasePath}/applications/${encodeURIComponent(clientDetail.id)}/assignments`, {
                method: "POST",
                body: JSON.stringify({
                    principalType: form.get("principalType"),
                    organizationId: form.get("organizationId") || null,
                    principalId: form.get("principalId") || null,
                    roleKey: form.get("roleKey") || null,
                    access: form.get("access") || "allowed",
                    reason: form.get("reason") || null
                })
            });
            setFlash("success", "Application assignment added.");
        });

        bindClientAssignmentRevokeButtons(clientDetail);

        document.querySelectorAll("[data-client-action]").forEach(button => {
            button.addEventListener("click", async () => {
                const action = button.getAttribute("data-client-action");
                const clientId = button.getAttribute("data-client-id");
                if (!action || !clientId) {
                    return;
                }

                try {
                    if (action === "inspect") {
                        history.pushState({}, "", clientDetailPath(clientId));
                    } else if (action === "disable") {
                        const reason = window.prompt("Why are you disabling this client?", "disabled_by_operator");
                        if (reason === null) {
                            return;
                        }

                        await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientId)}/disable`, {
                            method: "POST",
                            body: JSON.stringify({ reason })
                        });
                        setFlash("success", "Client disabled.");
                    } else if (action === "enable") {
                        await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientId)}/enable`, {
                            method: "POST",
                            body: JSON.stringify({})
                        });
                        setFlash("success", "Client enabled.");
                    } else if (action === "revoke") {
                        const reason = window.prompt("Why are you revoking sessions for this client?", "client_revoked");
                        if (reason === null) {
                            return;
                        }

                        if (await revokeSessionsWithPreview({ clientApplicationId: clientId, reason })) {
                            setFlash("success", "Client sessions revoked.");
                        }
                    }
                } catch (error) {
                    setFlash("error", error.message || String(error));
                }

                await render();
            });
        });

        if (focusClientFormAfterPreset) {
            focusClientFormAfterPreset = false;
            const form = document.getElementById("create-client-form");
            form?.scrollIntoView({ behavior: "smooth", block: "start" });
            form?.querySelector("input[name=\"clientId\"]")?.focus();
        }
    }

    function renderClientAssignmentItem(assignment) {
        return `
            <div class="client-list-row">
                <div class="client-list-header">
                    <div>
                        <strong>${esc(assignment.principalType)}</strong>
                        <div class="client-badge-row">
                            ${renderClientBadge(assignment.access, assignment.access === "denied" ? "danger" : "success")}
                            ${assignment.revokedAt ? renderClientBadge("Revoked", "muted") : ""}
                            ${renderClientBadge(assignment.ownership?.owner === "code" ? "Code owned" : "Dashboard owned", assignment.ownership?.owner === "code" ? "info" : "muted")}
                        </div>
                    </div>
                    ${assignment.revokedAt || (assignment.ownership && !assignment.ownership.isEditable) ? "" : `
                        <button type="button" data-application-assignment-revoke="${esc(assignment.id)}">Revoke</button>
                    `}
                </div>
                ${renderMetadataRows([
                    { label: "Organization", value: assignment.organization || assignment.organizationId || "n/a" },
                    { label: "Principal ID", value: assignment.principalId || "n/a" },
                    { label: "Role key", value: assignment.roleKey || "n/a" },
                    { label: "Reason", value: assignment.reason || "n/a" },
                    { label: "Source key", value: assignment.ownership?.sourceKey || "n/a" },
                    { label: "Created", value: formatDate(assignment.createdAt) }
                ])}
            </div>
        `;
    }

    function renderClientCredentialItem(credential) {
        return `
            <div class="client-list-row">
                <div class="client-list-header">
                    <strong>${esc(credential.displayName || credential.id)}</strong>
                    ${credential.revokedAt || credential.configurationOwner !== "dashboard" ? "" : `<button type="button" data-client-credential-revoke="${esc(credential.id)}">Revoke</button>`}
                </div>
                ${renderMetadataRows([
                    { label: "Credential ID", value: credential.id },
                    { label: "Owner", value: credential.configurationOwner },
                    { label: "Created", value: formatDate(credential.createdAt) },
                    { label: "Expires", value: formatDate(credential.expiresAt) },
                    { label: "Last used", value: formatDate(credential.lastUsedAt) },
                    { label: "Status", value: credential.revokedAt ? `Revoked ${formatDate(credential.revokedAt)}` : "Active" }
                ])}
            </div>
        `;
    }

    function bindClientAssignmentRevokeButtons(clientDetail) {
        document.querySelectorAll("[data-application-assignment-revoke]:not([data-bound])").forEach(button => {
            button.dataset.bound = "true";
            button.addEventListener("click", async () => {
                if (!clientDetail) {
                    return;
                }

                const assignmentId = button.getAttribute("data-application-assignment-revoke");
                if (!assignmentId) {
                    return;
                }

                await fetchJson(`${authApiBasePath}/applications/${encodeURIComponent(clientDetail.id)}/assignments/${encodeURIComponent(assignmentId)}`, {
                    method: "DELETE"
                });
                setFlash("success", "Application assignment revoked.");
                await render();
            });
        });
    }

    function bindClientCredentialRevokeButtons(clientDetail) {
        document.querySelectorAll("[data-client-credential-revoke]:not([data-bound])").forEach(button => {
            button.dataset.bound = "true";
            button.addEventListener("click", async () => {
                if (!clientDetail || !window.confirm("Revoke this client credential immediately?")) {
                    return;
                }
                await fetchJson(`${authApiBasePath}/clients/${encodeURIComponent(clientDetail.id)}/credentials/${encodeURIComponent(button.dataset.clientCredentialRevoke)}`, {
                    method: "DELETE"
                });
                setFlash("success", "Client credential revoked.");
                await render();
            });
        });
    }

    async function renderAuthMachineClients() {
        const config = authViews["machine-clients"];
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading machine clients...");
        const pager = getPagerState("auth-machine-clients", 25);
        restartPagerWindow("auth-machine-org-picker");
        const orgPickerPager = getPagerState("auth-machine-org-picker", 25);
        const [machines, organizations] = await Promise.all([
            fetchJson(`${authApiBasePath}/machine-clients?${pagerQuery(pager)}`),
            fetchJson(`${authApiBasePath}/organizations?${pagerQuery(orgPickerPager)}`)
        ]);
        const machineRows = machines.data || [];
        const organizationRows = organizations.data || [];

        content.innerHTML = `
            ${consumeFlashHtml()}
            ${latestMachineClientSecret ? `<section class="panel callout"><h2>Copy this secret now</h2><p>SqlOS stores only a slow hash and cannot show this value again.</p><div class="inline-code">${esc(latestMachineClientSecret.secret)}</div><p><strong>Client ID:</strong> ${esc(latestMachineClientSecret.clientId)}</p><button id="machine-secret-ack" type="button">I copied it</button></section>` : ""}
            <div class="panel-grid">
                <section class="panel">
                    <h2>Create machine client</h2>
                    <p>Creates the confidential OAuth client, FGA subject, credential hash, and initial grant atomically.</p>
                    <form id="machine-client-form">
                        <input name="clientId" maxlength="200" placeholder="nightly-worker" required>
                        <input name="displayName" maxlength="200" placeholder="Nightly worker" required>
                        <input name="description" maxlength="500" placeholder="Purpose (optional)">
                        <input name="audience" maxlength="500" placeholder="https://api.example.com" required>
                        <input name="scopes" placeholder="jobs.run jobs.read" required>
                        ${renderRemotePicker({
                            searchId: "machine-org-picker-search",
                            selectName: "organizationId",
                            selectId: "machine-org-picker",
                            loadMoreId: "machine-org-picker-more",
                            searchPlaceholder: "Search organizations",
                            emptyLabel: "No organization binding",
                            items: organizationRows,
                            hasNextPage: !!organizations.hasNextPage,
                            itemValue: org => org.id,
                            itemLabel: org => org.name
                        })}
                        <input name="expiresAt" type="datetime-local" placeholder="Expiry (optional)">
                        <input name="resourceId" placeholder="Initial FGA resource ID (optional)">
                        <input name="roleId" placeholder="Initial FGA role ID (optional)">
                        <button type="submit">Create and reveal secret once</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Worker configuration</h2>
                    ${renderMetadataRows([
                        { label: "Token endpoint", value: `${window.location.origin}${authServerBasePath}/token` },
                        { label: "Authentication", value: "HTTP Basic (client_secret_basic)" },
                        { label: "Grant", value: "client_credentials" }
                    ])}
                    <p>Code-owned clients should use <code>SeedMachineClient</code> and a host secret resolver. Dashboard-owned clients reveal a generated secret only at creation and rotation.</p>
                </section>
            </div>
            <section class="panel">
                <div class="panel-actions">
                    <h2>Operational identities</h2>
                    <div id="machine-clients-pagination-top">${renderPagination(pager, machines)}</div>
                </div>
                ${machineRows.length ? `<div class="table-wrap"><table><thead><tr><th>Name</th><th>Client / audience</th><th>Status</th><th>Ownership</th><th>Grants</th><th>Last use</th><th>Actions</th></tr></thead><tbody>${machineRows.map(machine => `<tr>
                    <td>${esc(machine.displayName)}</td>
                    <td><code>${esc(machine.clientId)}</code><br><small>${esc(machine.audience)}</small></td>
                    <td>${machine.ready ? '<span class="status active">Ready</span>' : '<span class="status inactive">Unavailable</span>'}</td>
                    <td>${esc(machine.configurationOwner)}${machine.configurationSourceKey ? `<br><small>${esc(machine.configurationSourceKey)}</small>` : ""}${machine.configurationOrphanedAt ? '<br><span class="status warning">Orphaned</span>' : ""}</td>
                    <td>${esc(machine.grantCount)}</td><td>${machine.lastUsedAt ? esc(formatDate(machine.lastUsedAt)) : "Never"}</td>
                    <td><button type="button" data-machine-test="${esc(machine.clientId)}" data-resource="${esc(machine.audience)}" data-scopes="${esc(machine.scopes.join(" "))}">Test</button> ${machine.configurationOwner === "dashboard" ? `<button type="button" data-machine-rotate="${esc(machine.clientId)}">Rotate</button>` : ""} <button type="button" data-machine-revoke="${esc(machine.clientId)}">Revoke</button></td>
                </tr>`).join("")}</tbody></table></div>` : "<p>No machine clients yet.</p>"}
                <p><a href="${esc(pathForRoute("fga-service-accounts"))}" data-dashboard-route="fga-service-accounts">Inspect service-account subjects and grant paths in FGA</a></p>
            </section>`;

        bindRemotePicker({
            searchId: "machine-org-picker-search",
            selectId: "machine-org-picker",
            loadMoreId: "machine-org-picker-more",
            pagerKey: "auth-machine-org-picker",
            pageSize: 25,
            emptyLabel: "No organization binding",
            initialResult: organizations,
            itemValue: org => org.id,
            itemLabel: org => org.name,
            fetchPage: (pager, search) => {
                const params = new URLSearchParams(pagerQuery(pager));
                if (search) {
                    params.set("search", search);
                }
                return fetchJson(`${authApiBasePath}/organizations?${params.toString()}`);
            }
        });
        bindPagination("#machine-clients-pagination-top", "auth-machine-clients", machines, () => renderAuthMachineClients());

        bindForm("machine-client-form", async form => {
            const resourceId = String(form.get("resourceId") || "").trim();
            const roleId = String(form.get("roleId") || "").trim();
            if ((resourceId && !roleId) || (!resourceId && roleId)) throw new Error("Specify both initial resource and role IDs.");
            const result = await fetchJson(`${authApiBasePath}/machine-clients`, { method: "POST", body: JSON.stringify({
                clientId: form.get("clientId"), displayName: form.get("displayName"), description: form.get("description") || null,
                audience: form.get("audience"), scopes: String(form.get("scopes") || "").split(/\s+/).filter(Boolean),
                organizationId: form.get("organizationId") || null, expiresAt: form.get("expiresAt") ? new Date(form.get("expiresAt")).toISOString() : null,
                grants: resourceId ? [{ resourceId, roleId, description: "Initial dashboard grant" }] : []
            }) });
            latestMachineClientSecret = { clientId: result.client.clientId, secret: result.clientSecret };
            restartPagerWindow("auth-machine-clients");
            await renderAuthMachineClients();
        });
        document.getElementById("machine-secret-ack")?.addEventListener("click", async () => { latestMachineClientSecret = null; await renderAuthMachineClients(); });
        document.querySelectorAll("[data-machine-rotate]").forEach(button => button.addEventListener("click", async () => {
            if (!window.confirm(`Rotate ${button.dataset.machineRotate}? The old secret stops working immediately.`)) return;
            const result = await fetchJson(`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineRotate)}/rotate`, { method: "POST" });
            latestMachineClientSecret = { clientId: result.client.clientId, secret: result.clientSecret }; await renderAuthMachineClients();
        }));
        document.querySelectorAll("[data-machine-test]").forEach(button => button.addEventListener("click", async () => {
            const secret = window.prompt(`Paste the current secret for ${button.dataset.machineTest}. It is sent only to this authenticated admin operation and is never stored or returned.`);
            if (!secret) return;
            const result = await fetchJson(`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineTest)}/validate`, { method: "POST", body: JSON.stringify({ clientSecret: secret, resource: button.dataset.resource, scopes: button.dataset.scopes.split(/\s+/).filter(Boolean) }) });
            setFlash(result.valid ? "success" : "error", result.valid ? "Credential, audience, scope, expiry, and client status are valid." : "Credential or binding is invalid."); await renderAuthMachineClients();
        }));
        document.querySelectorAll("[data-machine-revoke]").forEach(button => button.addEventListener("click", async () => {
            if (!window.confirm(`Immediately revoke ${button.dataset.machineRevoke}? Existing database-validated service tokens will stop working.`)) return;
            await fetchJson(`${authApiBasePath}/machine-clients/${encodeURIComponent(button.dataset.machineRevoke)}/revoke`, { method: "POST" }); setFlash("success", "Machine client revoked."); await renderAuthMachineClients();
        }));
    }

    async function renderAuthOidc() {
        const config = authViews.oidc;
        const callbackTemplate = `${window.location.origin}${dashboardBasePath}/auth/oidc/callback`;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading OIDC connections...");

        const pager = getPagerState("auth-oidc", 25);
        const oidcResult = await fetchJson(`${authApiBasePath}/oidc-connections?${pagerQuery(pager)}`);
        const oidcConnections = oidcResult.data || [];

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-stack">
                <div class="panel-grid">
                    <section class="panel">
                        <h2>Configure Social Provider</h2>
                        <p>SqlOS owns the provider callback for social login. Register this exact callback URI with Google, Microsoft, Apple, GitHub, or your custom provider, then save the provider configuration here.</p>
                        <form id="create-oidc-connection-form">
                            <select id="oidc-provider-type" name="providerType" required>
                                <option value="Google">Google</option>
                                <option value="Microsoft">Microsoft</option>
                                <option value="Apple">Apple</option>
                                <option value="GitHub">GitHub</option>
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
                            <button type="submit">Create social connection</button>
                        </form>
                    </section>
                    <section class="panel">
                        <h2>Social Login Guide</h2>
                        <p>Pick a provider type in the form; this section updates to show the most relevant integration checklist.</p>
                        <div id="oidc-provider-guide"></div>
                    </section>
                </div>
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Configured Providers</h2>
                        <div id="oidc-pagination-top">${renderPagination(pager, oidcResult)}</div>
                    </div>
                    ${renderList(
                        oidcConnections,
                        item => `
                            <div class="list-item-header">
                                <div class="oidc-provider-summary">
                                    ${renderOidcProviderLogo(item.effectiveLogoDataUrl || item.logoDataUrl, item.displayName)}
                                    <div>
                                        <strong>${esc(item.displayName)}</strong>
                                        <div class="oidc-provider-subtitle">${esc(item.providerType)} social login${item.protocol ? ` · ${esc(item.protocol)}` : ""}</div>
                                    </div>
                                </div>
                            </div>
                            ${renderMetadataRows([
                                { label: "Provider", value: item.providerType },
                                { label: "Protocol", value: item.protocol || "Oidc" },
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
                                ,{ label: "Configuration owner", value: item.ownership?.owner || "dashboard" }
                                ,{ label: "Source key", value: item.ownership?.sourceKey }
                                ,{ label: "Reconciliation", value: item.ownership?.isOrphaned ? "Seed missing" : item.ownership?.lastReconciledAt ? `Reconciled ${formatDate(item.ownership.lastReconciledAt)}` : "Not reconciled" }
                            ])}
                            ${item.ownership && !item.ownership.isEditable ? `<div class="callout"><strong>Code owned:</strong> Edit this connection in startup configuration. Emergency enable/disable remains available here.</div>` : ""}
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
            if (item.ownership && !item.ownership.isEditable) {
                const form = document.getElementById(`edit-oidc-${item.id}`);
                form?.querySelectorAll("input, textarea, select, button[type='submit']").forEach(field => { field.disabled = true; });
            }
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

        bindPagination("#oidc-pagination-top", "auth-oidc", oidcResult, () => render());
    }

    async function renderAuthSso() {
        const config = authViews.sso;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading SSO data...");

        const pager = getPagerState("auth-sso", 25);
        const ssoResult = await fetchJson(`${authApiBasePath}/sso-connections?${pagerQuery(pager)}`);
        const ssoConnections = ssoResult.data || [];

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
                    <div class="panel-actions">
                        <h2>SAML Connections</h2>
                        <div id="sso-pagination-top">${renderPagination(pager, ssoResult)}</div>
                    </div>
                    ${renderList(
                        ssoConnections,
                        item => `
                            <strong>${esc(item.displayName)}</strong>
                            ${renderMetadataRows([
                                { label: "Connection ID", value: item.id },
                                { label: "Organization", value: item.organization },
                                { label: "Primary domain", value: item.primaryDomain || "n/a" },
                                { label: "Status", value: `${item.setupStatus} | Enabled: ${item.isEnabled}` },
                                { label: "Configuration owner", value: item.ownership?.owner || "dashboard" },
                                { label: "Source key", value: item.ownership?.sourceKey || "n/a" },
                                { label: "SP Entity ID", value: item.serviceProviderEntityId },
                                { label: "ACS URL", value: item.assertionConsumerServiceUrl }
                            ])}
                            ${item.ownership && !item.ownership.isEditable ? `<div class="callout"><strong>Code owned:</strong> Change connection fields in startup configuration. Emergency enable/disable remains available.</div>` : ""}
                            <button type="button" data-saml-toggle="${esc(item.id)}" data-enabled="${item.isEnabled ? "true" : "false"}">${item.isEnabled ? "Emergency disable" : "Enable"}</button>
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

        document.querySelectorAll("[data-saml-toggle]").forEach(button => {
            button.addEventListener("click", async () => {
                const enabled = button.dataset.enabled === "true";
                if (enabled && !window.confirm("Disable this SAML connection? New SSO sign-ins will stop until it is enabled again.")) return;
                await fetchJson(`${authApiBasePath}/sso-connections/${encodeURIComponent(button.dataset.samlToggle)}/${enabled ? "disable" : "enable"}`, { method: "POST" });
                setFlash("success", enabled ? "SAML connection disabled." : "SAML connection enabled.");
                await render();
            });
        });

        bindPagination("#sso-pagination-top", "auth-sso", ssoResult, () => render());
    }

    async function renderAuthSecurity() {
        const config = authViews.security;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading security settings...");

        const [settings, otp] = await Promise.all([
            fetchJson(`${authApiBasePath}/settings/security`),
            fetchJson(`${authApiBasePath}/otp/readiness`)
        ]);
        const otpCard = method => `
            <section class="panel">
                <h2>${method.method === "email" ? "Email OTP" : "Phone OTP"}</h2>
                <div class="status-row"><span class="status ${method.enabled && method.locallyConfigured ? "active" : "inactive"}">${method.enabled && method.locallyConfigured ? "Ready" : method.enabled ? "Incomplete" : "Disabled"}</span></div>
                ${renderMetadataRows([
                    { label: "Provider", value: method.provider },
                    { label: "Deployment configuration", value: "Code / host configuration (view only)" },
                    { label: "Reason codes", value: method.reasonCodes.length ? method.reasonCodes.join(", ") : "None" },
                    { label: "Configuration keys", value: method.configurationKeys.join("\n") }
                ])}
                <details><summary>Effective non-secret policy</summary><pre>${esc(JSON.stringify(method.policy, null, 2))}</pre></details>
                <form class="otp-test-form" data-method="${method.method}">
                    <input name="destination" type="${method.method === "email" ? "email" : "tel"}" maxlength="320" placeholder="${method.method === "email" ? "operator@example.com" : "+14155550123"}" required>
                    <button type="submit" ${method.locallyConfigured ? "" : "disabled"}>Send test delivery</button>
                </form>
                <small>Tests are limited to three sends per destination and 20 per operator source per hour. They never create a user, session, or SqlOS login challenge.</small>
            </section>`;

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
                        <input name="refreshTokenGraceWindowSeconds" type="number" min="0" placeholder="Refresh token grace window (seconds, 0 to disable)" value="${esc(settings.refreshTokenGraceWindowSeconds)}" required>
                        <small style="display:block;margin-top:-8px;margin-bottom:12px;color:#64748b;">Window after a refresh token is rotated during which the previous token is still accepted. Prevents legitimate concurrent refreshes (multi-tab, parallel SSR, mobile retries) from being false-flagged as token theft. Default 30s. Set 0 to disable.</small>
                        <button type="submit">Save settings</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Current Values</h2>
                    ${renderMetadataRows([
                        { label: "Refresh token lifetime", value: `${settings.refreshTokenLifetimeMinutes} minutes` },
                        { label: "Idle timeout", value: `${settings.sessionIdleTimeoutMinutes} minutes` },
                        { label: "Absolute lifetime", value: `${settings.sessionAbsoluteLifetimeMinutes} minutes` },
                        { label: "Refresh grace window", value: settings.refreshTokenGraceWindowSeconds === 0 ? "Disabled" : `${settings.refreshTokenGraceWindowSeconds} seconds` }
                    ])}
                </section>
            </div>
            <h2>OTP communications readiness</h2>
            <p>Provider secrets remain deployment-owned and are never returned here. These checks validate local configuration only; startup never calls a provider.</p>
            <div class="panel-grid">
                ${otpCard(otp.email)}
                ${otpCard(otp.phone)}
            </div>
            <section class="panel">
                <h2>Recent OTP test diagnostics</h2>
                ${otp.recentDiagnostics.length ? `<div class="table-wrap"><table><thead><tr><th>Time</th><th>Method</th><th>Outcome</th><th>Destination</th><th>Provider status</th></tr></thead><tbody>${otp.recentDiagnostics.map(item => `<tr><td>${esc(formatDate(item.occurredAt))}</td><td>${esc(item.method || "-")}</td><td>${esc(item.action)}</td><td>${esc(item.maskedDestination || "-")}</td><td>${esc(item.providerStatus || "-")}</td></tr>`).join("")}</tbody></table></div>` : "<p>No administrative OTP test deliveries have been attempted.</p>"}
            </section>
        `;

        bindForm("security-settings-form", async form => {
            // Include the signing-key fields from the loaded settings even
            // though the form doesn't expose them. The PUT endpoint requires
            // all fields and would otherwise reject the request as missing
            // positive integers for the signing-key rotation values.
            await fetchJson(`${authApiBasePath}/settings/security`, {
                method: "PUT",
                body: JSON.stringify({
                    refreshTokenLifetimeMinutes: Number(form.get("refreshTokenLifetimeMinutes")),
                    sessionIdleTimeoutMinutes: Number(form.get("sessionIdleTimeoutMinutes")),
                    sessionAbsoluteLifetimeMinutes: Number(form.get("sessionAbsoluteLifetimeMinutes")),
                    signingKeyRotationIntervalDays: settings.signingKeyRotationIntervalDays,
                    signingKeyGraceWindowDays: settings.signingKeyGraceWindowDays,
                    signingKeyRetiredCleanupDays: settings.signingKeyRetiredCleanupDays,
                    refreshTokenGraceWindowSeconds: Number(form.get("refreshTokenGraceWindowSeconds"))
                })
            });
            setFlash("success", "Security settings saved.");
        });

        document.querySelectorAll(".otp-test-form").forEach(form => {
            form.addEventListener("submit", async event => {
                event.preventDefault();
                const method = form.dataset.method;
                const destination = new FormData(form).get("destination");
                if (!window.confirm(`Send a ${method} OTP delivery test to ${destination}? This may incur provider charges.`)) return;
                await fetchJson(`${authApiBasePath}/otp/test-delivery`, {
                    method: "POST",
                    body: JSON.stringify({ method, destination })
                });
                setFlash("success", `${method === "email" ? "Email" : "Phone"} test accepted. No login challenge was created.`);
                await renderAuthSecurity();
            });
        });
    }

    async function renderAuthMfa() {
        const config = authViews.mfa;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading MFA settings...");

        const settings = await fetchJson(`${authApiBasePath}/settings/mfa`);
        const factors = Array.isArray(settings.availableFactors) ? settings.availableFactors.join(", ") : "totp, recovery_code";
        const roles = Array.isArray(settings.requiredRoles) ? settings.requiredRoles.join(", ") : "owner, admin";

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid">
                <section class="panel">
                    <h2>MFA Settings</h2>
                    ${settings.ownership && !settings.ownership.isEditable ? `<div class="callout"><strong>Code owned:</strong> Policy fields come from startup configuration. The master Enable MFA switch remains available for emergency shutdown.</div>` : ""}
                    <form id="mfa-settings-form">
                        <label><input type="checkbox" name="enabled" ${settings.enabled ? "checked" : ""}> Enable MFA</label>
                        <label><input type="checkbox" name="totpEnabled" ${settings.totpEnabled ? "checked" : ""}> Enable authenticator apps</label>
                        <label><input type="checkbox" name="userSelfEnrollmentEnabled" ${settings.userSelfEnrollmentEnabled ? "checked" : ""}> Allow users to add MFA voluntarily</label>
                        <label><input type="checkbox" name="recoveryCodesEnabled" ${settings.recoveryCodesEnabled ? "checked" : ""}> Issue recovery codes</label>
                        <label><input type="checkbox" name="requireForAllUsers" ${settings.requireForAllUsers ? "checked" : ""}> Require MFA for all users</label>
                        <label><input type="checkbox" name="requireForOwnersAndAdmins" ${settings.requireForOwnersAndAdmins ? "checked" : ""}> Require MFA for owners and admins</label>
                        <input name="requiredRoles" placeholder="Required roles, comma separated" value="${esc(roles)}">
                        <input name="availableFactors" placeholder="Available factors, comma separated" value="${esc(factors)}">
                        <button type="submit">Save MFA settings</button>
                    </form>
                </section>
                <section class="panel">
                    <h2>Current Policy</h2>
                    ${renderMetadataRows([
                        { label: "MFA", value: settings.enabled ? "Enabled" : "Disabled" },
                        { label: "Authenticator apps", value: settings.totpEnabled ? "Enabled" : "Disabled" },
                        { label: "User self-enrollment", value: settings.userSelfEnrollmentEnabled ? "Enabled" : "Disabled" },
                        { label: "Recovery codes", value: settings.recoveryCodesEnabled ? "Enabled" : "Disabled" },
                        { label: "All users required", value: settings.requireForAllUsers ? "Yes" : "No" },
                        { label: "Privileged roles required", value: settings.requireForOwnersAndAdmins ? "Yes" : "No" },
                        { label: "Updated", value: formatDate(settings.updatedAt) }
                    ])}
                </section>
            </div>
        `;

        if (settings.ownership && !settings.ownership.isEditable) {
            content.querySelectorAll("#mfa-settings-form input:not([name='enabled'])").forEach(field => { field.disabled = true; });
        }

        bindForm("mfa-settings-form", async form => {
            const splitList = value => String(value || "")
                .split(",")
                .map(item => item.trim())
                .filter(Boolean);

            await fetchJson(`${authApiBasePath}/settings/mfa`, {
                method: "PUT",
                body: JSON.stringify({
                    enabled: form.get("enabled") === "on",
                    totpEnabled: form.get("totpEnabled") === "on",
                    userSelfEnrollmentEnabled: form.get("userSelfEnrollmentEnabled") === "on",
                    recoveryCodesEnabled: form.get("recoveryCodesEnabled") === "on",
                    requireForAllUsers: form.get("requireForAllUsers") === "on",
                    requireForOwnersAndAdmins: form.get("requireForOwnersAndAdmins") === "on",
                    requiredRoles: splitList(form.get("requiredRoles")),
                    availableFactors: splitList(form.get("availableFactors"))
                })
            });
            setFlash("success", "MFA settings saved.");
        });
    }

    async function renderAuthPage() {
        const config = authViews.authpage;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading Auth Page settings...");

        const [settings, emailSettings, metadata] = await Promise.all([
            fetchJson(`${authApiBasePath}/settings/auth-page`),
            fetchJson(`${authApiBasePath}/settings/email`),
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
                            <p class="muted" style="margin-top:-4px;font-size:12px;line-height:1.5;">Accepted colors: <code>#RGB</code>, <code>#RRGGBB</code>, <code>#RRGGBBAA</code>, <code>rgb()</code>/<code>rgba()</code>, <code>hsl()</code>/<code>hsla()</code>, or <code>transparent</code>. HTML, URLs, and CSS rules are rejected.</p>
                            ${settings.headlessCapabilityRegistered
                                ? `<div class="callout"><strong>Headless auth is enabled.</strong> <code>/authorize</code> redirects into your app because <code>UseHeadlessAuthPage()</code> registered a UI callback.</div>`
                                : `<div class="callout"><strong>Hosted auth is enabled.</strong> SqlOS serves the login and signup pages because no headless UI callback is registered.</div>`}
                            ${settings.emailOtpRuntimeConfigured
                                ? `<div class="callout"><strong>Email OTP uses transactional templates.</strong> Add <code>email_otp</code> to enabled credential types to let users sign in with a one-time code.</div>`
                                : `<div class="callout"><strong>Custom Email OTP delivery is not configured.</strong> Configure the custom auth email sender before enabling <code>email_otp</code>.</div>`}
                            ${settings.magicLinkRuntimeConfigured
                                ? `<div class="callout"><strong>Magic links use transactional templates.</strong> Add <code>magic_link</code> to enabled credential types to let users sign in from a one-time email link.</div>`
                                : `<div class="callout"><strong>Custom magic-link delivery is not configured.</strong> Configure the custom auth email sender before enabling <code>magic_link</code>.</div>`}
                            <label><input type="checkbox" name="enablePasswordSignup" ${settings.enablePasswordSignup ? "checked" : ""}> Allow password signup</label>
                            <input name="enabledCredentialTypes" placeholder="Enabled credential types (password email_otp magic_link)" value="${esc(enabledCredentialTypes || "password")}" required>
                            <p class="muted" style="margin-top:-4px;font-size:12px;line-height:1.5;">Space or comma separate values. Supported first-party types today: <code>password</code>, <code>email_otp</code>, <code>magic_link</code>, <code>phone_otp</code>.</p>
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
                            <strong>Admin guidance:</strong> Use this page to set the title, logo, colors, layout, and first-party sign-in methods. OIDC and SAML providers still appear below these local credential choices when configured.
                        </div>
                    </section>
                    <section class="panel">
                        <h2>Email Branding</h2>
                        <p>These settings style built-in AuthServer emails. Use Communications templates for copy and layout, or SDK message builders for advanced custom behavior.</p>
                        ${emailSettings.managedByStartupSeed ? `<div class="callout"><strong>Startup managed:</strong> These email values are seeded from application startup and will be reapplied on restart.</div>` : ""}
                        <form id="auth-email-settings-form">
                            <input name="applicationName" placeholder="Application name" value="${esc(emailSettings.applicationName || "")}" required>
                            <div class="panel-grid">
                                <input name="primaryColor" placeholder="Primary color (#2563eb)" value="${esc(emailSettings.primaryColor || "")}" required>
                                <input name="accentColor" placeholder="Accent color (#0f172a)" value="${esc(emailSettings.accentColor || "")}" required>
                            </div>
                            <input name="backgroundColor" placeholder="Background color (#f8fafc)" value="${esc(emailSettings.backgroundColor || "")}" required>
                            <p class="muted" style="margin-top:-4px;font-size:12px;line-height:1.5;">Accepted colors: <code>#RGB</code>, <code>#RRGGBB</code>, <code>#RRGGBBAA</code>, <code>rgb()</code>/<code>rgba()</code>, <code>hsl()</code>/<code>hsla()</code>, or <code>transparent</code>. HTML, URLs, and CSS rules are rejected.</p>
                            <label>Email logo upload<input id="auth-email-logo-file" type="file" accept="image/*"></label>
                            <textarea name="logoBase64" placeholder="Optional base64 image payload or data URL. Leave blank to reuse the Auth Page logo.">${esc(emailSettings.logoBase64 || "")}</textarea>
                            <button type="submit">Save Email Branding</button>
                        </form>
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

        bindForm("auth-email-settings-form", async form => {
            await fetchJson(`${authApiBasePath}/settings/email`, {
                method: "PUT",
                body: JSON.stringify({
                    applicationName: form.get("applicationName"),
                    logoBase64: form.get("logoBase64") || null,
                    primaryColor: form.get("primaryColor"),
                    accentColor: form.get("accentColor"),
                    backgroundColor: form.get("backgroundColor")
                })
            });
            setFlash("success", "Email branding saved.");
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

        const emailFileInput = document.getElementById("auth-email-logo-file");
        const emailForm = document.getElementById("auth-email-settings-form");
        emailFileInput?.addEventListener("change", () => {
            const file = emailFileInput.files?.[0];
            if (!file || !emailForm) {
                return;
            }

            const reader = new FileReader();
            reader.onload = () => {
                emailForm.elements.logoBase64.value = String(reader.result || "");
            };
            reader.readAsDataURL(file);
        });
    }

    async function revokeSessionsWithPreview(request) {
        const preview = await fetchJson(`${authApiBasePath}/sessions/revocation/preview`, {
            method: "POST",
            body: JSON.stringify(request)
        });
        if (preview.matchedSessions === 0) {
            setFlash("info", "No sessions matched this scope.");
            return false;
        }
        const confirmed = window.confirm(
            `Revoke ${preview.matchedSessions} session(s) and ${preview.activeRefreshTokens} active refresh token(s)? Already-revoked sessions will be left unchanged.`
        );
        if (!confirmed) return false;
        await fetchJson(`${authApiBasePath}/sessions/revocation`, {
            method: "POST",
            body: JSON.stringify({
                ...request,
                operationId: preview.operationId,
                expectedMatchedSessions: preview.matchedSessions,
                confirm: true
            })
        });
        return true;
    }

    async function renderAuthSessions() {
        const config = authViews.sessions;
        setHeader("Auth Server", config.title, config.description);
        renderLoading("Loading sessions...");

        const pager = getPagerState("auth-sessions");
        const sessions = await fetchJson(`${authApiBasePath}/sessions?${pagerQuery(pager)}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="panel list-page">
                ${renderListToolbar({
                    title: "Sessions",
                    pagerHtml: `<div id="sessions-pagination-top">${renderPagination(pager, sessions)}</div>`
                })}
                <form id="session-revocation-form" class="client-filter-form">
                    <input name="userId" placeholder="User ID (optional)">
                    <input name="organizationId" placeholder="Organization ID (optional)">
                    <input name="clientApplicationId" placeholder="Client application ID (optional)">
                    <input name="reason" placeholder="Reason" value="admin_revoked" required>
                    <button type="submit">Preview and revoke</button>
                </form>
                <p>Use one or more filters. Combined filters use AND semantics, and you will see the affected session and refresh-token count before confirmation.</p>
                ${renderListRows(
                    sessions.data,
                    item => renderListRow({
                        href: item.revokedAt && item.userId ? userDetailPath(item.userId, "sessions") : "",
                        title: item.user || "Unknown user",
                        subtitle: [item.authenticationMethod, item.clientApplicationId, item.createdAt ? formatDate(item.createdAt) : ""].filter(Boolean).join(" · "),
                        metaHtml: [
                            item.revokedAt ? renderChip("Revoked") : renderChip("Active", "green"),
                            renderIdChip(item.id)
                        ].join(""),
                        actionsHtml: item.revokedAt
                            ? ""
                            : `<button type="button" class="js-revoke-session" data-session-id="${esc(item.id)}">Revoke</button>`
                    }),
                    "No sessions yet."
                )}
            </section>
        `;

        bindPagination("#sessions-pagination-top", "auth-sessions", sessions, () => render());

        bindForm("session-revocation-form", async form => {
            const request = {
                userId: form.get("userId") || null,
                organizationId: form.get("organizationId") || null,
                clientApplicationId: form.get("clientApplicationId") || null,
                reason: form.get("reason")
            };
            if (await revokeSessionsWithPreview(request)) setFlash("success", "Sessions revoked.");
        });

        document.querySelectorAll(".js-revoke-session").forEach(button => {
            button.addEventListener("click", async () => {
                try {
                    const reason = window.prompt("Why are you revoking this session?", "admin_revoked");
                    if (reason === null) return;
                    if (await revokeSessionsWithPreview({ sessionId: button.dataset.sessionId, reason })) {
                        setFlash("success", "Session revoked.");
                    }
                } catch (error) {
                    setFlash("error", error.message || String(error));
                }
                await render();
            });
        });
    }

    async function renderAuthAudit() {
        await renderAuditLogs({
            fixedSource: "authserver",
            eyebrow: "Audit Logs",
            title: "Auth Server Audit",
            description: "Review auth-server events through the central audit log product."
        });
    }

    function auditDateParam(value, endOfRange = false) {
        if (!value) {
            return "";
        }

        const normalized = value.length === 10
            ? `${value}T${endOfRange ? "23:59:59" : "00:00:00"}`
            : value;
        const parsed = new Date(normalized);
        return Number.isNaN(parsed.getTime()) ? value : parsed.toISOString();
    }

    function buildAuditQueryParams({ includePager = true, fixedSource = null } = {}) {
        const params = new URLSearchParams();
        if (includePager) {
            const pager = getPagerState("audit-logs", 25);
            new URLSearchParams(pagerQuery(pager)).forEach((value, key) => params.set(key, value));
        }

        const values = {
            organizationId: auditFilters.organizationId,
            application: auditFilters.application,
            source: fixedSource || auditFilters.source,
            action: auditFilters.action,
            actorType: auditFilters.actorType,
            actorId: auditFilters.actorId,
            targetType: auditFilters.targetType,
            targetId: auditFilters.targetId,
            result: auditFilters.result,
            search: auditFilters.search,
            occurredAtFrom: auditDateParam(auditFilters.from),
            occurredAtTo: auditDateParam(auditFilters.to, true)
        };

        Object.entries(values).forEach(([key, value]) => {
            if (value) {
                params.set(key, value);
            }
        });

        return params;
    }

    function auditActorLabel(event) {
        const actor = event.actor || {};
        const identity = actor.displayName || actor.id || "n/a";
        return `${actor.type || "system"}: ${identity}`;
    }

    function auditTargetSummary(targets) {
        if (!targets || targets.length === 0) {
            return "n/a";
        }

        return targets
            .map(target => `${target.type}:${target.displayName || target.id}`)
            .join(", ");
    }

    function auditApplicationLabel(event) {
        return event.applicationKey || event.applicationId || "n/a";
    }

    function renderAuditFilterForm(fixedSource) {
        return `
            <form id="audit-filter-form" class="audit-filter-form">
                <input name="search" placeholder="Search action, actor, target, metadata" value="${esc(auditFilters.search)}">
                <input name="organizationId" placeholder="Organization ID" value="${esc(auditFilters.organizationId)}">
                <input name="application" placeholder="Application key or client ID" value="${esc(auditFilters.application)}">
                ${fixedSource ? `
                    <input name="source" value="${esc(fixedSource)}" disabled>
                ` : `
                    <input name="source" placeholder="Source" value="${esc(auditFilters.source)}">
                `}
                <input name="action" placeholder="Action" value="${esc(auditFilters.action)}">
                <input name="actorType" placeholder="Actor type" value="${esc(auditFilters.actorType)}">
                <input name="actorId" placeholder="Actor ID" value="${esc(auditFilters.actorId)}">
                <input name="targetType" placeholder="Target type" value="${esc(auditFilters.targetType)}">
                <input name="targetId" placeholder="Target ID" value="${esc(auditFilters.targetId)}">
                <input name="result" placeholder="Metadata result/status" value="${esc(auditFilters.result)}">
                <input name="from" type="datetime-local" value="${esc(auditFilters.from)}">
                <input name="to" type="datetime-local" value="${esc(auditFilters.to)}">
                <div class="form-actions audit-filter-actions">
                    <button type="submit">Apply filters</button>
                    <button id="audit-clear-filters" type="button">Clear</button>
                </div>
            </form>
        `;
    }

    function renderAuditDetail(event) {
        if (!event) {
            return `
                <section class="panel">
                    <h2>Event Detail</h2>
                    <div class="empty-state-block">Select an audit event to inspect its actor, targets, context, and metadata.</div>
                </section>
            `;
        }

        return `
            <section class="panel audit-detail-panel">
                <div class="panel-actions">
                    <h2>Event Detail</h2>
                    <span class="client-badge client-badge--source">${esc(event.source)}</span>
                </div>
                <h3>${esc(event.action)}</h3>
                ${renderMetadataRows([
                    { label: "Event ID", value: event.id },
                    { label: "Occurred", value: formatDate(event.occurredAt) },
                    { label: "Ingested", value: formatDate(event.ingestedAt) },
                    { label: "Organization", value: event.organizationId || "n/a" },
                    { label: "Application", value: auditApplicationLabel(event) },
                    { label: "Actor", value: auditActorLabel(event) },
                    { label: "Targets", value: auditTargetSummary(event.targets) },
                    { label: "IP", value: event.ipAddress || event.context?.ipAddress || "n/a" },
                    { label: "Request", value: event.requestId || event.context?.requestId || "n/a" },
                    { label: "Correlation", value: event.correlationId || event.context?.correlationId || "n/a" }
                ])}
                <details open>
                    <summary>Actor</summary>
                    <pre class="json-preview">${esc(formatJson(event.actor))}</pre>
                </details>
                <details open>
                    <summary>Targets</summary>
                    <pre class="json-preview">${esc(formatJson(event.targets || []))}</pre>
                </details>
                <details open>
                    <summary>Context</summary>
                    <pre class="json-preview">${esc(formatJson(event.context || {}))}</pre>
                </details>
                <details open>
                    <summary>Metadata</summary>
                    <pre class="json-preview">${esc(formatJson(event.metadata || {}))}</pre>
                </details>
            </section>
        `;
    }

    async function renderAuditLogs(options = {}) {
        const fixedSource = options.fixedSource || null;
        setHeader(
            options.eyebrow || "Governance",
            options.title || "Audit Logs",
            options.description || "Search, filter, inspect, and export structured SqlOS and application audit events.");
        renderLoading("Loading audit logs...");

        const auditFilterKey = [
            fixedSource || auditFilters.source,
            auditFilters.organizationId,
            auditFilters.application,
            auditFilters.action,
            auditFilters.actorType,
            auditFilters.actorId,
            auditFilters.targetType,
            auditFilters.targetId,
            auditFilters.result,
            auditFilters.search,
            auditFilters.from,
            auditFilters.to
        ].join("|");
        const pager = resetPager("audit-logs", 25, auditFilterKey);
        const params = buildAuditQueryParams({ fixedSource });
        const auditResult = await fetchJson(`${auditApiBasePath}/events?${params.toString()}`);
        let selectedEvent = null;
        if (selectedAuditEventId) {
            try {
                selectedEvent = await fetchJson(`${auditApiBasePath}/events/${encodeURIComponent(selectedAuditEventId)}`);
            } catch {
                selectedAuditEventId = null;
            }
        }

        const exportParams = buildAuditQueryParams({ includePager: false, fixedSource });
        const exportHref = `${auditApiBasePath}/events/export.csv?${exportParams.toString()}`;

        content.innerHTML = `
            ${consumeFlashHtml()}
            <div class="panel-grid audit-grid">
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Filters</h2>
                        <a class="button-link" href="${esc(exportHref)}">Export CSV</a>
                    </div>
                    ${renderAuditFilterForm(fixedSource)}
                </section>
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Events</h2>
                        <div id="audit-pagination-top">${renderPagination(pager, auditResult)}</div>
                    </div>
                    ${renderList(
                        auditResult.data,
                        item => `
                            <div class="audit-event-row ${item.id === selectedAuditEventId ? "audit-event-row--selected" : ""}">
                                <div class="list-item-header">
                                    <strong>${esc(item.action)}</strong>
                                    <button type="button" data-audit-event-id="${esc(item.id)}">Inspect</button>
                                </div>
                                <div class="client-badge-row">
                                    <span class="client-badge client-badge--source">${esc(item.source)}</span>
                                    ${item.applicationKey || item.applicationId ? `<span class="client-badge client-badge--info">${esc(auditApplicationLabel(item))}</span>` : ""}
                                    ${item.metadata?.result ? `<span class="client-badge client-badge--success">${esc(item.metadata.result)}</span>` : ""}
                                </div>
                                ${renderMetadataRows([
                                    { label: "Occurred", value: formatDate(item.occurredAt) },
                                    { label: "Actor", value: auditActorLabel(item) },
                                    { label: "Organization", value: item.organizationId || "n/a" },
                                    { label: "Targets", value: auditTargetSummary(item.targets) },
                                    { label: "IP", value: item.ipAddress || item.context?.ipAddress || "n/a" }
                                ])}
                            </div>
                        `,
                        "No audit events matched the current filters."
                    )}
                </section>
                ${renderAuditDetail(selectedEvent)}
            </div>
        `;

        bindForm("audit-filter-form", async form => {
            auditFilters.search = String(form.get("search") || "").trim();
            auditFilters.organizationId = String(form.get("organizationId") || "").trim();
            auditFilters.application = String(form.get("application") || "").trim();
            auditFilters.source = fixedSource ? "" : String(form.get("source") || "").trim();
            auditFilters.action = String(form.get("action") || "").trim();
            auditFilters.actorType = String(form.get("actorType") || "").trim();
            auditFilters.actorId = String(form.get("actorId") || "").trim();
            auditFilters.targetType = String(form.get("targetType") || "").trim();
            auditFilters.targetId = String(form.get("targetId") || "").trim();
            auditFilters.result = String(form.get("result") || "").trim();
            auditFilters.from = String(form.get("from") || "").trim();
            auditFilters.to = String(form.get("to") || "").trim();
            selectedAuditEventId = null;
        });

        document.getElementById("audit-clear-filters")?.addEventListener("click", async () => {
            Object.keys(auditFilters).forEach(key => auditFilters[key] = "");
            selectedAuditEventId = null;
            await render();
        });

        document.querySelectorAll("[data-audit-event-id]").forEach(button => {
            button.addEventListener("click", async () => {
                selectedAuditEventId = button.dataset.auditEventId || null;
                await render();
            });
        });

        bindPagination("#audit-pagination-top", "audit-logs", auditResult, () => {
            selectedAuditEventId = null;
            return render();
        });
    }

    async function renderCalendarConnections() {
        const config = calendarViews.connections;
        setHeader("Integrations", config.title, config.description);
        renderLoading("Loading calendar connections...");

        const calendarFilterKey = `${calendarConnectionFilters.search}|${calendarConnectionFilters.includeRevoked ? "revoked" : "active"}`;
        const pager = resetPager("calendar-connections", 25, calendarFilterKey);
        const params = new URLSearchParams(pagerQuery(pager));
        params.set("includeRevoked", calendarConnectionFilters.includeRevoked ? "true" : "false");
        if (calendarConnectionFilters.search) {
            params.set("search", calendarConnectionFilters.search);
        }

        const [summary, result] = await Promise.all([
            fetchJson(`${calendarApiBasePath}/summary`),
            fetchJson(`${calendarApiBasePath}/connections?${params.toString()}`)
        ]);

        let selectedConnection = null;
        if (selectedCalendarConnectionId) {
            try {
                selectedConnection = await fetchJson(`${calendarApiBasePath}/connections/${encodeURIComponent(selectedCalendarConnectionId)}`);
            } catch {
                selectedCalendarConnectionId = null;
            }
        }

        content.innerHTML = `
            ${consumeFlashHtml()}
            ${renderStatsGroup("Calendar Integration", summary, [
                { key: "connections", label: "Connections" },
                { key: "active", label: "Active" },
                { key: "errored", label: "Errored" },
                { key: "events", label: "Synced Events" }
            ])}
            <div class="panel-grid">
                <section class="panel">
                    <div class="panel-actions">
                        <h2>Connections</h2>
                        <div id="calendar-pagination-top">${renderPagination(pager, result)}</div>
                    </div>
                    <form id="calendar-filter-form" class="inline-form">
                        <input name="search" value="${esc(calendarConnectionFilters.search)}" placeholder="Search by name, account, user, or organization">
                        <label class="checkbox-label">
                            <input type="checkbox" name="includeRevoked" ${calendarConnectionFilters.includeRevoked ? "checked" : ""}>
                            Include disconnected
                        </label>
                        <button type="submit">Apply</button>
                    </form>
                    ${renderList(
                        result.data,
                        item => `
                            <div class="list-item-header">
                                <strong>${esc(item.displayName)}</strong>
                                <button type="button" data-calendar-connection-id="${esc(item.id)}">Inspect</button>
                            </div>
                            <div class="client-badge-row">
                                <span class="client-badge client-badge--source">${esc(item.providerType)}</span>
                                <span class="client-badge client-badge--info">${esc(item.mode)}</span>
                                <span class="client-badge ${calendarStatusBadgeClass(item)}">${esc(item.revokedAt ? "Disconnected" : item.status)}</span>
                            </div>
                            ${renderMetadataRows([
                                { label: "Owner", value: item.userId ? `User ${item.userId}` : item.organizationId ? `Org ${item.organizationId}` : "n/a" },
                                { label: "Account", value: item.providerAccountEmail || "n/a" },
                                { label: "Last sync", value: item.lastSyncAt ? formatDate(item.lastSyncAt) : "never" },
                                { label: "Token expires", value: item.accessTokenExpiresAt ? formatDate(item.accessTokenExpiresAt) : "n/a" },
                                { label: "Refresh token", value: item.hasRefreshToken ? "stored (encrypted)" : "none" },
                                { label: "Last error", value: item.lastError || "" }
                            ])}
                        `,
                        "No calendar connections yet. Apps create them through the SqlOS calendar connect flow."
                    )}
                </section>
                ${renderCalendarConnectionDetail(selectedConnection)}
            </div>
        `;

        bindForm("calendar-filter-form", async form => {
            calendarConnectionFilters.search = String(form.get("search") || "").trim();
            calendarConnectionFilters.includeRevoked = form.get("includeRevoked") === "on";
            selectedCalendarConnectionId = null;
        });

        document.querySelectorAll("[data-calendar-connection-id]").forEach(button => {
            button.addEventListener("click", async () => {
                selectedCalendarConnectionId = button.dataset.calendarConnectionId || null;
                await render();
            });
        });

        bindCalendarConnectionAction("[data-calendar-sync]", "calendarSync", "sync", "Calendar sync completed.");
        bindCalendarConnectionAction("[data-calendar-refresh]", "calendarRefresh", "refresh", "Calendar access token refreshed.");
        bindCalendarConnectionAction("[data-calendar-disconnect]", "calendarDisconnect", "disconnect", "Calendar connection disconnected.");

        bindPagination("#calendar-pagination-top", "calendar-connections", result, () => {
            selectedCalendarConnectionId = null;
            return render();
        });
    }

    function bindCalendarConnectionAction(selector, dataKey, action, successMessage) {
        document.querySelectorAll(selector).forEach(button => {
            button.addEventListener("click", async () => {
                try {
                    await fetchJson(`${calendarApiBasePath}/connections/${encodeURIComponent(button.dataset[dataKey])}/${action}`, {
                        method: "POST"
                    });
                    setFlash("success", successMessage);
                } catch (error) {
                    setFlash("error", error.message || String(error));
                }

                await render();
            });
        });
    }

    function calendarStatusBadgeClass(item) {
        if (item.revokedAt) {
            return "client-badge--muted";
        }

        if (item.status === "Error") {
            return "client-badge--danger";
        }

        return "client-badge--success";
    }

    function renderCalendarConnectionDetail(detail) {
        if (!detail) {
            return `
                <section class="panel">
                    <h2>Connection detail</h2>
                    <div class="empty-state-block">Select a connection to inspect sync health, calendars, and token status.</div>
                </section>
            `;
        }

        const connection = detail.connection;
        const isRevoked = Boolean(connection.revokedAt);
        return `
            <section class="panel">
                <div class="panel-actions">
                    <h2>${esc(connection.displayName)}</h2>
                    <div class="form-actions">
                        ${isRevoked || connection.mode === "ConnectionOnly" ? "" : `<button type="button" data-calendar-sync="${esc(connection.id)}">Sync now</button>`}
                        ${isRevoked ? "" : `<button type="button" data-calendar-refresh="${esc(connection.id)}">Refresh token</button>`}
                        ${isRevoked ? "" : `<button type="button" data-calendar-disconnect="${esc(connection.id)}">Disconnect</button>`}
                    </div>
                </div>
                ${renderMetadataRows([
                    { label: "Provider", value: connection.providerType },
                    { label: "Mode", value: connection.mode },
                    { label: "Status", value: isRevoked ? `Disconnected (${connection.revokedAt ? formatDate(connection.revokedAt) : "n/a"})` : connection.status },
                    { label: "Owner", value: connection.userId ? `User ${connection.userId}` : connection.organizationId ? `Org ${connection.organizationId}` : "n/a" },
                    { label: "Account", value: connection.providerAccountEmail || "n/a" },
                    { label: "Scopes", value: (connection.scopes || []).join(" ") || "n/a" },
                    { label: "Connected", value: formatDate(connection.createdAt) },
                    { label: "Last sync", value: connection.lastSyncAt ? formatDate(connection.lastSyncAt) : "never" },
                    { label: "Synced events", value: String(detail.eventCount ?? 0) },
                    { label: "Last error", value: connection.lastError || "" }
                ])}
                <h3>Calendars</h3>
                ${renderList(
                    detail.calendars,
                    calendar => `
                        <div class="list-item-header">
                            <strong>${esc(calendar.displayName || calendar.providerCalendarId)}</strong>
                            <span class="client-badge ${calendar.lastSyncStatus === "error" ? "client-badge--danger" : calendar.isSyncEnabled ? "client-badge--success" : "client-badge--muted"}">
                                ${esc(calendar.lastSyncStatus === "error" ? "Error" : calendar.isSyncEnabled ? "Syncing" : "Paused")}
                            </span>
                        </div>
                        ${renderMetadataRows([
                            { label: "Calendar id", value: calendar.providerCalendarId },
                            { label: "Last sync", value: calendar.lastSyncCompletedAt ? formatDate(calendar.lastSyncCompletedAt) : "never" },
                            { label: "Cursor", value: calendar.hasSyncCursor ? "incremental" : "full window" },
                            { label: "Events", value: String(calendar.eventCount ?? 0) },
                            { label: "Error", value: calendar.lastSyncError || "" }
                        ])}
                    `,
                    connection.mode === "ConnectionOnly"
                        ? "Connection-only mode: the app calls the provider directly; SqlOS stores no calendars or events."
                        : "No calendars enrolled yet. The first sync enrolls the provider's primary calendar automatically."
                )}
            </section>
        `;
    }

    async function renderEmailRoute(view) {
        const config = emailViews[view] || emailViews.templates;
        setHeader("Communications", config.title, config.description);

        if (view === "messages") {
            await renderEmailMessages();
            return;
        }

        await renderEmailTemplates();
    }

    function renderEmailTabs(activeView) {
        return `
            <div class="tab-strip email-tab-strip">
                <a class="tab-link ${activeView === "templates" ? "active" : ""}" href="${esc(pathForRoute("email-templates"))}" data-dashboard-route="email-templates">Templates</a>
                <a class="tab-link ${activeView === "messages" ? "active" : ""}" href="${esc(pathForRoute("email-messages"))}" data-dashboard-route="email-messages">Messages</a>
            </div>
        `;
    }

    async function renderEmailTemplates() {
        renderLoading("Loading email templates...");
        const pager = getPagerState("email-templates", 10);
        const templates = await fetchJson(`${emailApiBasePath}/templates?${pagerQuery(pager)}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            ${renderEmailTabs("templates")}
            <div class="panel-grid email-template-grid">
                <section class="panel">
                    <h2>Create Template</h2>
                    <form id="create-email-template-form">
                        ${renderEmailTemplateFormFields()}
                        <div class="form-actions">
                            <button type="submit">Create template</button>
                        </div>
                    </form>
                </section>
                <section class="panel">
                    <div class="panel-actions">
                        <div>
                            <h2>Templates</h2>
                            <p>Template keys are stable API identifiers. Editing content increments the version used by future sends.</p>
                        </div>
                    </div>
                    ${renderList(
                        templates.data || [],
                        renderEmailTemplateItem,
                        "No email templates yet."
                    )}
                    <div class="email-template-pagination">
                        ${renderPagination(pager, templates)}
                    </div>
                </section>
            </div>
        `;

        bindForm("create-email-template-form", async form => {
            await fetchJson(`${emailApiBasePath}/templates`, {
                method: "POST",
                body: JSON.stringify(emailTemplatePayloadFromForm(form))
            });
            setFlash("success", "Email template created.");
            restartPagerWindow("email-templates");
        });

        (templates.data || []).forEach(template => {
            const safeId = domId(template.id);
            bindForm(`edit-email-template-${safeId}`, async form => {
                await fetchJson(`${emailApiBasePath}/templates/${encodeURIComponent(template.id)}`, {
                    method: "PUT",
                    body: JSON.stringify(emailTemplatePayloadFromForm(form))
                });
                setFlash("success", "Email template updated.");
            });
            bindEmailPreviewForm(template.id, safeId);
        });

        document.querySelectorAll("[data-email-template-delete]").forEach(button => {
            button.addEventListener("click", async () => {
                await fetchJson(`${emailApiBasePath}/templates/${encodeURIComponent(button.dataset.emailTemplateDelete)}`, {
                    method: "DELETE"
                });
                setFlash("success", "Email template removed or deactivated.");
                await render();
            });
        });

        bindPagination(".email-template-pagination", "email-templates", templates, () => render());
    }

    function renderEmailTemplateItem(template) {
        const safeId = domId(template.id);
        return `
            <details class="email-template-item">
                <summary>
                    <span>
                        <strong>${esc(template.displayName || template.key)}</strong>
                        <span class="inline-code">${esc(template.key)}</span>
                    </span>
                    <span class="client-badge ${template.isActive ? "client-badge--success" : "client-badge--muted"}">${template.isActive ? "Active" : "Inactive"} v${esc(template.version)}</span>
                </summary>
                <div class="email-template-body">
                    <form id="edit-email-template-${safeId}">
                        ${renderEmailTemplateFormFields(template)}
                        <div class="form-actions">
                            <button type="submit">Save changes</button>
                            <button type="button" data-email-template-delete="${esc(template.id)}">Delete</button>
                        </div>
                    </form>
                    <form id="preview-email-template-${safeId}" class="nested-form">
                        <label>
                            Sample variables JSON
                            <textarea name="variables" spellcheck="false">${esc(JSON.stringify(template.variables || {}, null, 2))}</textarea>
                        </label>
                        <div class="form-actions">
                            <button type="submit">Preview</button>
                        </div>
                    </form>
                    <div id="preview-output-${safeId}" class="email-preview-output"></div>
                </div>
            </details>
        `;
    }

    function renderEmailTemplateFormFields(template = null) {
        const variables = template?.variables || {};
        const isCreateForm = !template;
        return `
            <label>
                Key
                <input name="key" value="${esc(template?.key || "")}" placeholder="order-shipped" required>
            </label>
            <label>
                Display name
                <input name="displayName" value="${esc(template?.displayName || "")}" placeholder="Order shipped" required>
            </label>
            <label>
                Subject template
                <input name="subjectTemplate" value="${esc(template?.subjectTemplate || "")}" placeholder="Order {orderId} shipped" required>
            </label>
            <label>
                HTML body template
                <textarea name="htmlBodyTemplate" spellcheck="false" required>${esc(template?.htmlBodyTemplate ?? (isCreateForm ? "<p>Your order {orderId} shipped.</p>" : ""))}</textarea>
            </label>
            <label>
                Text body template
                <textarea name="textBodyTemplate" spellcheck="false" required>${esc(template?.textBodyTemplate ?? (isCreateForm ? "Your order {orderId} shipped." : ""))}</textarea>
            </label>
            <label>
                Variables JSON
                <textarea name="variables" spellcheck="false">${esc(JSON.stringify(variables, null, 2))}</textarea>
            </label>
            <label class="checkbox-row">
                <input type="checkbox" name="isActive" ${template?.isActive === false ? "" : "checked"}>
                Active
            </label>
        `;
    }

    function emailTemplatePayloadFromForm(form) {
        return {
            key: String(form.get("key") || ""),
            displayName: String(form.get("displayName") || ""),
            subjectTemplate: String(form.get("subjectTemplate") || ""),
            htmlBodyTemplate: String(form.get("htmlBodyTemplate") || ""),
            textBodyTemplate: String(form.get("textBodyTemplate") || ""),
            variables: parseJsonObject(form.get("variables")),
            isActive: form.get("isActive") === "on"
        };
    }

    function bindEmailPreviewForm(templateId, safeId) {
        const form = document.getElementById(`preview-email-template-${safeId}`);
        const output = document.getElementById(`preview-output-${safeId}`);
        if (!form || !output) {
            return;
        }

        form.addEventListener("submit", async event => {
            event.preventDefault();
            output.innerHTML = `<div class="loading">Rendering preview...</div>`;

            try {
                const preview = await fetchJson(`${emailApiBasePath}/templates/${encodeURIComponent(templateId)}/preview`, {
                    method: "POST",
                    body: JSON.stringify({ variables: parseJsonObject(new FormData(form).get("variables")) })
                });
                output.innerHTML = `
                    <div class="email-preview-card">
                        <strong>${esc(preview.subject)}</strong>
                        <div class="email-preview-html">${preview.htmlBody}</div>
                        <pre class="json-preview">${esc(preview.textBody)}</pre>
                    </div>
                `;
            } catch (error) {
                output.innerHTML = `<div class="error-banner">${esc(error.message || String(error))}</div>`;
            }
        });
    }

    async function renderEmailMessages() {
        renderLoading("Loading email messages...");
        const emailFilterKey = [
            emailMessageFilters.status,
            emailMessageFilters.templateKey,
            emailMessageFilters.recipient,
            emailMessageFilters.from,
            emailMessageFilters.to
        ].join("|");
        const pager = resetPager("email-messages", 25, emailFilterKey);
        const params = new URLSearchParams(pagerQuery(pager));

        Object.entries(emailMessageFilters).forEach(([key, value]) => {
            if (value) {
                params.set(key, value);
            }
        });

        const messages = await fetchJson(`${emailApiBasePath}/messages?${params.toString()}`);

        content.innerHTML = `
            ${consumeFlashHtml()}
            ${renderEmailTabs("messages")}
            <section class="panel">
                <h2>Messages</h2>
                <form id="email-message-filter-form" class="client-filter-form">
                    <label>
                        Status
                        <select name="status">
                            ${["all", "pending", "queued", "failed"].map(status => `<option value="${status}" ${emailMessageFilters.status === status ? "selected" : ""}>${status}</option>`).join("")}
                        </select>
                    </label>
                    <label>
                        Template key
                        <input name="templateKey" value="${esc(emailMessageFilters.templateKey)}" placeholder="order-shipped">
                    </label>
                    <label>
                        Recipient
                        <input name="recipient" value="${esc(emailMessageFilters.recipient)}" placeholder="user@example.com">
                    </label>
                    <label>
                        From
                        <input name="from" type="datetime-local" value="${esc(emailMessageFilters.from)}">
                    </label>
                    <label>
                        To
                        <input name="to" type="datetime-local" value="${esc(emailMessageFilters.to)}">
                    </label>
                    <button type="submit">Filter</button>
                </form>
                ${renderList(
                    messages.data || [],
                    renderEmailMessageItem,
                    "No email messages match the current filters."
                )}
                <div class="email-message-pagination">
                    ${renderPagination(pager, messages)}
                </div>
            </section>
        `;

        bindForm("email-message-filter-form", async form => {
            emailMessageFilters.status = String(form.get("status") || "all");
            emailMessageFilters.templateKey = String(form.get("templateKey") || "").trim();
            emailMessageFilters.recipient = String(form.get("recipient") || "").trim();
            emailMessageFilters.from = String(form.get("from") || "");
            emailMessageFilters.to = String(form.get("to") || "");
        });

        bindPagination(".email-message-pagination", "email-messages", messages, () => render());
    }

    function renderEmailMessageItem(item) {
        const badgeTone = item.status === "failed" ? "danger" : item.status === "queued" ? "success" : "muted";
        return `
            <div class="email-message-row">
                <div class="list-item-header">
                    <div>
                        <strong>${esc(item.renderedSubject || item.templateKey)}</strong>
                        <div>${esc(item.to)}</div>
                    </div>
                    <span class="client-badge client-badge--${badgeTone}">${esc(item.status)}</span>
                </div>
                ${renderMetadataRows([
                    { label: "Template", value: `${item.templateKey} v${item.templateVersion}` },
                    { label: "Created", value: formatDate(item.createdAt) },
                    { label: "Sent", value: item.sentAt ? formatDate(item.sentAt) : "n/a" },
                    { label: "Provider", value: item.providerMessageId || "n/a" },
                    { label: "Error", value: item.sanitizedError || "n/a" }
                ])}
                <pre class="json-preview email-text-preview">${esc(item.renderedTextPreview || "")}</pre>
            </div>
        `;
    }

    function parseJsonObject(value) {
        const raw = String(value || "").trim();
        if (!raw) {
            return {};
        }

        const parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
            throw new Error("Variables must be a JSON object.");
        }

        return parsed;
    }

    function domId(value) {
        return String(value || "").replace(/[^A-Za-z0-9_-]/g, "-");
    }

    async function renderFgaRoute(route) {
        const config = fgaViews[route.view] || fgaViews.resources;
        setHeader("Fine-Grained Auth", config.title, config.description);
        content.innerHTML = `
            ${consumeFlashHtml()}
            <section class="fga-component-shell" aria-label="${esc(config.title)}">
                <div id="fga-dashboard-host"></div>
            </section>
        `;

        if (!window.SqlOSFgaDashboard?.mount) {
            throw new Error("The FGA dashboard component failed to load.");
        }

        const host = document.getElementById("fga-dashboard-host");
        activeFgaDashboard = window.SqlOSFgaDashboard.mount({
            host,
            basePath: fgaDashboardPath,
            dashboardBasePath,
            initialRoute: route.componentRoute || config.hash,
            onNavigate(componentRoute) {
                const targetPath = `${fgaDashboardPath}${componentRoute}`;
                history.pushState({}, "", targetPath);

                const view = componentRoute.split("/").filter(Boolean)[0];
                const nextConfig = fgaViews[view] || fgaViews.resources;
                setHeader("Fine-Grained Auth", nextConfig.title, nextConfig.description);
                updateActiveNav(`fga-${view}`);
            }
        });
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
