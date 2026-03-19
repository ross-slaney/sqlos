import * as SecureStore from "expo-secure-store";
import * as ExpoCrypto from "expo-crypto";
import * as AuthSession from "expo-auth-session";
import { API_URL, CLIENT_ID } from "./config";

const KEYS = {
  verifier: "sqlos_pkce_verifier",
  state: "sqlos_pkce_state",
};

function base64UrlEncode(buffer: Uint8Array): string {
  const base64 = btoa(String.fromCharCode(...Array.from(buffer)));
  return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=/g, "");
}

export function generateCodeVerifier(): string {
  const randomBytes = ExpoCrypto.getRandomBytes(32);
  return base64UrlEncode(new Uint8Array(randomBytes));
}

export async function generateCodeChallenge(verifier: string): Promise<string> {
  const digest = await ExpoCrypto.digestStringAsync(
    ExpoCrypto.CryptoDigestAlgorithm.SHA256,
    verifier,
    { encoding: ExpoCrypto.CryptoEncoding.BASE64 },
  );
  return digest.replace(/\+/g, "-").replace(/\//g, "_").replace(/=/g, "");
}

export function generateState(): string {
  const randomBytes = ExpoCrypto.getRandomBytes(16);
  return base64UrlEncode(new Uint8Array(randomBytes));
}

export function getAuthServerUrl(): string {
  return `${API_URL}/sqlos/auth`;
}

export function getClientId(): string {
  return CLIENT_ID;
}

export function getRedirectUri(): string {
  return AuthSession.makeRedirectUri({ path: "auth-callback" });
}

export async function persistPKCE(
  state: string,
  verifier: string,
): Promise<void> {
  await SecureStore.setItemAsync(KEYS.state, state);
  await SecureStore.setItemAsync(KEYS.verifier, verifier);
}

export async function readPKCE(): Promise<{
  state: string | null;
  verifier: string | null;
}> {
  return {
    state: await SecureStore.getItemAsync(KEYS.state),
    verifier: await SecureStore.getItemAsync(KEYS.verifier),
  };
}

export async function clearPKCE(): Promise<void> {
  await SecureStore.deleteItemAsync(KEYS.state);
  await SecureStore.deleteItemAsync(KEYS.verifier);
}

export async function buildAuthorizeUrl(
  view: "login" | "signup",
  redirectUri: string,
  state: string,
  challenge: string,
): Promise<string> {
  const url = new URL(`${getAuthServerUrl()}/authorize`);
  url.searchParams.set("response_type", "code");
  url.searchParams.set("client_id", getClientId());
  url.searchParams.set("redirect_uri", redirectUri);
  url.searchParams.set("state", state);
  url.searchParams.set("code_challenge", challenge);
  url.searchParams.set("code_challenge_method", "S256");
  if (view === "signup") url.searchParams.set("view", "signup");
  return url.toString();
}
