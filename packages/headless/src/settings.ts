import { HEADLESS_CREDENTIAL_RUNTIME_FLAGS, type HeadlessCredentialType } from "./contract.js";
import type { HeadlessSettings } from "./types.js";

/**
 * Whether a credential type is both enabled on the AuthPage settings and
 * actually configured at runtime. This is the same rule hosted AuthPage uses
 * to decide which sign-in methods to render, so custom UIs do not have to
 * copy it.
 */
export function credentialEnabled(
  settings: HeadlessSettings | null | undefined,
  type: HeadlessCredentialType,
): boolean {
  if (!settings) {
    return false;
  }
  const runtimeFlag = HEADLESS_CREDENTIAL_RUNTIME_FLAGS[type];
  if (!settings[runtimeFlag]) {
    return false;
  }
  return (settings.enabledCredentialTypes ?? []).some(
    (value) => value.toLowerCase() === type,
  );
}
