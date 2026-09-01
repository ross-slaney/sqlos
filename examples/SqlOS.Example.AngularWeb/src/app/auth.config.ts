import { AuthConfig } from 'angular-oauth2-oidc';
import { environment } from './environments/environment';

export const nextPathStorageKey = 'sqlos_example_next';

export function createSqlOSAuthConfig(): AuthConfig {
  return {
    issuer: `${environment.apiUrl}/sqlos/auth`,
    redirectUri: `${window.location.origin}/auth/callback`,
    clientId: environment.clientId,
    responseType: 'code',
    scope: 'openid profile email offline_access',
    requireHttps: false,
    oidc: true,
    showDebugInformation: false,
    // Issuer is {origin}/sqlos/auth, so discovery URLs share a path prefix.
    strictDiscoveryDocumentValidation: false,
    useSilentRefresh: false,
  };
}

export function persistNextPath(nextPath: string | null | undefined): void {
  const trimmed = nextPath?.trim() ?? '';
  const next = !trimmed || !trimmed.startsWith('/') || trimmed.startsWith('//')
    ? '/retail'
    : trimmed;
  sessionStorage.setItem(nextPathStorageKey, next);
}

export function consumeNextPath(): string {
  const next = sessionStorage.getItem(nextPathStorageKey) || '/retail';
  sessionStorage.removeItem(nextPathStorageKey);
  return next;
}
