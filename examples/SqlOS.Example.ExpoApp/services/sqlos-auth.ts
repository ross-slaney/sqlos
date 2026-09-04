import * as AuthSession from "expo-auth-session";
import * as Crypto from "expo-crypto";
import { createPkceGenerator, type HeadlessAuthorization } from "@sqlos/headless";
import { API_URL, CLIENT_ID } from "./config";

export type HostedAuthView = "login" | "signup";

export type HostedTokenResult = {
  accessToken: string;
  refreshToken: string;
  idToken?: string;
  expiresIn?: number;
};

export function getAuthServerUrl(): string {
  return `${API_URL}/sqlos/auth`;
}

export function getClientId(): string {
  return CLIENT_ID;
}

export function getRedirectUri(): string {
  return AuthSession.makeRedirectUri({ path: "auth-callback" });
}

export async function fetchSqlOSDiscovery(): Promise<AuthSession.DiscoveryDocument> {
  return AuthSession.fetchDiscoveryAsync(getAuthServerUrl());
}

export function getNativeHeadlessRedirectUri(): string {
  return "sqlos-expo://auth-callback";
}

/**
 * Hermes does not ship Web Crypto `subtle`, so hand `@sqlos/headless` the
 * expo-crypto primitives. The package owns the verifier format (43-char
 * base64url) and the S256 challenge; nothing is re-implemented here.
 */
export const generateNativePkce = createPkceGenerator({
  randomBytes: (size) => Crypto.getRandomBytesAsync(size),
  sha256: async (data) =>
    new Uint8Array(await Crypto.digest(Crypto.CryptoDigestAlgorithm.SHA256, data as BufferSource)),
});

export async function exchangeHeadlessAuthorization(
  authorization: HeadlessAuthorization,
): Promise<HostedTokenResult> {
  if (!authorization.codeVerifier) {
    throw new Error("The headless flow did not produce a PKCE verifier.");
  }

  const tokens = await AuthSession.exchangeCodeAsync(
    {
      clientId: getClientId(),
      code: authorization.code,
      extraParams: { code_verifier: authorization.codeVerifier },
      redirectUri: authorization.redirectUri,
    },
    await fetchSqlOSDiscovery(),
  );

  if (!tokens.accessToken || !tokens.refreshToken) {
    throw new Error("Token exchange did not include access and refresh tokens.");
  }

  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    idToken: tokens.idToken,
    expiresIn: tokens.expiresIn,
  };
}

export async function startHostedAuth(view: HostedAuthView): Promise<HostedTokenResult> {
  const discovery = await fetchSqlOSDiscovery();
  const redirectUri = getRedirectUri();
  const request = new AuthSession.AuthRequest({
    clientId: getClientId(),
    scopes: ["openid", "profile", "email", "offline_access"],
    redirectUri,
    usePKCE: true,
    extraParams: view === "signup" ? { view: "signup" } : { prompt: "login" },
  });

  const result = await request.promptAsync(discovery);
  if (result.type !== "success" || !result.params.code) {
    throw new Error(result.type === "cancel" || result.type === "dismiss"
      ? "Sign-in was cancelled."
      : "The authorization response did not include a code.");
  }

  if (!request.codeVerifier) {
    throw new Error("expo-auth-session did not produce a PKCE verifier.");
  }

  const tokens = await AuthSession.exchangeCodeAsync(
    {
      clientId: getClientId(),
      code: result.params.code,
      extraParams: { code_verifier: request.codeVerifier },
      redirectUri,
    },
    discovery,
  );

  if (!tokens.accessToken || !tokens.refreshToken) {
    throw new Error("Token exchange did not include access and refresh tokens.");
  }

  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    idToken: tokens.idToken,
    expiresIn: tokens.expiresIn,
  };
}

export async function refreshHostedAuth(refreshToken: string): Promise<HostedTokenResult> {
  const tokens = await AuthSession.refreshAsync(
    {
      clientId: getClientId(),
      refreshToken,
    },
    await fetchSqlOSDiscovery(),
  );

  if (!tokens.accessToken || !tokens.refreshToken) {
    throw new Error("Refresh did not include access and refresh tokens.");
  }

  return {
    accessToken: tokens.accessToken,
    refreshToken: tokens.refreshToken,
    idToken: tokens.idToken,
    expiresIn: tokens.expiresIn,
  };
}
