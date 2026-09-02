import type { HeadlessPkcePair } from "./types.js";

const VERIFIER_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

function randomBytes(size: number): Uint8Array {
  const cryptoRef = globalThis.crypto;
  if (!cryptoRef?.getRandomValues) {
    throw new Error("Web Crypto getRandomValues is required to generate PKCE material.");
  }
  const bytes = new Uint8Array(size);
  cryptoRef.getRandomValues(bytes);
  return bytes;
}

export function toBase64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  const base64 = btoa(binary);
  return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

export function randomState(length = 32): string {
  const bytes = randomBytes(sizeForVerifier(length));
  return toBase64Url(bytes).slice(0, length);
}

function sizeForVerifier(length: number): number {
  return Math.max(32, Math.ceil((length * 3) / 4));
}

export async function generatePkce(): Promise<HeadlessPkcePair> {
  const bytes = randomBytes(32);
  let codeVerifier = "";
  for (const byte of bytes) {
    codeVerifier += VERIFIER_ALPHABET[byte % VERIFIER_ALPHABET.length];
  }
  const digest = await sha256(new TextEncoder().encode(codeVerifier));
  return {
    codeVerifier,
    codeChallenge: toBase64Url(digest),
    codeChallengeMethod: "S256",
  };
}

async function sha256(data: Uint8Array): Promise<Uint8Array> {
  const cryptoRef = globalThis.crypto;
  if (!cryptoRef?.subtle?.digest) {
    throw new Error(
      "Web Crypto subtle.digest is required to generate PKCE. Pass generatePkce when it is unavailable.",
    );
  }
  return new Uint8Array(await cryptoRef.subtle.digest("SHA-256", data as BufferSource));
}
