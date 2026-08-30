import { Component, inject, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  template: `
    <div class="callback-page">
      <div class="callback-card">
        <h2>Redirecting to sign in...</h2>
        <p>angular-oauth2-oidc is starting the standard OpenID Connect authorization-code flow.</p>
        @if (error) {
          <p class="error">{{ error }}</p>
        }
      </div>
    </div>
  `,
})
export class LoginComponent implements OnInit {
  private auth = inject(AuthService);
  private route = inject(ActivatedRoute);
  error: string | null = null;

  ngOnInit() {
    try {
      this.auth.startHostedSignIn('login', this.route.snapshot.queryParamMap.get('next'));
    } catch (err) {
      this.error = err instanceof Error ? err.message : 'Failed to start the hosted SqlOS auth flow.';
    }
  }
}
