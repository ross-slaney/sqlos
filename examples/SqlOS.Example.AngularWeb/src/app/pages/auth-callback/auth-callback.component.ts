import { Component, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { consumeNextPath } from '../../auth.config';

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
  private auth = inject(AuthService);
  private router = inject(Router);

  message = 'Completing the hosted SqlOS sign-in...';

  async ngOnInit() {
    try {
      const params = new URLSearchParams(window.location.search);
      if (params.get('error')) {
        throw new Error(params.get('error_description') || params.get('error') || 'Sign-in failed.');
      }

      if (!this.auth.syncFromOidc()) {
        throw new Error('The OIDC library did not complete the code exchange.');
      }

      await this.router.navigateByUrl(consumeNextPath());
    } catch (error) {
      this.message = error instanceof Error ? error.message : 'Hosted SqlOS sign-in failed.';
    }
  }
}
