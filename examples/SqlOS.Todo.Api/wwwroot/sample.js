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
        emailOtpEnabled: config.emailOtpEnabled,
        phoneOtpEnabled: config.phoneOtpEnabled,
        localClient: config.localClient,
        portableClient: config.portableClient,
        cimdEnabled: config.cimdEnabled,
        dcrEnabled: config.dcrEnabled
      },
      null,
      2
    );
  }

}

async function handleCallbackPage() {
  const status = document.getElementById("callback-status");
  const debug = document.getElementById("callback-debug");
  if (status) {
    status.textContent =
      "This vanilla page no longer exchanges authorization codes. Use the ASP.NET Core client at http://localhost:5090 or the Next.js Auth.js example at http://localhost:3010.";
  }
  if (debug) {
    debug.textContent = "Hosted JS login is owned by a stack-standard OIDC library, not sample.js.";
  }
}

async function initAppPage() {
  const sessionInfo = document.getElementById("session-info");
  const todoList = document.getElementById("todo-list");
  const todoForm = document.getElementById("todo-form");
  const todoMessage = document.getElementById("todo-message");
  const tokens = readTokens();

  if (!tokens?.access_token) {
    sessionInfo.textContent = "No local access token was found. Use the ASP.NET Core or Next.js hosted client.";
    todoList.innerHTML = `<div class="empty-state">Sign in from the ASP.NET Core client at http://localhost:5090 or the Next.js example at http://localhost:3010.</div>`;
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
      subjectId: mePayload.subjectId,
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

async function readApiError(response) {
  try {
    const payload = await response.json();
    return payload.error_description || payload.error || payload.message || "The request failed.";
  } catch {
    return "The request failed.";
  }
}

function readTokens() {
  const raw = localStorage.getItem(tokenStorageKey);
  return raw ? JSON.parse(raw) : null;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}
