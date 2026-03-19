import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { jwtDecode } from 'jwt-decode';
import { SqlosAuthService } from '../../services/sqlos-auth.service';
import { SqlosHeadlessService } from '../../services/sqlos-headless.service';
import { AuthService } from '../../services/auth.service';
import { HeadlessViewModel, HeadlessActionResult, DecodedToken } from '../../models';

interface ReferralOption { value: string; label: string; }

const referralOptions: ReferralOption[] = [
  { value: 'docs', label: 'SqlOS docs or examples' },
  { value: 'emcy', label: 'Emcy or MCP integration work' },
  { value: 'friend', label: 'Recommendation from a teammate' },
  { value: 'review', label: 'Build vs. buy auth evaluation' },
];

function buildDisplayName(firstName: string, lastName: string, fallbackEmail: string) {
  const combined = `${firstName} ${lastName}`.trim();
  return combined || fallbackEmail.trim() || 'Example User';
}

const IMAGE_LOGIN = 'https://images.unsplash.com/photo-1604719312566-8912e9227c6a?w=1200&q=80&auto=format';
const IMAGE_SIGNUP = 'https://images.unsplash.com/photo-1556740758-90de374c12ad?w=1200&q=80&auto=format';

@Component({
  selector: 'app-auth-authorize',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="ha">
      <!-- Left: image + branding -->
      <div class="ha-left" [style.backgroundImage]="'url(' + (isSignup() ? IMAGE_SIGNUP : IMAGE_LOGIN) + ')'">
        <div class="ha-left-overlay"></div>
        <div class="ha-left-content">
          <a routerLink="/" class="ha-brand">
            <div class="ha-brand-icon">N</div>
            <span>Northwind Retail</span>
          </a>
          <div class="ha-left-bottom">
            <blockquote class="ha-quote">
              &ldquo;{{ testimonialQuote() }}&rdquo;
            </blockquote>
            <div class="ha-quote-author">
              <strong>{{ testimonialName() }}</strong>
              <span>{{ testimonialRole() }}</span>
            </div>
            <div class="ha-badge-row">
              <span class="ha-tech-badge">Headless Auth</span>
              <span class="ha-tech-badge">OAuth 2.0 + PKCE</span>
              <span class="ha-tech-badge">SqlOS</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Right: form -->
      <div class="ha-right">
        <div class="ha-form-wrapper">
          <div class="ha-form-header">
            <h1>{{ headline() }}</h1>
            <p>{{ subtitle() }}</p>
          </div>

          @if (error()) {
            <div class="ha-error">{{ error() }}</div>
          }

          @if (!requestId) {
            <!-- Flow starter -->
            <div class="ha-form">
              <p class="muted" style="font-size: 13px; line-height: 1.6; margin-bottom: 8px">
                This page demonstrates <strong>headless auth</strong> — your app owns the UI while SqlOS handles the OAuth protocol underneath.
              </p>
              @if (starterError()) {
                <div class="ha-error">{{ starterError() }}</div>
              }
              <button class="ha-submit" (click)="startFlow(initialIsSignup ? 'signup' : 'login')" [disabled]="flowStarting()">
                {{ flowStarting() && starterView() === (initialIsSignup ? 'signup' : 'login') ? 'Redirecting...' : (initialIsSignup ? 'Start signup flow' : 'Start sign in flow') }}
              </button>
              <button class="ha-provider-btn" (click)="startFlow(initialIsSignup ? 'login' : 'signup')" [disabled]="flowStarting()">
                {{ initialIsSignup ? 'Or sign in instead' : 'Or create an account' }}
              </button>
            </div>
          } @else {
            <!-- Identify / Login view -->
            @if (view() === 'login' || view() === 'identify') {
              <form class="ha-form" (ngSubmit)="onIdentify()">
                <div class="ha-field">
                  <label for="ha-email">Email address</label>
                  <input id="ha-email" type="email" [(ngModel)]="email" name="email" placeholder="you&#64;company.com" required autofocus>
                  @if (fieldErrors()['email']) {
                    <p class="ha-field-error">{{ fieldErrors()['email'] }}</p>
                  }
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Checking...' : 'Continue' }}
                </button>
                <div class="ha-alt">
                  Don't have an account?
                  <button type="button" class="ha-link-btn" (click)="view.set('signup')">Sign up</button>
                </div>
              </form>
            }

            <!-- Password view -->
            @if (view() === 'password') {
              <form class="ha-form" (ngSubmit)="onLogin()">
                <div class="ha-field">
                  <label for="ha-pw-email">Email</label>
                  <input id="ha-pw-email" type="email" [(ngModel)]="email" name="email" required>
                </div>
                <div class="ha-field">
                  <label for="ha-pw">Password</label>
                  <input id="ha-pw" type="password" [(ngModel)]="password" name="password" placeholder="Enter your password" required autofocus>
                  @if (fieldErrors()['password']) {
                    <p class="ha-field-error">{{ fieldErrors()['password'] }}</p>
                  }
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Signing in...' : 'Sign in' }}
                </button>
                <div class="ha-alt">
                  <button type="button" class="ha-link-btn" (click)="view.set('login')">Use a different email</button>
                </div>
              </form>
            }

            <!-- Signup view -->
            @if (view() === 'signup') {
              <form class="ha-form" (ngSubmit)="onSignup()">
                <div class="ha-row">
                  <div class="ha-field">
                    <label for="ha-fn">First name</label>
                    <input id="ha-fn" type="text" [(ngModel)]="firstName" name="firstName" placeholder="Taylor" required>
                    @if (fieldErrors()['firstName']) {
                      <p class="ha-field-error">{{ fieldErrors()['firstName'] }}</p>
                    }
                  </div>
                  <div class="ha-field">
                    <label for="ha-ln">Last name</label>
                    <input id="ha-ln" type="text" [(ngModel)]="lastName" name="lastName" placeholder="Morgan" required>
                    @if (fieldErrors()['lastName']) {
                      <p class="ha-field-error">{{ fieldErrors()['lastName'] }}</p>
                    }
                  </div>
                </div>
                <div class="ha-field">
                  <label for="ha-org">Organization</label>
                  <input id="ha-org" type="text" [(ngModel)]="organizationName" name="organizationName" placeholder="Your company name" required>
                  @if (fieldErrors()['organizationName']) {
                    <p class="ha-field-error">{{ fieldErrors()['organizationName'] }}</p>
                  }
                </div>
                <div class="ha-field">
                  <label for="ha-su-email">Email</label>
                  <input id="ha-su-email" type="email" [(ngModel)]="email" name="email" placeholder="taylor&#64;company.com" required>
                  @if (fieldErrors()['email']) {
                    <p class="ha-field-error">{{ fieldErrors()['email'] }}</p>
                  }
                </div>
                <div class="ha-field">
                  <label for="ha-su-pw">Password</label>
                  <input id="ha-su-pw" type="password" [(ngModel)]="password" name="password" placeholder="Min. 8 characters" required>
                  @if (fieldErrors()['password']) {
                    <p class="ha-field-error">{{ fieldErrors()['password'] }}</p>
                  }
                </div>
                <div class="ha-field">
                  <label for="ha-ref">How did you hear about us?</label>
                  <select id="ha-ref" [(ngModel)]="referralSource" name="referralSource" required>
                    <option value="">Select one</option>
                    @for (o of referralOptions; track o.value) {
                      <option [value]="o.value">{{ o.label }}</option>
                    }
                  </select>
                  @if (fieldErrors()['referralSource']) {
                    <p class="ha-field-error">{{ fieldErrors()['referralSource'] }}</p>
                  }
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Creating account...' : 'Create account' }}
                </button>
                <div class="ha-alt">
                  Already have an account?
                  <button type="button" class="ha-link-btn" (click)="view.set('login')">Sign in</button>
                </div>
              </form>
            }

            <!-- Organization selection -->
            @if (view() === 'organization') {
              <div class="ha-form">
                <div class="ha-org-list">
                  @for (org of viewModel()?.organizationSelection ?? []; track org.id) {
                    <button type="button" class="ha-org-btn" [disabled]="loading()" (click)="onSelectOrganization(org.id)">
                      <div class="ha-org-btn-icon">{{ org.name.charAt(0).toUpperCase() }}</div>
                      <div>
                        <strong>{{ org.name }}</strong>
                        <span>{{ org.role }}</span>
                      </div>
                    </button>
                  }
                </div>
              </div>
            }

            <!-- Provider buttons -->
            @if (showProviderButtons()) {
              <div class="ha-providers">
                <div class="ha-divider"><span>or</span></div>
                @for (provider of viewModel()?.providers ?? []; track provider.connectionId) {
                  <button type="button" class="ha-provider-btn" [disabled]="loading()" (click)="onProviderStart(provider.connectionId)">
                    Continue with {{ provider.displayName }}
                  </button>
                }
              </div>
            }
          }

          <div class="ha-footer">
            <a routerLink="/">← Back to Northwind Retail</a>
          </div>
        </div>
      </div>
    </div>
  `,
})
export class AuthAuthorizeComponent implements OnInit {
  private sqlosAuth = inject(SqlosAuthService);
  private headless = inject(SqlosHeadlessService);
  private authService = inject(AuthService);
  private route = inject(ActivatedRoute);

  IMAGE_LOGIN = IMAGE_LOGIN;
  IMAGE_SIGNUP = IMAGE_SIGNUP;
  referralOptions = referralOptions;

  requestId: string | null = null;
  initialIsSignup = false;

  view = signal('login');
  loading = signal(false);
  error = signal<string | null>(null);
  fieldErrors = signal<Record<string, string>>({});
  viewModel = signal<HeadlessViewModel | null>(null);

  email = '';
  password = '';
  organizationName = '';
  firstName = '';
  lastName = '';
  referralSource = '';

  // Flow starter state
  flowStarting = signal(false);
  starterError = signal<string | null>(null);
  starterView = signal<'login' | 'signup'>('login');

  isSignup = () => this.view() === 'signup';

  headline = () => {
    if (this.isSignup()) return 'Start your free trial';
    if (this.view() === 'organization') return 'Choose workspace';
    return 'Welcome back';
  };

  subtitle = () => {
    if (this.isSignup()) return 'Create your account and start managing retail operations in minutes.';
    if (this.view() === 'organization') return "Select the organization you'd like to sign in to.";
    return 'Sign in to your Northwind Retail account.';
  };

  testimonialQuote = () => this.isSignup()
    ? 'Setting up took less than five minutes. We had our entire team onboarded before lunch.'
    : "I love that I can see exactly my stores. No noise, no clutter — just the data I need.";

  testimonialName = () => this.isSignup() ? 'Marcus Rivera' : 'Priya Sharma';
  testimonialRole = () => this.isSignup() ? 'Head of Retail Ops, FreshMart' : 'Store Manager, Target #100';

  showProviderButtons = () => {
    const v = this.view();
    return (v === 'login' || v === 'identify' || v === 'signup') && (this.viewModel()?.providers?.length ?? 0) > 0;
  };

  async ngOnInit() {
    const params = this.route.snapshot.queryParamMap;
    this.requestId = params.get('request');
    const initialView = params.get('view') || 'login';
    this.initialIsSignup = initialView === 'signup';
    this.view.set(initialView);
    this.error.set(params.get('error'));
    this.email = params.get('email') || '';

    const pendingToken = params.get('pendingToken');
    const initialDisplayName = params.get('displayName') || '';
    const nextPath = params.get('next') || '/retail';

    if (!this.requestId) return;

    try {
      const vm = await this.headless.getHeadlessRequest(
        this.requestId,
        initialView,
        params.get('error'),
        pendingToken,
        this.email || null,
        initialDisplayName || null,
      );
      this.viewModel.set(vm);
      if (vm.view) this.view.set(vm.view);
      if (vm.error) this.error.set(vm.error);
      if (vm.email) this.email = vm.email;
      if (vm.displayName && !this.firstName && !this.lastName && initialDisplayName) {
        const [first = '', ...rest] = vm.displayName.split(' ');
        this.firstName = first;
        this.lastName = rest.join(' ');
      }
      if (vm.fieldErrors) this.fieldErrors.set(vm.fieldErrors);
    } catch (err) {
      this.error.set(err instanceof Error ? err.message : 'Failed to load authorization request.');
    }
  }

  private async handleResult(result: HeadlessActionResult) {
    if (result.type === 'redirect' && result.redirectUrl) {
      const url = new URL(result.redirectUrl);
      const code = url.searchParams.get('code');

      if (code) {
        const flow = this.sqlosAuth.readAuthFlow();
        const tokenRes = await fetch(`${this.sqlosAuth.getAuthServerUrl()}/token`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            grant_type: 'authorization_code',
            code,
            client_id: this.sqlosAuth.getClientId(),
            redirect_uri: this.sqlosAuth.getRedirectUri(),
            code_verifier: flow.verifier || '',
          }),
        });

        const tokenData = await tokenRes.json();
        if (!tokenRes.ok || !tokenData.access_token) {
          this.error.set(tokenData.error_description || tokenData.error || 'Token exchange failed.');
          return;
        }

        const decoded = jwtDecode<DecodedToken>(tokenData.access_token);
        this.authService.setSession({
          accessToken: tokenData.access_token,
          refreshToken: tokenData.refresh_token,
          userId: decoded.sub ?? '',
          email: decoded.email ?? '',
          displayName: decoded.name ?? decoded.email ?? 'User',
          organizationId: decoded.org_id ?? null,
          sessionId: decoded.sid ?? '',
          exp: decoded.exp,
        });

        this.sqlosAuth.clearAuthFlow();
        window.location.replace(flow.nextPath || '/retail');
        return;
      }

      window.location.href = result.redirectUrl;
      return;
    }

    if (result.viewModel) {
      this.viewModel.set(result.viewModel);
      if (result.viewModel.view) this.view.set(result.viewModel.view);
      if (result.viewModel.error) this.error.set(result.viewModel.error);
      if (result.viewModel.email) this.email = result.viewModel.email;
      this.fieldErrors.set(result.viewModel.fieldErrors ?? {});
    }
  }

  async onIdentify() {
    if (!this.requestId) return;
    this.loading.set(true); this.error.set(null); this.fieldErrors.set({});
    try { await this.handleResult(await this.headless.identify(this.requestId, this.email)); }
    catch (err) { this.error.set(err instanceof Error ? err.message : 'We could not start sign in.'); }
    finally { this.loading.set(false); }
  }

  async onLogin() {
    if (!this.requestId) return;
    this.loading.set(true); this.error.set(null); this.fieldErrors.set({});
    try { await this.handleResult(await this.headless.passwordLogin(this.requestId, this.email, this.password)); }
    catch (err) { this.error.set(err instanceof Error ? err.message : 'Login failed.'); }
    finally { this.loading.set(false); }
  }

  async onSignup() {
    if (!this.requestId) return;
    this.loading.set(true); this.error.set(null); this.fieldErrors.set({});
    try {
      await this.handleResult(await this.headless.signup(
        this.requestId,
        buildDisplayName(this.firstName, this.lastName, this.email),
        this.email,
        this.password,
        this.organizationName,
        { referralSource: this.referralSource, firstName: this.firstName, lastName: this.lastName },
      ));
    } catch (err) { this.error.set(err instanceof Error ? err.message : 'Signup failed.'); }
    finally { this.loading.set(false); }
  }

  async onProviderStart(connectionId: string) {
    if (!this.requestId) return;
    this.loading.set(true); this.error.set(null);
    try { await this.handleResult(await this.headless.startProvider(this.requestId, connectionId, this.email || undefined)); }
    catch (err) { this.error.set(err instanceof Error ? err.message : 'Provider auth failed.'); }
    finally { this.loading.set(false); }
  }

  async onSelectOrganization(organizationId: string) {
    const activePendingToken = this.viewModel()?.pendingToken ?? this.route.snapshot.queryParamMap.get('pendingToken');
    if (!activePendingToken) return;
    this.loading.set(true); this.error.set(null);
    try { await this.handleResult(await this.headless.selectOrganization(activePendingToken, organizationId)); }
    catch (err) { this.error.set(err instanceof Error ? err.message : 'Organization selection failed.'); }
    finally { this.loading.set(false); }
  }

  async startFlow(flowView: 'login' | 'signup') {
    this.starterView.set(flowView);
    this.flowStarting.set(true);
    this.starterError.set(null);
    try {
      const nextPath = this.route.snapshot.queryParamMap.get('next') || '/retail';
      const verifier = this.sqlosAuth.createOpaqueToken(48);
      const state = this.sqlosAuth.createOpaqueToken(24);
      const challenge = await this.sqlosAuth.createCodeChallenge(verifier);
      this.sqlosAuth.persistAuthFlow(flowView, state, verifier, nextPath);

      const url = new URL(`${this.sqlosAuth.getAuthServerUrl()}/authorize`);
      url.searchParams.set('response_type', 'code');
      url.searchParams.set('client_id', this.sqlosAuth.getClientId());
      url.searchParams.set('redirect_uri', this.sqlosAuth.getRedirectUri());
      url.searchParams.set('state', state);
      url.searchParams.set('code_challenge', challenge);
      url.searchParams.set('code_challenge_method', 'S256');
      if (flowView === 'signup') url.searchParams.set('view', 'signup');

      window.location.replace(url.toString());
    } catch (err) {
      this.starterError.set(err instanceof Error ? err.message : 'Failed to start.');
      this.flowStarting.set(false);
    }
  }
}
