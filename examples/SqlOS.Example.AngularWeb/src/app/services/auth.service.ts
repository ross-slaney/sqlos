import { Injectable, signal, computed, inject } from '@angular/core';
import { Router } from '@angular/router';
import { jwtDecode } from 'jwt-decode';
import { environment } from '../environments/environment';
import { AuthOverride, DecodedToken, SessionData } from '../models';

const SESSION_KEY = 'sqlos_angular_session';
const AUTH_OVERRIDE_KEY = 'demo_auth_override';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private router = inject(Router);
  private sessionSignal = signal<SessionData | null>(this.loadSession());

  readonly session = this.sessionSignal.asReadonly();
  readonly isAuthenticated = computed(() => !!this.sessionSignal()?.accessToken);
  readonly accessToken = computed(() => this.sessionSignal()?.accessToken ?? null);
  readonly user = computed(() => {
    const s = this.sessionSignal();
    if (!s) return null;
    return { id: s.userId, email: s.email, name: s.displayName };
  });

  private refreshPromise: Promise<void> | null = null;

  private loadSession(): SessionData | null {
    try {
      const stored = sessionStorage.getItem(SESSION_KEY);
      if (!stored) return null;
      return JSON.parse(stored) as SessionData;
    } catch {
      return null;
    }
  }

  setSession(data: SessionData): void {
    sessionStorage.setItem(SESSION_KEY, JSON.stringify(data));
    this.sessionSignal.set(data);
  }

  clearSession(): void {
    sessionStorage.removeItem(SESSION_KEY);
    sessionStorage.removeItem(AUTH_OVERRIDE_KEY);
    this.sessionSignal.set(null);
  }

  async signOut(returnPath = '/'): Promise<void> {
    const session = this.sessionSignal();
    if (session) {
      try {
        await fetch(`${environment.apiUrl}/api/v1/auth/logout`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            refreshToken: session.refreshToken ?? null,
            sessionId: session.sessionId ?? null,
          }),
        });
      } catch { /* ignore */ }
    }

    this.clearSession();

    const logoutUrl = new URL('/sqlos/auth/logout', environment.apiUrl);
    logoutUrl.searchParams.set('returnTo', new URL(returnPath, window.location.origin).toString());
    window.location.assign(logoutUrl.toString());
  }

  async ensureValidToken(): Promise<string | null> {
    const session = this.sessionSignal();
    if (!session?.accessToken) return null;

    try {
      const decoded = jwtDecode<DecodedToken>(session.accessToken);
      const now = Math.floor(Date.now() / 1000);
      if (decoded.exp && now < decoded.exp) {
        return session.accessToken;
      }
    } catch { /* token is invalid, try refresh */ }

    if (this.refreshPromise) {
      await this.refreshPromise;
      return this.sessionSignal()?.accessToken ?? null;
    }

    this.refreshPromise = this.refreshAccessToken();
    try {
      await this.refreshPromise;
    } finally {
      this.refreshPromise = null;
    }
    return this.sessionSignal()?.accessToken ?? null;
  }

  private async refreshAccessToken(): Promise<void> {
    const session = this.sessionSignal();
    if (!session?.refreshToken) {
      this.clearSession();
      return;
    }

    try {
      const decoded = jwtDecode<DecodedToken>(session.accessToken);
      const usesHostedSqlOS = decoded?.iss?.includes('/sqlos/auth') ?? false;

      const response = usesHostedSqlOS
        ? await fetch(`${environment.apiUrl}/sqlos/auth/token`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
              grant_type: 'refresh_token',
              refresh_token: session.refreshToken,
            }),
          })
        : await fetch(`${environment.apiUrl}/api/v1/auth/refresh`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              refreshToken: session.refreshToken,
              organizationId: session.organizationId,
            }),
          });

      const data = await response.json();
      if (!response.ok) {
        console.warn('[Auth] Refresh failed.', data);
        await this.signOut('/');
        return;
      }

      const nextAccessToken = data.accessToken ?? data.access_token;
      const nextRefreshToken = data.refreshToken ?? data.refresh_token;
      if (!nextAccessToken || !nextRefreshToken) {
        throw new Error('Refresh response did not include new tokens.');
      }

      const refreshedDecoded = jwtDecode<DecodedToken>(nextAccessToken);
      this.setSession({
        ...session,
        accessToken: nextAccessToken,
        refreshToken: nextRefreshToken,
        sessionId: data.sessionId ?? refreshedDecoded.sid ?? session.sessionId,
        organizationId: data.organizationId ?? refreshedDecoded.org_id ?? session.organizationId ?? null,
        exp: refreshedDecoded.exp,
      });
    } catch (error) {
      console.error('[Auth] Refresh threw unexpectedly.', error);
      await this.signOut('/');
    }
  }

  // Auth override for demo agents/service accounts
  setAuthOverride(override: AuthOverride | null): void {
    if (override) {
      sessionStorage.setItem(AUTH_OVERRIDE_KEY, JSON.stringify(override));
    } else {
      sessionStorage.removeItem(AUTH_OVERRIDE_KEY);
    }
  }

  getAuthOverride(): AuthOverride | null {
    const stored = sessionStorage.getItem(AUTH_OVERRIDE_KEY);
    if (!stored) return null;
    try {
      return JSON.parse(stored) as AuthOverride;
    } catch {
      return null;
    }
  }

  buildAuthHeaders(): Record<string, string> {
    const override = this.getAuthOverride();
    if (override) {
      return { [override.header]: override.value };
    }
    const token = this.sessionSignal()?.accessToken;
    return token ? { Authorization: `Bearer ${token}` } : {};
  }
}
