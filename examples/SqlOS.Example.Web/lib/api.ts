export const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export type AuthOverride = {
  type: "agent" | "service_account";
  header: string;
  value: string;
  displayName: string;
};

const AUTH_OVERRIDE_KEY = "demo_auth_override";

export function setAuthOverride(override: AuthOverride | null) {
  if (typeof window === "undefined") return;
  if (override) {
    sessionStorage.setItem(AUTH_OVERRIDE_KEY, JSON.stringify(override));
  } else {
    sessionStorage.removeItem(AUTH_OVERRIDE_KEY);
  }
}

export function getAuthOverride(): AuthOverride | null {
  if (typeof window === "undefined") return null;
  const stored = sessionStorage.getItem(AUTH_OVERRIDE_KEY);
  if (!stored) return null;
  try {
    return JSON.parse(stored) as AuthOverride;
  } catch {
    return null;
  }
}

function buildAuthHeaders(accessToken: string): Record<string, string> {
  const override = getAuthOverride();
  if (override) {
    return { [override.header]: override.value };
  }
  return { Authorization: `Bearer ${accessToken}` };
}

export async function apiFetch(path: string, accessToken: string, init?: RequestInit) {
  const response = await fetch(`${apiUrl}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...buildAuthHeaders(accessToken),
      ...init?.headers,
    },
    cache: "no-store",
  });
  return response;
}

export async function apiGet<T = unknown>(path: string, accessToken: string): Promise<T> {
  const response = await apiFetch(path, accessToken);
  if (!response.ok) {
    throw new Error(`GET ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiPost<T = unknown>(path: string, accessToken: string, body: unknown): Promise<T> {
  const response = await apiFetch(path, accessToken, {
    method: "POST",
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`POST ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiPut<T = unknown>(path: string, accessToken: string, body: unknown): Promise<T> {
  const response = await apiFetch(path, accessToken, {
    method: "PUT",
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`PUT ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiDelete(path: string, accessToken: string): Promise<void> {
  const response = await apiFetch(path, accessToken, { method: "DELETE" });
  if (!response.ok) {
    throw new Error(`DELETE ${path} failed with ${response.status}`);
  }
}
