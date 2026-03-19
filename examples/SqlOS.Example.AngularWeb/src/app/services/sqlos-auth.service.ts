import { Injectable } from '@angular/core';
import { environment } from '../environments/environment';

export type SqlOSAuthView = 'login' | 'signup';

const storageKeys = {
  verifier: 'sqlos_example_oauth_code_verifier',
  state: 'sqlos_example_oauth_state',
  view: 'sqlos_example_oauth_view',
  next: 'sqlos_example_oauth_next',
} as const;

function base64UrlEncode(bytes: Uint8Array): string {
  let binary = '';
  bytes.forEach((value) => {
    binary += String.fromCharCode(value);
  });
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

@Injectable({ providedIn: 'root' })
export class SqlosAuthService {
  createOpaqueToken(size = 32): string {
    const bytes = new Uint8Array(size);
    crypto.getRandomValues(bytes);
    return base64UrlEncode(bytes);
  }

  async createCodeChallenge(verifier: string): Promise<string> {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return base64UrlEncode(new Uint8Array(digest));
  }

  getApiUrl(): string {
    return environment.apiUrl;
  }

  getAuthServerUrl(): string {
    return `${this.getApiUrl()}/sqlos/auth`;
  }

  getClientId(): string {
    return environment.clientId;
  }

  getRedirectUri(): string {
    return `${window.location.origin}/auth/callback`;
  }

  normalizeNextPath(nextPath: string | null | undefined): string {
    if (!nextPath) return '/retail';
    const trimmed = nextPath.trim();
    if (!trimmed.startsWith('/') || trimmed.startsWith('//')) return '/retail';
    return trimmed;
  }

  persistAuthFlow(view: SqlOSAuthView, state: string, verifier: string, nextPath: string | null | undefined): void {
    sessionStorage.setItem(storageKeys.view, view);
    sessionStorage.setItem(storageKeys.state, state);
    sessionStorage.setItem(storageKeys.verifier, verifier);
    sessionStorage.setItem(storageKeys.next, this.normalizeNextPath(nextPath));
  }

  readAuthFlow(): { view: SqlOSAuthView; state: string | null; verifier: string | null; nextPath: string } {
    const view = sessionStorage.getItem(storageKeys.view) === 'signup' ? 'signup' : 'login';
    return {
      view,
      state: sessionStorage.getItem(storageKeys.state),
      verifier: sessionStorage.getItem(storageKeys.verifier),
      nextPath: this.normalizeNextPath(sessionStorage.getItem(storageKeys.next)),
    };
  }

  clearAuthFlow(): void {
    sessionStorage.removeItem(storageKeys.view);
    sessionStorage.removeItem(storageKeys.state);
    sessionStorage.removeItem(storageKeys.verifier);
    sessionStorage.removeItem(storageKeys.next);
  }
}
