import * as SecureStore from "expo-secure-store";
import { jwtDecode } from "jwt-decode";
import { API_URL } from "./config";
import { clearPKCE } from "./sqlos-auth";
import type { SessionData, DecodedToken } from "./types";

const SESSION_KEY = "sqlos_session";
const OVERRIDE_KEY = "sqlos_auth_override";

export type AuthOverride = {
  type: "agent" | "service_account";
  header: string;
  value: string;
  displayName: string;
};

let cachedSession: SessionData | null = null;
let refreshPromise: Promise<void> | null = null;

export async function getSession(): Promise<SessionData | null> {
  if (cachedSession) return cachedSession;
  try {
    const stored = await SecureStore.getItemAsync(SESSION_KEY);
    if (!stored) return null;
    cachedSession = JSON.parse(stored) as SessionData;
    return cachedSession;
  } catch {
    return null;
  }
}

export async function setSession(data: SessionData): Promise<void> {
  cachedSession = data;
  await SecureStore.setItemAsync(SESSION_KEY, JSON.stringify(data));
}

export async function clearSession(): Promise<void> {
  cachedSession = null;
  refreshPromise = null;
  await SecureStore.deleteItemAsync(SESSION_KEY);
  await SecureStore.deleteItemAsync(OVERRIDE_KEY);
  await clearPKCE();
}

export async function isAuthenticated(): Promise<boolean> {
  const session = await getSession();
  return !!session?.accessToken;
}

export async function ensureValidToken(): Promise<string | null> {
  const session = await getSession();
  if (!session?.accessToken) return null;

  try {
    const decoded = jwtDecode<DecodedToken>(session.accessToken);
    const now = Math.floor(Date.now() / 1000);
    if (decoded.exp && now < decoded.exp) {
      return session.accessToken;
    }
  } catch {
    /* token invalid, try refresh */
  }

  if (refreshPromise) {
    await refreshPromise;
    const s = await getSession();
    return s?.accessToken ?? null;
  }

  refreshPromise = refreshAccessToken();
  try {
    await refreshPromise;
  } finally {
    refreshPromise = null;
  }
  const s = await getSession();
  return s?.accessToken ?? null;
}

async function refreshAccessToken(): Promise<void> {
  const session = await getSession();
  if (!session?.refreshToken) {
    await clearSession();
    return;
  }

  try {
    const decoded = jwtDecode<DecodedToken>(session.accessToken);
    const usesHostedSqlOS = decoded?.iss?.includes("/sqlos/auth") ?? false;

    const response = usesHostedSqlOS
      ? await fetch(`${API_URL}/sqlos/auth/token`, {
          method: "POST",
          headers: { "Content-Type": "application/x-www-form-urlencoded" },
          body: new URLSearchParams({
            grant_type: "refresh_token",
            refresh_token: session.refreshToken,
          }).toString(),
        })
      : await fetch(`${API_URL}/api/v1/auth/refresh`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            refreshToken: session.refreshToken,
            organizationId: session.organizationId,
          }),
        });

    const data = await response.json();
    if (!response.ok) {
      await clearSession();
      return;
    }

    const nextAccessToken = data.accessToken ?? data.access_token;
    const nextRefreshToken = data.refreshToken ?? data.refresh_token;
    if (!nextAccessToken || !nextRefreshToken) {
      throw new Error("Refresh response did not include new tokens.");
    }

    const refreshedDecoded = jwtDecode<DecodedToken>(nextAccessToken);
    await setSession({
      ...session,
      accessToken: nextAccessToken,
      refreshToken: nextRefreshToken,
      sessionId: data.sessionId ?? refreshedDecoded.sid ?? session.sessionId,
      organizationId:
        data.organizationId ??
        refreshedDecoded.org_id ??
        session.organizationId ??
        null,
      exp: refreshedDecoded.exp,
    });
  } catch {
    await clearSession();
  }
}

export async function signOut(): Promise<void> {
  await clearSession();
}

// Auth override for demo
export async function setAuthOverride(
  override: AuthOverride | null,
): Promise<void> {
  if (override) {
    await SecureStore.setItemAsync(OVERRIDE_KEY, JSON.stringify(override));
  } else {
    await SecureStore.deleteItemAsync(OVERRIDE_KEY);
  }
}

export async function getAuthOverride(): Promise<AuthOverride | null> {
  try {
    const stored = await SecureStore.getItemAsync(OVERRIDE_KEY);
    if (!stored) return null;
    return JSON.parse(stored) as AuthOverride;
  } catch {
    return null;
  }
}
