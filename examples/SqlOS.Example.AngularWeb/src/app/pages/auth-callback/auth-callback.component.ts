import { Component, inject, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { jwtDecode } from 'jwt-decode';
import { SqlosAuthService } from '../../services/sqlos-auth.service';
import { AuthService } from '../../services/auth.service';
import { DecodedToken, TokenResponse } from '../../models';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `
    <div class="callback-page">
      <div class="callback-card">
        <h2>Completing sign in...</h2>
        <p>{{ message }}</p>
      </div>
    </div>
  `,
})
export class AuthCallbackComponent implements OnInit {
  private sqlosAuth = inject(SqlosAuthService);
  private auth = inject(AuthService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  message = 'Completing the hosted SqlOS sign-in...';

  async ngOnInit() {
    try {
      const params = this.route.snapshot.queryParamMap;
      const code = params.get('code');
      const state = params.get('state');
      const error = params.get('error');
      const errorDescription = params.get('error_description');

      if (error) {
        throw new Error(errorDescription || error);
      }

      if (!code || !state) {
        throw new Error('The hosted auth callback is missing the required code or state.');
      }

      const flow = this.sqlosAuth.readAuthFlow();
      if (!flow.state || !flow.verifier) {
        throw new Error('The local PKCE login state is missing or expired.');
      }

      if (flow.state !== state) {
        throw new Error('OAuth state validation failed.');
      }

      const tokenResponse = await fetch(`${this.sqlosAuth.getAuthServerUrl()}/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'authorization_code',
          code,
          client_id: this.sqlosAuth.getClientId(),
          redirect_uri: this.sqlosAuth.getRedirectUri(),
          code_verifier: flow.verifier,
        }),
      });

      const tokenData = (await tokenResponse.json()) as TokenResponse;
      if (!tokenResponse.ok || !tokenData.access_token || !tokenData.refresh_token) {
        throw new Error(tokenData.error_description || tokenData.error || 'Failed to exchange the authorization code.');
      }

      const decoded = jwtDecode<DecodedToken>(tokenData.access_token);

      this.auth.setSession({
        accessToken: tokenData.access_token,
        refreshToken: tokenData.refresh_token,
        userId: decoded.sub ?? '',
        email: decoded.email ?? `${decoded.sub ?? 'user'}@example.local`,
        displayName: decoded.name ?? decoded.email ?? decoded.sub ?? 'SqlOS user',
        organizationId: decoded.org_id ?? null,
        sessionId: decoded.sid ?? '',
        exp: decoded.exp,
      });

      this.sqlosAuth.clearAuthFlow();
      this.router.navigateByUrl(flow.nextPath);
    } catch (error) {
      this.sqlosAuth.clearAuthFlow();
      this.message = error instanceof Error ? error.message : 'Hosted SqlOS sign-in failed.';
    }
  }
}
