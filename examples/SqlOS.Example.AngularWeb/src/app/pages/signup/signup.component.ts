import { Component, inject, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { SqlosAuthService } from '../../services/sqlos-auth.service';

@Component({
  selector: 'app-signup',
  standalone: true,
  template: `
    <div class="callback-page">
      <div class="callback-card">
        <h2>Redirecting to sign up...</h2>
        <p>Taking you to the SqlOS hosted auth page.</p>
        @if (error) {
          <p class="error">{{ error }}</p>
        }
      </div>
    </div>
  `,
})
export class SignupComponent implements OnInit {
  private sqlosAuth = inject(SqlosAuthService);
  private route = inject(ActivatedRoute);
  error: string | null = null;

  async ngOnInit() {
    try {
      const nextPath = this.route.snapshot.queryParamMap.get('next');
      const verifier = this.sqlosAuth.createOpaqueToken(48);
      const state = this.sqlosAuth.createOpaqueToken(24);
      const challenge = await this.sqlosAuth.createCodeChallenge(verifier);

      this.sqlosAuth.persistAuthFlow('signup', state, verifier, nextPath);

      const url = new URL(`${this.sqlosAuth.getAuthServerUrl()}/authorize`);
      url.searchParams.set('response_type', 'code');
      url.searchParams.set('client_id', this.sqlosAuth.getClientId());
      url.searchParams.set('redirect_uri', this.sqlosAuth.getRedirectUri());
      url.searchParams.set('state', state);
      url.searchParams.set('code_challenge', challenge);
      url.searchParams.set('code_challenge_method', 'S256');
      url.searchParams.set('view', 'signup');

      window.location.replace(url.toString());
    } catch (err) {
      this.error = err instanceof Error ? err.message : 'Failed to start the hosted SqlOS auth flow.';
    }
  }
}
