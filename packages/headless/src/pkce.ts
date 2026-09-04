import type { HeadlessPkcePair, HeadlessPkcePrimitives } from "./types.js";

/**
 * 32 random bytes → 43 base64url characters, the RFC 7636 minimum
 * `code_verifier` length. SqlOS rejects shorter verifiers at `/token`
 * before comparing the challenge, so never shrink this.
 */
const VERIFIER_BYTES = 32;

function defaultRandomBytes(size: number): Uint8Array {
  const cryptoRef = globalThis.crypto;
  if (!cryptoRef?.getRandomValues) {
    throw new Error(
      "Web Crypto getRandomValues is unavailable. Pass `randomBytes` (for example expo-crypto getRandomBytesAsync).",
    );
  }
  const bytes = new Uint8Array(size);
  cryptoRef.getRandomValues(bytes);
  return bytes;
}

async function defaultSha256(data: Uint8Array): Promise<Uint8Array> {
  const cryptoRef = globalThis.crypto;
  if (!cryptoRef?.subtle?.digest) {
    throw new Error(
      "Web Crypto subtle.digest is unavailable. Pass `sha256` (for example expo-crypto digest).",
    );
  }
  return new Uint8Array(await cryptoRef.subtle.digest("SHA-256", data as BufferSource));
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
  const bytes = defaultRandomBytes(Math.max(VERIFIER_BYTES, Math.ceil((length * 3) / 4)));
  return toBase64Url(bytes).slice(0, length);
}

/**
 * Generate an S256 PKCE pair. Web Crypto is used by default; pass
 * `randomBytes` / `sha256` where it is unavailable (React Native / Expo)
 * instead of re-implementing the verifier format.
 */
export async function generatePkce(primitives: HeadlessPkcePrimitives = {}): Promise<HeadlessPkcePair> {
  const randomBytes = primitives.randomBytes ?? defaultRandomBytes;
  const sha256 = primitives.sha256 ?? defaultSha256;

  const codeVerifier = toBase64Url(await randomBytes(VERIFIER_BYTES));
  const digest = await sha256(new TextEncoder().encode(codeVerifier));
  return {
    codeVerifier,
    codeChallenge: toBase64Url(digest),
    codeChallengeMethod: "S256",
  };
}

/** Bind primitives once so the result can be passed as `generatePkce`. */
export function createPkceGenerator(primitives: HeadlessPkcePrimitives): () => Promise<HeadlessPkcePair> {
  return () => generatePkce(primitives);
}
