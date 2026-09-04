import { Component, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { createHeadlessFlow, credentialEnabled, type HeadlessFlow, type HeadlessFlowStatus, type HeadlessView, type HeadlessViewModel } from '@sqlos/headless';
import { AuthService } from '../../services/auth.service';
import { environment } from '../../environments/environment';

interface ReferralOption { value: string; label: string; }

const referralOptions: ReferralOption[] = [
  { value: 'docs', label: 'SqlOS docs or examples' },
  { value: 'mcp', label: 'MCP integration work' },
  { value: 'friend', label: 'Recommendation from a teammate' },
  { value: 'review', label: 'Build vs. buy auth evaluation' },
];

function buildDisplayName(firstName: string, lastName: string, fallbackEmail: string) {
  const combined = `${firstName} ${lastName}`.trim();
  return combined || fallbackEmail.trim() || 'Example User';
}

function getProviderMonogram(displayName: string) {
  const parts = displayName
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2);

  if (parts.length === 0) {
    return '?';
  }

  return parts.map((part) => part.charAt(0).toUpperCase()).join('');
}

const IMAGE_LOGIN = 'https://images.unsplash.com/photo-1604719312566-8912e9227c6a?w=1200&q=80&auto=format';
const IMAGE_SIGNUP = 'https://images.unsplash.com/photo-1556740758-90de374c12ad?w=1200&q=80&auto=format';

/**
 * Views this component draws. Anything else SqlOS returns (phone OTP, magic
 * link, consent, invitations, device approval, ...) falls back to hosted
 * sign-in instead of showing a headline with no form.
 */
const HANDLED_VIEWS: ReadonlySet<string> = new Set<HeadlessView>([
  'login',
  'password',
  'email-otp',
  'email-otp-verify',
  'signup',
  'organization',
  'mfa',
  'mfa-enroll',
]);

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

          @if (viewModel()?.info) {
            <div class="ha-success">{{ viewModel()?.info }}</div>
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
            @if (view() === 'login') {
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
                  <button type="button" class="ha-link-btn" (click)="formView.set('signup')">Sign up</button>
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
                  <button type="button" class="ha-link-btn" (click)="formView.set('login')">Use a different email</button>
                  @if (supportsEmailOtp()) {
                    <button type="button" class="ha-link-btn" (click)="formView.set('email-otp')">Email me a code instead</button>
                  }
                </div>
              </form>
            }

            @if (view() === 'email-otp') {
              <form class="ha-form" (ngSubmit)="onRequestEmailOtp()">
                <div class="ha-field">
                  <label for="ha-otp-email">Email</label>
                  <input id="ha-otp-email" type="email" [(ngModel)]="email" name="email" placeholder="you&#64;company.com" required>
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Sending code...' : 'Email me a code' }}
                </button>
                <div class="ha-alt">
                  @if (supportsPassword()) {
                    <button type="button" class="ha-link-btn" (click)="formView.set('password')">Use password instead</button>
                  }
                  <button type="button" class="ha-link-btn" (click)="formView.set('login')">Use a different email</button>
                </div>
              </form>
            }

            @if (view() === 'email-otp-verify') {
              <form class="ha-form" (ngSubmit)="onVerifyEmailOtp()">
                <div class="ha-field">
                  <label for="ha-otp-code">Code</label>
                  <input id="ha-otp-code" type="text" [(ngModel)]="otpCode" name="otpCode" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus>
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Verifying...' : 'Verify code' }}
                </button>
                <div class="ha-alt">
                  <button type="button" class="ha-link-btn" (click)="formView.set('email-otp')">Send a new code</button>
                  @if (supportsPassword()) {
                    <button type="button" class="ha-link-btn" (click)="formView.set('password')">Use password instead</button>
                  }
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
                  <button type="button" class="ha-link-btn" (click)="formView.set('login')">Sign in</button>
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

            <!-- MFA challenge -->
            @if (view() === 'mfa') {
              <form class="ha-form" (ngSubmit)="onVerifyMfa()">
                <div class="ha-field">
                  <label for="ha-mfa-code">Authenticator or recovery code</label>
                  <input id="ha-mfa-code" type="text" [(ngModel)]="mfaCode" name="mfaCode" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus>
                  @if (fieldErrors()['code']) {
                    <p class="ha-field-error">{{ fieldErrors()['code'] }}</p>
                  }
                </div>
                <button type="submit" class="ha-submit" [disabled]="loading()">
                  {{ loading() ? 'Verifying...' : 'Verify' }}
                </button>
              </form>
            }

            <!-- MFA enrollment -->
            @if (view() === 'mfa-enroll') {
              @if (viewModel()?.totpEnrollment; as enrollment) {
                <form class="ha-form" (ngSubmit)="onVerifyMfaEnrollment()">
                  <div class="ha-mfa-setup">
                    <img [src]="enrollment.qrCodeDataUrl" alt="Authenticator setup QR code" width="160" height="160">
                    <p>Scan with an authenticator app, or enter the secret manually: <code>{{ enrollment.secret }}</code></p>
                  </div>
                  <div class="ha-field">
                    <label for="ha-mfa-enroll-code">Verification code</label>
                    <input id="ha-mfa-enroll-code" type="text" [(ngModel)]="mfaCode" name="mfaCode" inputmode="numeric" autocomplete="one-time-code" placeholder="123456" required autofocus>
                    @if (fieldErrors()['code']) {
                      <p class="ha-field-error">{{ fieldErrors()['code'] }}</p>
                    }
                  </div>
                  <button type="submit" class="ha-submit" [disabled]="loading()">
                    {{ loading() ? 'Verifying...' : 'Verify and continue' }}
                  </button>
                </form>
              } @else {
                <div class="ha-form">
                  <p class="muted">This organization requires an authenticator app before you can continue.</p>
                  <button type="button" class="ha-submit" [disabled]="loading()" (click)="onStartMfaEnrollment()">
                    {{ loading() ? 'Starting...' : 'Add authenticator app' }}
                  </button>
                </div>
              }
            }

            <!-- Steps this component does not draw -->
            @if (!isHandledView()) {
              <div class="ha-form">
                <p class="muted">
                  SqlOS needs the &ldquo;{{ view() }}&rdquo; step, which this page does not draw. Continue with hosted sign-in to finish.
                </p>
                <button type="button" class="ha-submit" [disabled]="flowStarting()" (click)="startFlow('login')">
                  Continue with hosted sign-in
                </button>
              </div>
            }

            <!-- Provider buttons -->
            @if (showProviderButtons()) {
              <div class="ha-providers">
                <div class="ha-divider"><span>or</span></div>
                @for (provider of viewModel()?.providers ?? []; track provider.connectionId) {
                  <button type="button" class="ha-provider-btn" [disabled]="loading()" (click)="onProviderStart(provider.connectionId)">
                    @if (provider.logoDataUrl) {
                      <span class="ha-provider-logo-badge" aria-hidden="true">
                        <img [src]="provider.logoDataUrl" alt="">
                      </span>
                    } @else {
                      <span class="ha-provider-logo-badge ha-provider-logo-badge--fallback" aria-hidden="true">
                        {{ providerMonogram(provider.displayName) }}
                      </span>
                    }
                    <span class="ha-provider-btn-label">Continue with {{ provider.displayName }}</span>
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
export class AuthAuthorizeComponent implements OnInit, OnDestroy {
  private authService = inject(AuthService);
  private route = inject(ActivatedRoute);
  private flow: HeadlessFlow | null = null;
  private unsubscribe: (() => void) | null = null;

  IMAGE_LOGIN = IMAGE_LOGIN;
  IMAGE_SIGNUP = IMAGE_SIGNUP;
  referralOptions = referralOptions;

  requestId: string | null = null;
  initialIsSignup = false;
  initialView = 'login';
  private lastFlowView: string | null = null;

  flowStatus = signal<HeadlessFlowStatus>('idle');
  flowView = signal<string | null>(null);
  formView = signal<string | null>(null);
  error = signal<string | null>(null);
  fieldErrors = signal<Record<string, string>>({});
  viewModel = signal<HeadlessViewModel | null>(null);

  view = () => this.formView() ?? this.flowView() ?? this.initialView;
  loading = () => this.flowStatus() === 'loading';

  email = '';
  password = '';
  otpCode = '';
  mfaCode = '';
  organizationName = '';
  firstName = '';
  lastName = '';
  referralSource = '';

  // Flow starter state
  flowStarting = signal(false);
  starterError = signal<string | null>(null);
  starterView = signal<'login' | 'signup'>('login');

  isSignup = () => this.view() === 'signup';
  isHandledView = () => HANDLED_VIEWS.has(this.view());

  headline = () => {
    if (this.isSignup()) return 'Start your free trial';
    if (this.view() === 'organization') return 'Choose workspace';
    if (this.view() === 'mfa') return 'Two-step verification';
    if (this.view() === 'mfa-enroll') return 'Add authenticator app';
    return 'Welcome back';
  };

  subtitle = () => {
    if (this.isSignup()) return 'Create your account and start managing retail operations in minutes.';
    if (this.view() === 'organization') return "Select the organization you'd like to sign in to.";
    if (this.view() === 'mfa') return 'Enter an authenticator code or one of your recovery codes.';
    if (this.view() === 'mfa-enroll') return 'Set up an authenticator app before continuing.';
    return 'Sign in to your Northwind Retail account.';
  };

  testimonialQuote = () => this.isSignup()
    ? 'Setting up took less than five minutes. We had our entire team onboarded before lunch.'
    : "I love that I can see exactly my stores. No noise, no clutter — just the data I need.";

  testimonialName = () => this.isSignup() ? 'Marcus Rivera' : 'Priya Sharma';
  testimonialRole = () => this.isSignup() ? 'Head of Retail Ops, FreshMart' : 'Store Manager, Target #100';

  showProviderButtons = () => {
    const v = this.view();
    return (v === 'login' || v === 'signup') && (this.viewModel()?.providers?.length ?? 0) > 0;
  };

  supportsPassword = () => credentialEnabled(this.viewModel()?.settings, 'password');
  supportsEmailOtp = () => credentialEnabled(this.viewModel()?.settings, 'email_otp');

  providerMonogram(displayName: string): string {
    return getProviderMonogram(displayName);
  }

  async ngOnInit() {
    const params = this.route.snapshot.queryParamMap;
    this.requestId = params.get('request');
    this.initialView = params.get('view') || 'login';
    this.initialIsSignup = this.initialView === 'signup';
    this.email = params.get('email') || '';
    const initialDisplayName = params.get('displayName') || '';

    this.flow = createHeadlessFlow({
      issuer: `${environment.apiUrl}/sqlos/auth`,
      clientId: environment.clientId,
      redirectUri: `${window.location.origin}/auth/callback`,
      credentials: 'include',
    });
    this.unsubscribe = this.flow.subscribe(() => this.applyFlow());

    if (!this.requestId) {
      // Error-only bounce: SqlOS dropped the request id but still reports why.
      const initialError = params.get('error');
      if (initialError) this.error.set(initialError);
      return;
    }

    await this.flow.resume(window.location);
    const vm = this.viewModel();
    if (vm?.displayName && !this.firstName && !this.lastName && initialDisplayName) {
      const [first = '', ...rest] = vm.displayName.split(' ');
      this.firstName = first;
      this.lastName = rest.join(' ');
    }
  }

  ngOnDestroy() {
    this.unsubscribe?.();
  }

  private applyFlow() {
    const flow = this.flow;
    if (!flow) return;
    if (flow.status === 'redirect' && flow.redirectUrl) {
      window.location.assign(flow.redirectUrl);
      return;
    }
    this.flowStatus.set(flow.status);
    this.error.set(flow.error);
    this.fieldErrors.set(flow.fieldErrors);
    this.viewModel.set(flow.viewModel);
    const nextView = flow.viewModel?.view ?? null;
    if (nextView && nextView !== this.lastFlowView) {
      this.formView.set(null);
    }
    this.lastFlowView = nextView;
    this.flowView.set(nextView);
    if (flow.viewModel?.email) this.email = flow.viewModel.email;
    if (flow.viewModel?.challengeToken) this.otpCode = '';
    if (flow.viewModel?.mfaToken || flow.viewModel?.totpEnrollment) this.mfaCode = '';
  }

  onIdentify() {
    if (!this.flow) return;
    void this.flow.identify({ email: this.email });
  }

  onLogin() {
    if (!this.flow) return;
    void this.flow.password.login({ email: this.email, password: this.password });
  }

  onRequestEmailOtp() {
    if (!this.flow) return;
    void this.flow.emailOtp.start({ email: this.email });
  }

  onVerifyEmailOtp() {
    if (!this.flow) return;
    void this.flow.emailOtp.verify({ code: this.otpCode });
  }

  onSignup() {
    if (!this.flow) return;
    void this.flow.signup({
      displayName: buildDisplayName(this.firstName, this.lastName, this.email),
      email: this.email,
      password: this.password,
      organizationName: this.organizationName,
      customFields: { referralSource: this.referralSource, firstName: this.firstName, lastName: this.lastName },
    });
  }

  onProviderStart(connectionId: string) {
    if (!this.flow) return;
    void this.flow.provider.start({ connectionId, email: this.email || undefined });
  }

  onSelectOrganization(organizationId: string) {
    if (!this.flow) return;
    void this.flow.organization.select({ organizationId });
  }

  onVerifyMfa() {
    if (!this.flow) return;
    void this.flow.mfa.verify({ code: this.mfaCode });
  }

  onStartMfaEnrollment() {
    if (!this.flow) return;
    void this.flow.mfa.totp.enrollStart({ displayName: 'Authenticator app' });
  }

  onVerifyMfaEnrollment() {
    if (!this.flow) return;
    void this.flow.mfa.totp.enrollVerify({ code: this.mfaCode });
  }

  startFlow(flowView: 'login' | 'signup') {
    this.starterView.set(flowView);
    this.flowStarting.set(true);
    this.starterError.set(null);
    try {
      this.authService.startHostedSignIn(flowView, this.route.snapshot.queryParamMap.get('next') || '/retail');
    } catch (err) {
      this.starterError.set(err instanceof Error ? err.message : 'Failed to start.');
      this.flowStarting.set(false);
    }
  }
}
