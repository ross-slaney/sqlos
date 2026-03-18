"use client";

export type SqlOSAuthView = "login" | "signup";

export const sqlOsAuthStorageKeys = {
  verifier: "sqlos_example_oauth_code_verifier",
  state: "sqlos_example_oauth_state",
  view: "sqlos_example_oauth_view",
  next: "sqlos_example_oauth_next",
} as const;

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = "";
  bytes.forEach((value) => {
    binary += String.fromCharCode(value);
  });

  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

export function createOpaqueToken(size = 32): string {
  const bytes = new Uint8Array(size);
  crypto.getRandomValues(bytes);
  return base64UrlEncode(bytes);
}

export async function createCodeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64UrlEncode(new Uint8Array(digest));
}

export function getExampleApiUrl(): string {
  const configured = (process.env.NEXT_PUBLIC_API_URL ?? "").trim().replace(/\/$/, "");
  return configured || "http://localhost:5062";
}

export function getExampleAuthServerUrl(): string {
  return `${getExampleApiUrl()}/sqlos/auth`;
}

export function getExampleClientId(): string {
  const configured = (process.env.NEXT_PUBLIC_SQL_OS_CLIENT_ID ?? "").trim();
  return configured || "example-web";
}

export function getExampleRedirectUri(): string {
  return `${window.location.origin}/auth/callback`;
}

export function normalizeNextPath(nextPath: string | null | undefined): string {
  if (!nextPath) {
    return "/retail";
  }

  const trimmed = nextPath.trim();
  if (!trimmed.startsWith("/") || trimmed.startsWith("//")) {
    return "/app";
  }

  return trimmed;
}

export function persistSqlOSAuthFlow(
  view: SqlOSAuthView,
  state: string,
  verifier: string,
  nextPath: string | null | undefined,
): void {
  sessionStorage.setItem(sqlOsAuthStorageKeys.view, view);
  sessionStorage.setItem(sqlOsAuthStorageKeys.state, state);
  sessionStorage.setItem(sqlOsAuthStorageKeys.verifier, verifier);
  sessionStorage.setItem(sqlOsAuthStorageKeys.next, normalizeNextPath(nextPath));
}

export function readSqlOSAuthFlow(): {
  view: SqlOSAuthView;
  state: string | null;
  verifier: string | null;
  nextPath: string;
} {
  const view = sessionStorage.getItem(sqlOsAuthStorageKeys.view) === "signup" ? "signup" : "login";
  return {
    view,
    state: sessionStorage.getItem(sqlOsAuthStorageKeys.state),
    verifier: sessionStorage.getItem(sqlOsAuthStorageKeys.verifier),
    nextPath: normalizeNextPath(sessionStorage.getItem(sqlOsAuthStorageKeys.next)),
  };
}

export function clearSqlOSAuthFlow(): void {
  sessionStorage.removeItem(sqlOsAuthStorageKeys.view);
  sessionStorage.removeItem(sqlOsAuthStorageKeys.state);
  sessionStorage.removeItem(sqlOsAuthStorageKeys.verifier);
  sessionStorage.removeItem(sqlOsAuthStorageKeys.next);
}
