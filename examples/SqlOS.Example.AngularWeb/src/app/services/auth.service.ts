import { Injectable, signal, computed, inject } from '@angular/core';
import { OAuthService } from 'angular-oauth2-oidc';
import { jwtDecode } from 'jwt-decode';
import { environment } from '../environments/environment';
import { persistNextPath } from '../auth.config';
import { AuthOverride, DecodedToken, SessionData } from '../models';

const SESSION_KEY = 'sqlos_angular_session';
const AUTH_OVERRIDE_KEY = 'demo_auth_override';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private oauth = inject(OAuthService);
  private sessionSignal = signal<SessionData | null>(this.loadSession());

  readonly session = this.sessionSignal.asReadonly();
  readonly isAuthenticated = computed(() => !!this.sessionSignal()?.accessToken || this.oauth.hasValidAccessToken());
  readonly accessToken = computed(() => this.sessionSignal()?.accessToken ?? this.oauth.getAccessToken() ?? null);
  readonly user = computed(() => {
    const s = this.sessionSignal();
    if (!s) return null;
    return { id: s.userId, email: s.email, name: s.displayName };
  });

  private refreshPromise: Promise<void> | null = null;

  private loadSession(): SessionData | null {
    try {
      const stored = sessionStorage.getItem(SESSION_KEY);
      if (stored) {
        return JSON.parse(stored) as SessionData;
      }
    } catch {
      /* ignore */
    }

    return this.sessionFromOidc();
  }

  sessionFromOidc(): SessionData | null {
    const accessToken = this.oauth.getAccessToken();
    const refreshToken = this.oauth.getRefreshToken();
    if (!accessToken || !refreshToken) {
      return null;
    }

    const decoded = jwtDecode<DecodedToken>(accessToken);
    const claims = this.oauth.getIdentityClaims() as { sub?: string; email?: string; name?: string } | null;
    return {
      accessToken,
      refreshToken,
      userId: claims?.sub ?? decoded.sub ?? '',
      email: claims?.email ?? decoded.email ?? '',
      displayName: claims?.name ?? decoded.name ?? decoded.email ?? decoded.sub ?? 'SqlOS user',
      organizationId: decoded.org_id ?? null,
      sessionId: decoded.sid ?? '',
      exp: decoded.exp,
      source: 'oidc',
    };
  }

  syncFromOidc(): boolean {
    const session = this.sessionFromOidc();
    if (!session) {
      return false;
    }
    this.setSession(session);
    return true;
  }

  startHostedSignIn(view: 'login' | 'signup', nextPath?: string | null): void {
    persistNextPath(nextPath);
    this.oauth.initCodeFlow(undefined, view === 'signup' ? { view: 'signup' } : { prompt: 'login' });
  }

  setSession(data: SessionData): void {
    sessionStorage.setItem(SESSION_KEY, JSON.stringify(data));
    this.sessionSignal.set(data);
  }

  clearSession(): void {
    sessionStorage.removeItem(SESSION_KEY);
    sessionStorage.removeItem(AUTH_OVERRIDE_KEY);
    this.oauth.logOut(true);
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
    if (this.oauth.hasValidAccessToken()) {
      const token = this.oauth.getAccessToken();
      this.syncFromOidc();
      return token;
    }

    const session = this.sessionSignal();
    if (!session?.accessToken && !session?.refreshToken && !this.oauth.getRefreshToken()) {
      return null;
    }

    if (this.refreshPromise) {
      await this.refreshPromise;
      return this.sessionSignal()?.accessToken ?? this.oauth.getAccessToken() ?? null;
    }

    this.refreshPromise = this.refreshAccessToken();
    try {
      await this.refreshPromise;
    } finally {
      this.refreshPromise = null;
    }
    return this.sessionSignal()?.accessToken ?? this.oauth.getAccessToken() ?? null;
  }

  private async refreshAccessToken(): Promise<void> {
    const session = this.sessionSignal();
    if (session?.source !== 'demo' && this.oauth.getRefreshToken()) {
      try {
        await this.oauth.refreshToken();
        this.syncFromOidc();
        return;
      } catch (error) {
        console.error('[Auth] OIDC refresh failed.', error);
        await this.signOut('/');
        return;
      }
    }

    if (!session?.refreshToken) {
      this.clearSession();
      return;
    }

    try {
      const response = await fetch(`${environment.apiUrl}/api/v1/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          refreshToken: session.refreshToken,
          organizationId: session.organizationId,
        }),
      });

      const data = await response.json();
      if (!response.ok) {
        console.warn('[Auth] Demo refresh failed.', data);
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
        source: 'demo',
      });
    } catch (error) {
      console.error('[Auth] Refresh threw unexpectedly.', error);
      await this.signOut('/');
    }
  }

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
    const token = this.sessionSignal()?.accessToken ?? this.oauth.getAccessToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  }
}
