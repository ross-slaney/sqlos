import { API_URL } from "./config";
import { ensureValidToken, getAuthOverride } from "./auth";

async function apiFetch(
  path: string,
  init?: RequestInit,
): Promise<Response> {
  const token = await ensureValidToken();
  const override = await getAuthOverride();
  const authHeaders: Record<string, string> = override
    ? { [override.header]: override.value }
    : token
      ? { Authorization: `Bearer ${token}` }
      : {};

  return fetch(`${API_URL}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...authHeaders,
      ...init?.headers,
    },
  });
}

export async function apiGet<T = unknown>(path: string): Promise<T> {
  const response = await apiFetch(path);
  if (!response.ok) {
    throw new Error(`GET ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiPost<T = unknown>(
  path: string,
  body: unknown,
): Promise<T> {
  const response = await apiFetch(path, {
    method: "POST",
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`POST ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiPut<T = unknown>(
  path: string,
  body: unknown,
): Promise<T> {
  const response = await apiFetch(path, {
    method: "PUT",
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`PUT ${path} failed with ${response.status}`);
  }
  return response.json();
}

export async function apiDelete(path: string): Promise<void> {
  const response = await apiFetch(path, { method: "DELETE" });
  if (!response.ok) {
    throw new Error(`DELETE ${path} failed with ${response.status}`);
  }
}
