const statsElement = document.getElementById("stats");
const setupResultElement = document.getElementById("sso-setup-result");
const dashboardBasePath = window.location.pathname.split("/admin/auth")[0] || "/sqlos";
const adminApiBasePath = `${dashboardBasePath}/admin/auth/api`;
const authServerBasePath = `${dashboardBasePath}/auth`;
const embedMode = new URLSearchParams(window.location.search).get("embed") === "1";
const sectionMap = {
  overview: "section-overview",
  organizations: "section-organizations",
  users: "section-users",
  memberships: "section-memberships",
  clients: "section-clients",
  sso: "section-sso",
  security: "section-security",
  authpage: "section-authpage",
  authserver: "section-authserver",
  sessions: "section-sessions",
  audit: "section-audit"
};

if (embedMode) {
  document.body.classList.add("embed-mode");
}

async function fetchJson(url, options = {}) {
  const { skipUnauthorizedRedirect, ...requestOptions } = options;
  const response = await fetch(url, {
    ...requestOptions,
    credentials: "same-origin",
    headers: {
      "Content-Type": "application/json",
      ...(requestOptions.headers || {})
    }
  });

  if (response.status === 401 && !skipUnauthorizedRedirect) {
    const next = encodeURIComponent(`${window.location.pathname}${window.location.search}`);
    window.top.location.href = `${dashboardBasePath}/login?next=${next}`;
    throw new Error("Unauthorized");
  }

  if (!response.ok) {
    const text = await response.text();
    const error = new Error(text || `${response.status}`);
    error.status = response.status;
    throw error;
  }

  return response.status === 204 ? null : await response.json();
}

function renderList(elementId, items, formatter) {
  const element = document.getElementById(elementId);
  element.innerHTML = items.map(item => `<div class="list-item">${formatter(item)}</div>`).join("");
}

function renderSetupResult(connection) {
  if (!connection) {
    setupResultElement.innerHTML = "";
    return;
  }

  setupResultElement.innerHTML = `
    <strong>Draft created: ${connection.id}</strong>
    <div>Give these values to the Entra admin:</div>
    <div><strong>SP Entity ID</strong><br>${connection.serviceProviderEntityId}</div>
    <div><strong>ACS URL</strong><br>${connection.assertionConsumerServiceUrl}</div>
    <div><strong>Org primary domain</strong><br>${connection.primaryDomain || "Set the organization primary domain before enabling SSO."}</div>
    <div>After the enterprise application is configured, paste the federation metadata XML below.</div>
  `;
}

function renderSecuritySettings(settings) {
  const form = document.getElementById("security-settings-form");
  form.elements.refreshTokenLifetimeMinutes.value = settings.refreshTokenLifetimeMinutes;
  form.elements.sessionIdleTimeoutMinutes.value = settings.sessionIdleTimeoutMinutes;
  form.elements.sessionAbsoluteLifetimeMinutes.value = settings.sessionAbsoluteLifetimeMinutes;
  const rotationForm = document.getElementById("key-rotation-settings-form");
  rotationForm.elements.signingKeyRotationIntervalDays.value = settings.signingKeyRotationIntervalDays;
  rotationForm.elements.signingKeyGraceWindowDays.value = settings.signingKeyGraceWindowDays;
  rotationForm.elements.signingKeyRetiredCleanupDays.value = settings.signingKeyRetiredCleanupDays;
}

function renderSigningKeys(data) {
  const info = document.getElementById("signing-keys-info");
  info.innerHTML = `
    <strong>Rotation interval:</strong> ${data.rotationIntervalDays} days &nbsp;|&nbsp;
    <strong>Grace window:</strong> ${data.graceWindowDays} days &nbsp;|&nbsp;
    <strong>Next rotation due:</strong> ${data.nextRotationDue ? new Date(data.nextRotationDue).toLocaleString() : "N/A"}
  `;

  const list = document.getElementById("signing-keys-list");
  list.innerHTML = data.keys.map(k => `<div class="list-item">
    <strong>${k.kid}</strong> (${k.algorithm})<br>
    Status: ${k.isActive ? "Active" : "Retired"} &nbsp;|&nbsp; Age: ${k.ageDays} days<br>
    Activated: ${new Date(k.activatedAt).toLocaleString()}${k.retiredAt ? `<br>Retired: ${new Date(k.retiredAt).toLocaleString()}` : ""}
  </div>`).join("");
}

function renderAuthPageSettings(settings) {
  const form = document.getElementById("auth-page-settings-form");
  form.elements.pageTitle.value = settings.pageTitle;
  form.elements.pageSubtitle.value = settings.pageSubtitle;
  form.elements.primaryColor.value = settings.primaryColor;
  form.elements.accentColor.value = settings.accentColor;
  form.elements.backgroundColor.value = settings.backgroundColor;
  form.elements.layout.value = settings.layout;
  form.elements.enablePasswordSignup.checked = settings.enablePasswordSignup;
  form.elements.enabledCredentialTypes.value = (settings.enabledCredentialTypes || []).join(", ");
  form.elements.logoBase64.value = settings.logoBase64 || "";
}

function renderAuthorizationServerMetadata(metadata) {
  const element = document.getElementById("auth-server-metadata");
  element.innerHTML = `
    <strong>Issuer</strong><br>${metadata.issuer}<br><br>
    <strong>Authorization endpoint</strong><br>${metadata.authorizationEndpoint}<br><br>
    <strong>Token endpoint</strong><br>${metadata.tokenEndpoint}<br><br>
    <strong>JWKS URI</strong><br>${metadata.jwksUri}<br><br>
    <strong>Grant types</strong><br>${metadata.grantTypesSupported.join(", ")}<br><br>
    <strong>PKCE methods</strong><br>${metadata.codeChallengeMethodsSupported.join(", ")}
  `;
}

async function refresh() {
  const [stats, organizations, users, memberships, clients, ssoConnections, sessions, auditEvents, securitySettings, authPageSettings, authServerMetadata, signingKeys] = await Promise.all([
    fetchJson(`${adminApiBasePath}/stats`),
    fetchJson(`${adminApiBasePath}/organizations`),
    fetchJson(`${adminApiBasePath}/users`),
    fetchJson(`${adminApiBasePath}/memberships`),
    fetchJson(`${adminApiBasePath}/clients`),
    fetchJson(`${adminApiBasePath}/sso-connections`),
    fetchJson(`${adminApiBasePath}/sessions`),
    fetchJson(`${adminApiBasePath}/audit-events`),
    fetchJson(`${adminApiBasePath}/settings/security`),
    fetchJson(`${adminApiBasePath}/settings/auth-page`),
    fetchJson(`${authServerBasePath}/.well-known/oauth-authorization-server`),
    fetchJson(`${adminApiBasePath}/signing-keys`)
  ]);

  statsElement.innerHTML = `
    <h2>System Stats</h2>
    <div>${stats.organizations} organizations</div>
    <div>${stats.users} users</div>
    <div>${stats.clients} clients</div>
    <div>${stats.ssoConnections} SSO connections</div>
    <div>${stats.sessions} sessions</div>
    <div>${stats.auditEvents} audit events</div>
  `;

  renderSecuritySettings(securitySettings);
  renderSigningKeys(signingKeys);
  renderAuthPageSettings(authPageSettings);
  renderAuthorizationServerMetadata(authServerMetadata);

  renderList("organizations", organizations, item => `
    <strong>${item.name}</strong><br>
    ${item.slug}<br>
    ${item.primaryDomain ? `Domain: ${item.primaryDomain}<br>` : ""}
    Members: ${item.membershipCount} | Enabled SSO: ${item.enabledSsoConnections}
  `);

  renderList("users", users, item => `<strong>${item.displayName}</strong><br>${item.defaultEmail || ""}<br>${item.id}`);
  renderList("memberships", memberships, item => `<strong>${item.organization}</strong><br>${item.user} (${item.userEmail || "no email"})<br>${item.role}`);
  renderList("clients", clients, item => `
    <strong>${item.clientId}</strong><br>
    ${item.name}<br>
    ${item.description || "No description"}<br>
    Audience: ${item.audience} | Type: ${item.clientType}<br>
    PKCE: ${item.requirePkce ? "required" : "optional"} | First-party: ${item.isFirstParty ? "yes" : "no"}<br>
    Scopes: ${item.allowedScopes}<br>
    Redirect URIs: ${item.redirectUris}
  `);
  renderList("sso-connections", ssoConnections, item => `
    <strong>${item.displayName}</strong><br>
    ${item.organization} (${item.primaryDomain || "no domain"})<br>
    Status: ${item.setupStatus} | Enabled: ${item.isEnabled}<br>
    SP Entity ID: ${item.serviceProviderEntityId}<br>
    ACS URL: ${item.assertionConsumerServiceUrl}
  `);
  renderList("sessions", sessions, item => `<strong>${item.user}</strong><br>${item.authenticationMethod || "unknown"} / ${item.id}<br>${item.clientApplicationId || "no client"}<br>${item.createdAt}`);
  renderList("audit-events", auditEvents, item => `<strong>${item.eventType}</strong><br>${item.occurredAt}<br>${item.actorType}: ${item.actorId || "n/a"}`);
}

function renderDashboardUnavailable(err) {
  const message = err.status === 401
    ? "Your dashboard session expired. Sign in again to continue."
    : (err.status === 404
      ? "Dashboard data is unavailable outside development unless dashboard access is enabled."
      : err.message);

  statsElement.innerHTML = `
    <h2>Dashboard unavailable</h2>
    <p>${message}</p>
  `;
}

function scrollToCurrentSection() {
  const sectionKey = window.location.hash.replace(/^#/, "") || "overview";
  const sectionId = sectionMap[sectionKey];
  if (!sectionId) {
    return;
  }

  const element = document.getElementById(sectionId);
  element?.scrollIntoView({ block: "start", behavior: "smooth" });
}

document.getElementById("create-org-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/organizations`, {
    method: "POST",
    body: JSON.stringify({
      name: form.get("name"),
      slug: form.get("slug") || null,
      primaryDomain: form.get("primaryDomain") || null
    })
  });
  event.target.reset();
  await refresh();
});

document.getElementById("create-user-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/users`, {
    method: "POST",
    body: JSON.stringify({
      displayName: form.get("displayName"),
      email: form.get("email"),
      password: form.get("password") || null
    })
  });
  event.target.reset();
  await refresh();
});

document.getElementById("create-membership-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/memberships`, {
    method: "POST",
    body: JSON.stringify({
      organizationId: form.get("organizationId"),
      userId: form.get("userId"),
      role: form.get("role") || "member"
    })
  });
  event.target.reset();
  await refresh();
});

document.getElementById("create-client-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/clients`, {
    method: "POST",
    body: JSON.stringify({
      clientId: form.get("clientId"),
      name: form.get("name"),
      description: form.get("description") || null,
      audience: form.get("audience") || "sqlos",
      allowedScopes: String(form.get("allowedScopes") || "")
        .split(/[\n,\s]+/)
        .map(value => value.trim())
        .filter(Boolean),
      requirePkce: form.get("requirePkce") === "on",
      isFirstParty: form.get("isFirstParty") === "on",
      redirectUris: String(form.get("redirectUris") || "")
        .split("\n")
        .map(value => value.trim())
        .filter(Boolean)
    })
  });
  event.target.reset();
  await refresh();
});

document.getElementById("security-settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  const rotationForm = document.getElementById("key-rotation-settings-form");
  await fetchJson(`${adminApiBasePath}/settings/security`, {
    method: "PUT",
    body: JSON.stringify({
      refreshTokenLifetimeMinutes: Number(form.get("refreshTokenLifetimeMinutes")),
      sessionIdleTimeoutMinutes: Number(form.get("sessionIdleTimeoutMinutes")),
      sessionAbsoluteLifetimeMinutes: Number(form.get("sessionAbsoluteLifetimeMinutes")),
      signingKeyRotationIntervalDays: Number(rotationForm.elements.signingKeyRotationIntervalDays.value),
      signingKeyGraceWindowDays: Number(rotationForm.elements.signingKeyGraceWindowDays.value),
      signingKeyRetiredCleanupDays: Number(rotationForm.elements.signingKeyRetiredCleanupDays.value)
    })
  });
  await refresh();
});

document.getElementById("key-rotation-settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const securitySettings = await fetchJson(`${adminApiBasePath}/settings/security`);
  const rotationForm = event.target;
  await fetchJson(`${adminApiBasePath}/settings/security`, {
    method: "PUT",
    body: JSON.stringify({
      refreshTokenLifetimeMinutes: securitySettings.refreshTokenLifetimeMinutes,
      sessionIdleTimeoutMinutes: securitySettings.sessionIdleTimeoutMinutes,
      sessionAbsoluteLifetimeMinutes: securitySettings.sessionAbsoluteLifetimeMinutes,
      signingKeyRotationIntervalDays: Number(rotationForm.elements.signingKeyRotationIntervalDays.value),
      signingKeyGraceWindowDays: Number(rotationForm.elements.signingKeyGraceWindowDays.value),
      signingKeyRetiredCleanupDays: Number(rotationForm.elements.signingKeyRetiredCleanupDays.value)
    })
  });
  await refresh();
});

document.getElementById("rotate-key-btn").addEventListener("click", async () => {
  if (!confirm("Are you sure you want to rotate the signing key? Existing tokens will remain valid during the grace window.")) return;
  await fetchJson(`${adminApiBasePath}/signing-keys/rotate`, { method: "POST" });
  await refresh();
});

document.getElementById("auth-page-settings-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/settings/auth-page`, {
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
  await refresh();
});

document.getElementById("auth-page-logo-file").addEventListener("change", async (event) => {
  const input = event.target;
  const file = input.files?.[0];
  if (!file) {
    return;
  }

  const form = document.getElementById("auth-page-settings-form");
  const reader = new FileReader();
  reader.onload = () => {
    form.elements.logoBase64.value = String(reader.result || "");
  };
  reader.readAsDataURL(file);
});

document.getElementById("create-sso-draft-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  const result = await fetchJson(`${adminApiBasePath}/sso-connections/draft`, {
    method: "POST",
    body: JSON.stringify({
      organizationId: form.get("organizationId"),
      displayName: form.get("displayName"),
      primaryDomain: form.get("primaryDomain") || null,
      autoProvisionUsers: form.get("autoProvisionUsers") === "on",
      autoLinkByEmail: form.get("autoLinkByEmail") === "on"
    })
  });

  renderSetupResult({
    ...result,
    primaryDomain: form.get("primaryDomain") || null
  });
  event.target.reset();
  await refresh();
});

document.getElementById("import-sso-metadata-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = new FormData(event.target);
  await fetchJson(`${adminApiBasePath}/sso-connections/${form.get("connectionId")}/metadata`, {
    method: "POST",
    body: JSON.stringify({
      metadataXml: form.get("metadataXml")
    })
  });
  event.target.reset();
  await refresh();
});

window.addEventListener("hashchange", () => {
  scrollToCurrentSection();
});

refresh().then(() => {
  scrollToCurrentSection();
}).catch(err => {
  renderDashboardUnavailable(err);
});
