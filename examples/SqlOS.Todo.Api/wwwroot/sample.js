const oauthStorageKey = "sqlos.todo.oauth";
const tokenStorageKey = "sqlos.todo.tokens";

document.addEventListener("DOMContentLoaded", async () => {
  const page = document.body.dataset.page;
  if (!page) {
    return;
  }

  try {
    if (page === "index") {
      await initIndexPage();
    } else if (page === "callback") {
      await handleCallbackPage();
    } else if (page === "app") {
      await initAppPage();
    } else if (page === "headless") {
      await initHeadlessPage();
    }
  } catch (error) {
    console.error(error);
    const message = error instanceof Error ? error.message : String(error);
    document.querySelectorAll(".status-text, #callback-status").forEach(element => {
      element.textContent = message;
    });
  }
});

async function initIndexPage() {
  const config = await getSampleConfig();
  const configPre = document.getElementById("sample-config");
  if (configPre) {
    configPre.textContent = JSON.stringify(
      {
        issuer: config.issuer,
        resource: config.resource,
        hostedClient: config.hostedClient,
        localClient: config.localClient,
        portableClient: config.portableClient,
        cimdEnabled: config.cimdEnabled,
        dcrEnabled: config.dcrEnabled
      },
      null,
      2
    );
  }

  document.getElementById("hosted-login-button")?.addEventListener("click", async () => {
    await startAuthorization("login");
  });

  document.getElementById("hosted-signup-button")?.addEventListener("click", async () => {
    await startAuthorization("signup");
  });
}

async function handleCallbackPage() {
  const params = new URLSearchParams(window.location.search);
  const status = document.getElementById("callback-status");
  const debug = document.getElementById("callback-debug");
  const stored = readOAuthState();
  if (!stored) {
    throw new Error("The PKCE state is missing. Start the flow again from the sample home page.");
  }

  if (params.get("error")) {
    throw new Error(params.get("error"));
  }

  const state = params.get("state");
  const code = params.get("code");
  if (!code || !state) {
    throw new Error("The authorization response is missing its code or state.");
  }

  if (state !== stored.state) {
    throw new Error("The OAuth state did not match the stored PKCE state.");
  }

  status.textContent = "Exchanging your authorization code for tokens...";

  const body = new URLSearchParams({
    grant_type: "authorization_code",
    code,
    client_id: stored.clientId,
    redirect_uri: stored.redirectUri,
    code_verifier: stored.codeVerifier,
    resource: stored.resource
  });

  const response = await fetch("/sqlos/auth/token", {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded"
    },
    body
  });

  const payload = await response.json();
  if (!response.ok) {
    debug.textContent = JSON.stringify(payload, null, 2);
    throw new Error(payload.error_description || payload.message || "Token exchange failed.");
  }

  writeTokens(payload);
  sessionStorage.removeItem(oauthStorageKey);
  status.textContent = "Success. Redirecting to the Todo UI...";
  debug.textContent = JSON.stringify(
    {
      token_type: payload.token_type,
      expires_in: payload.expires_in,
      scope: payload.scope,
      aud: parseJwt(payload.access_token)?.aud
    },
    null,
    2
  );

  window.location.replace("/app.html");
}

async function initAppPage() {
  const sessionInfo = document.getElementById("session-info");
  const todoList = document.getElementById("todo-list");
  const todoForm = document.getElementById("todo-form");
  const todoMessage = document.getElementById("todo-message");
  const tokens = readTokens();

  if (!tokens?.access_token) {
    sessionInfo.textContent = "No local access token was found. Start from the hosted sign-in flow.";
    todoList.innerHTML = `<div class="empty-state">Sign in from the sample home page to read and write todos.</div>`;
    todoForm?.setAttribute("hidden", "hidden");
    return;
  }

  document.getElementById("logout-button")?.addEventListener("click", () => {
    localStorage.removeItem(tokenStorageKey);
    window.location.reload();
  });

  document.getElementById("refresh-todos-button")?.addEventListener("click", async () => {
    await refreshTodos(todoList, sessionInfo);
  });

  todoForm?.addEventListener("submit", async event => {
    event.preventDefault();
    const formData = new FormData(todoForm);
    const title = String(formData.get("title") || "").trim();
    if (!title) {
      return;
    }

    const response = await apiFetch("/api/todos", {
      method: "POST",
      body: JSON.stringify({ title })
    });
    if (!response.ok) {
      todoMessage.textContent = await readApiError(response);
      return;
    }

    todoMessage.textContent = "Todo added.";
    todoForm.reset();
    await refreshTodos(todoList, sessionInfo);
  });

  await refreshTodos(todoList, sessionInfo);
}

async function initHeadlessPage() {
  const requestInfo = document.getElementById("headless-request-info");
  const message = document.getElementById("headless-message");
  const requestId = new URLSearchParams(window.location.search).get("request");

  document.getElementById("headless-start-login")?.addEventListener("click", async () => {
    await startAuthorization("login");
  });

  document.getElementById("headless-start-signup")?.addEventListener("click", async () => {
    await startAuthorization("signup");
  });

  if (!requestId) {
    requestInfo.textContent = "No headless authorization request is active yet. Use one of the start buttons above after enabling headless mode in config.";
    return;
  }

  const requestResponse = await fetch(`/sqlos/auth/headless/requests/${encodeURIComponent(requestId)}`);
  const requestModel = await requestResponse.json();
  if (!requestResponse.ok) {
    requestInfo.textContent = JSON.stringify(requestModel, null, 2);
    throw new Error(requestModel.message || "Unable to load the headless request.");
  }

  requestInfo.textContent = JSON.stringify(
    {
      clientId: requestModel.clientId,
      clientName: requestModel.clientName,
      authBasePath: requestModel.authBasePath,
      headlessApiBasePath: requestModel.headlessApiBasePath,
      view: requestModel.view,
      email: requestModel.email,
      error: requestModel.error
    },
    null,
    2
  );

  const loginForm = document.getElementById("headless-login-form");
  loginForm?.addEventListener("submit", async event => {
    event.preventDefault();
    const formData = new FormData(loginForm);
    const result = await postJson("/sqlos/auth/headless/password/login", {
      requestId,
      email: String(formData.get("email") || ""),
      password: String(formData.get("password") || "")
    });
    await applyHeadlessResult(result, message);
  });

  const signupForm = document.getElementById("headless-signup-form");
  signupForm?.addEventListener("submit", async event => {
    event.preventDefault();
    const formData = new FormData(signupForm);
    const result = await postJson("/sqlos/auth/headless/signup", {
      requestId,
      displayName: String(formData.get("displayName") || ""),
      email: String(formData.get("email") || ""),
      password: String(formData.get("password") || ""),
      organizationName: String(formData.get("organizationName") || "")
    });
    await applyHeadlessResult(result, message);
  });
}

async function applyHeadlessResult(result, messageElement) {
  if (result.type === "redirect" && result.redirectUrl) {
    window.location.assign(result.redirectUrl);
    return;
  }

  if (result.viewModel) {
    messageElement.textContent = result.viewModel.error || result.viewModel.info || "Headless step completed.";
    return;
  }

  messageElement.textContent = "Unexpected headless response.";
}

async function refreshTodos(todoListElement, sessionInfoElement) {
  const meResponse = await apiFetch("/api/me");
  const todosResponse = await apiFetch("/api/todos");
  const mePayload = await meResponse.json();
  const todosPayload = await todosResponse.json();

  if (!meResponse.ok || !todosResponse.ok) {
    sessionInfoElement.textContent = JSON.stringify(
      {
        me: mePayload,
        todos: todosPayload
      },
      null,
      2
    );
    todoListElement.innerHTML = `<div class="empty-state">${todosPayload.error_description || todosPayload.error || "The API rejected this token."}</div>`;
    return;
  }

  sessionInfoElement.textContent = JSON.stringify(
    {
      userId: mePayload.userId,
      clientId: mePayload.clientId,
      audience: mePayload.audience,
      resource: todosPayload.resource
    },
    null,
    2
  );

  const items = todosPayload.items || [];
  if (!items.length) {
    todoListElement.innerHTML = `<div class="empty-state">No todos yet. Add one from the form above.</div>`;
    return;
  }

  todoListElement.innerHTML = items.map(item => `
    <article class="todo-item">
      <div>
        <strong>${escapeHtml(item.title)}</strong>
        <p>${item.isCompleted ? "Completed" : "Pending"} · Created ${new Date(item.createdAt).toLocaleString()}</p>
      </div>
      <div class="todo-actions">
        <button data-action="toggle" data-id="${item.id}" class="secondary" type="button">${item.isCompleted ? "Mark open" : "Complete"}</button>
        <button data-action="delete" data-id="${item.id}" class="secondary" type="button">Delete</button>
      </div>
    </article>
  `).join("");

  todoListElement.querySelectorAll("button[data-action='toggle']").forEach(button => {
    button.addEventListener("click", async () => {
      await apiFetch(`/api/todos/${button.dataset.id}/toggle`, { method: "POST" });
      await refreshTodos(todoListElement, sessionInfoElement);
    });
  });

  todoListElement.querySelectorAll("button[data-action='delete']").forEach(button => {
    button.addEventListener("click", async () => {
      await apiFetch(`/api/todos/${button.dataset.id}`, { method: "DELETE" });
      await refreshTodos(todoListElement, sessionInfoElement);
    });
  });
}

async function startAuthorization(view) {
  const config = await getSampleConfig();
  const codeVerifier = generateRandomString();
  const state = generateRandomString();
  const redirectUri = config.hostedClient.redirectUri;
  const challenge = await createCodeChallenge(codeVerifier);

  sessionStorage.setItem(
    oauthStorageKey,
    JSON.stringify({
      clientId: config.hostedClient.clientId,
      redirectUri,
      codeVerifier,
      resource: config.resource,
      state
    })
  );

  const params = new URLSearchParams({
    response_type: "code",
    client_id: config.hostedClient.clientId,
    redirect_uri: redirectUri,
    state,
    scope: (config.allowedScopes || []).join(" "),
    code_challenge: challenge,
    code_challenge_method: "S256",
    resource: config.resource,
    view
  });

  window.location.assign(`/sqlos/auth/authorize?${params.toString()}`);
}

async function getSampleConfig() {
  const response = await fetch("/sample/config");
  if (!response.ok) {
    throw new Error("Unable to load the sample configuration.");
  }

  return await response.json();
}

async function apiFetch(path, options = {}) {
  const tokens = readTokens();
  const headers = new Headers(options.headers || {});
  headers.set("Accept", "application/json");
  if (tokens?.access_token) {
    headers.set("Authorization", `Bearer ${tokens.access_token}`);
  }
  if (options.body && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }

  return await fetch(path, {
    ...options,
    headers
  });
}

async function postJson(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Accept": "application/json"
    },
    body: JSON.stringify(body)
  });
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.message || payload.error || "The headless request failed.");
  }

  return payload;
}

async function readApiError(response) {
  try {
    const payload = await response.json();
    return payload.error_description || payload.error || payload.message || "The request failed.";
  } catch {
    return "The request failed.";
  }
}

async function createCodeChallenge(verifier) {
  const data = new TextEncoder().encode(verifier);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return base64UrlEncode(new Uint8Array(digest));
}

function generateRandomString() {
  const buffer = new Uint8Array(32);
  crypto.getRandomValues(buffer);
  return base64UrlEncode(buffer);
}

function base64UrlEncode(bytes) {
  const base64 = btoa(String.fromCharCode(...bytes));
  return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function readOAuthState() {
  const raw = sessionStorage.getItem(oauthStorageKey);
  return raw ? JSON.parse(raw) : null;
}

function writeTokens(tokens) {
  localStorage.setItem(tokenStorageKey, JSON.stringify(tokens));
}

function readTokens() {
  const raw = localStorage.getItem(tokenStorageKey);
  return raw ? JSON.parse(raw) : null;
}

function parseJwt(token) {
  try {
    const [, payload] = token.split(".");
    return JSON.parse(atob(payload.replace(/-/g, "+").replace(/_/g, "/")));
  } catch {
    return null;
  }
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}
