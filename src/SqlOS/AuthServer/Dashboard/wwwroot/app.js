const statsElement = document.getElementById("stats");
const setupResultElement = document.getElementById("sso-setup-result");
const adminApiBasePath = "/sqlos/admin/auth/api";
const embedMode = new URLSearchParams(window.location.search).get("embed") === "1";
const sectionMap = {
  overview: "section-overview",
  organizations: "section-organizations",
  users: "section-users",
  memberships: "section-memberships",
  clients: "section-clients",
  sso: "section-sso",
  security: "section-security",
  sessions: "section-sessions",
  audit: "section-audit"
};

if (embedMode) {
  document.body.classList.add("embed-mode");
}

async function fetchJson(url, options = {}) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    }
  });

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
}

async function refresh() {
  const [stats, organizations, users, memberships, clients, ssoConnections, sessions, auditEvents, securitySettings] = await Promise.all([
    fetchJson(`${adminApiBasePath}/stats`),
    fetchJson(`${adminApiBasePath}/organizations`),
    fetchJson(`${adminApiBasePath}/users`),
    fetchJson(`${adminApiBasePath}/memberships`),
    fetchJson(`${adminApiBasePath}/clients`),
    fetchJson(`${adminApiBasePath}/sso-connections`),
    fetchJson(`${adminApiBasePath}/sessions`),
    fetchJson(`${adminApiBasePath}/audit-events`),
    fetchJson(`${adminApiBasePath}/settings/security`)
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

  renderList("organizations", organizations, item => `
    <strong>${item.name}</strong><br>
    ${item.slug}<br>
    ${item.primaryDomain ? `Domain: ${item.primaryDomain}<br>` : ""}
    Members: ${item.membershipCount} | Enabled SSO: ${item.enabledSsoConnections}
  `);

  renderList("users", users, item => `<strong>${item.displayName}</strong><br>${item.defaultEmail || ""}<br>${item.id}`);
  renderList("memberships", memberships, item => `<strong>${item.organization}</strong><br>${item.user} (${item.userEmail || "no email"})<br>${item.role}`);
  renderList("clients", clients, item => `<strong>${item.clientId}</strong><br>${item.audience}<br>${item.redirectUris}`);
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
  const message = err.status === 404
    ? "Dashboard data is unavailable outside development unless a dashboard authorization callback is configured."
    : err.message;

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
      audience: form.get("audience") || "sqlos",
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
  await fetchJson(`${adminApiBasePath}/settings/security`, {
    method: "PUT",
    body: JSON.stringify({
      refreshTokenLifetimeMinutes: Number(form.get("refreshTokenLifetimeMinutes")),
      sessionIdleTimeoutMinutes: Number(form.get("sessionIdleTimeoutMinutes")),
      sessionAbsoluteLifetimeMinutes: Number(form.get("sessionAbsoluteLifetimeMinutes"))
    })
  });
  await refresh();
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
