import { Component, inject, signal } from '@angular/core';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { SqlosAuthService } from '../../services/sqlos-auth.service';

@Component({
  selector: 'app-landing',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="lp">
      <!-- Nav -->
      <nav class="lp-nav">
        <div class="lp-nav-inner">
          <div class="lp-nav-brand">
            <div class="lp-nav-logo">N</div>
            <span>Northwind Retail</span>
          </div>
          <div class="lp-nav-links">
            <a href="#features">Features</a>
            <a href="#how-it-works">How It Works</a>
            <a href="#roles">Roles</a>
            @if (isAuthenticated()) {
              <a routerLink="/retail" class="lp-btn lp-btn--primary lp-btn--sm">Open Dashboard</a>
            } @else {
              <button type="button" class="lp-btn lp-btn--primary" [disabled]="starting() !== null" (click)="startAuth('login')">
                {{ starting() === 'login' ? 'Redirecting...' : 'Sign In' }}
              </button>
            }
          </div>
        </div>
      </nav>

      <!-- Hero -->
      <section class="lp-hero">
        <div class="lp-hero-inner">
          <div class="lp-hero-text">
            <div class="lp-pill">Powered by SqlOS</div>
            <h1>Retail management,<br>simplified.</h1>
            <p>
              Track chains, stores, and inventory across your entire operation.
              Fine-grained access control means every team member sees exactly what they need.
            </p>
            <div class="lp-hero-actions">
              @if (isAuthenticated()) {
                <a routerLink="/retail" class="lp-btn lp-btn--primary lp-btn--lg">Open Dashboard</a>
              } @else {
                <button type="button" class="lp-btn lp-btn--primary" [disabled]="starting() !== null" (click)="startAuth('signup')">
                  {{ starting() === 'signup' ? 'Redirecting...' : 'Get Started Free' }}
                </button>
                <button type="button" class="lp-btn lp-btn--ghost" [disabled]="starting() !== null" (click)="startAuth('login')">
                  {{ starting() === 'login' ? 'Redirecting...' : 'Sign In' }}
                </button>
              }
            </div>
            <div class="lp-hero-proof">
              <div class="lp-avatars">
                @for (i of [11, 12, 13, 14, 15]; track i) {
                  <img [src]="'https://i.pravatar.cc/64?img=' + i" alt="" class="lp-avatar">
                }
              </div>
              <span>Trusted by 2,400+ retail teams worldwide</span>
            </div>
          </div>
          <div class="lp-hero-visual">
            <img src="https://images.unsplash.com/photo-1556740758-90de374c12ad?w=800&q=80&auto=format" alt="Modern retail store interior" class="lp-hero-img">
          </div>
        </div>
      </section>

      <!-- Logos -->
      <section class="lp-logos">
        <div class="lp-logos-inner">
          <p>Trusted by leading retail brands</p>
          <div class="lp-logos-row">
            @for (name of ['Walmart', 'Target', 'Costco', 'Kroger', 'Aldi']; track name) {
              <div class="lp-logo-item">{{ name }}</div>
            }
          </div>
        </div>
      </section>

      <!-- Features -->
      <section class="lp-section" id="features">
        <div class="lp-section-inner">
          <div class="lp-section-header">
            <div class="lp-pill">Features</div>
            <h2>Everything you need to run retail operations</h2>
            <p>From a single store to thousands of locations, Northwind scales with you.</p>
          </div>
          <div class="lp-features-grid">
            <div class="lp-feature-card lp-feature-card--hero">
              <img src="https://images.unsplash.com/photo-1441986300917-64674bd600d8?w=600&q=80&auto=format" alt="Retail store shelves">
              <div class="lp-feature-card-body">
                <h3>Chain Management</h3>
                <p>Organize your retail empire by chain. Track headquarters, regions, and performance across every brand in your portfolio.</p>
              </div>
            </div>
            <div class="lp-feature-card">
              <div class="lp-feature-icon">📍</div>
              <h3>Store Locations</h3>
              <p>Every store with its address, manager, and real-time inventory data. Search, filter, and drill into any location instantly.</p>
            </div>
            <div class="lp-feature-card">
              <div class="lp-feature-icon">📦</div>
              <h3>Inventory Tracking</h3>
              <p>Visual stock levels with automatic low-stock alerts. One-click restock keeps your shelves full and customers happy.</p>
            </div>
            <div class="lp-feature-card">
              <div class="lp-feature-icon">🔐</div>
              <h3>Built-in Auth</h3>
              <p>OAuth 2.0 with PKCE, SAML SSO, and social login. No external auth service needed — it ships with your app.</p>
            </div>
            <div class="lp-feature-card">
              <div class="lp-feature-icon">🛡️</div>
              <h3>Fine-Grained Access</h3>
              <p>Company admins see everything. Store clerks see their store. Regional managers see their region. It just works.</p>
            </div>
          </div>
        </div>
      </section>

      <!-- How It Works -->
      <section class="lp-section lp-section--dark" id="how-it-works">
        <div class="lp-section-inner">
          <div class="lp-section-header">
            <div class="lp-pill">How It Works</div>
            <h2>Three steps to retail clarity</h2>
          </div>
          <div class="lp-steps">
            <div class="lp-step">
              <div class="lp-step-num">1</div>
              <h3>Sign up &amp; add your chains</h3>
              <p>Create your account and start adding your retail chains. Import existing data or build from scratch.</p>
            </div>
            <div class="lp-step">
              <div class="lp-step-num">2</div>
              <h3>Invite your team</h3>
              <p>Add store managers, regional leads, and clerks. Each person gets exactly the access they need — no more, no less.</p>
            </div>
            <div class="lp-step">
              <div class="lp-step-num">3</div>
              <h3>Track &amp; manage</h3>
              <p>Monitor inventory levels, restock items, and keep your entire operation running smoothly from one dashboard.</p>
            </div>
          </div>
        </div>
      </section>

      <!-- Roles -->
      <section class="lp-section" id="roles">
        <div class="lp-section-inner">
          <div class="lp-section-header">
            <div class="lp-pill">Role-Based Access</div>
            <h2>Every role, one platform</h2>
            <p>Northwind automatically shows each user exactly what they need. No configuration required.</p>
          </div>
          <div class="lp-roles-grid">
            <div class="lp-role-card">
              <img src="https://images.unsplash.com/photo-1560250097-0b93528c311a?w=400&q=80&auto=format" alt="Company Admin">
              <div class="lp-role-body">
                <h3>Company Admin</h3>
                <p>Full visibility across all chains, stores, and inventory. Create new chains, manage the team, and see the big picture.</p>
                <span class="lp-role-sees">Sees: Everything</span>
              </div>
            </div>
            <div class="lp-role-card">
              <img src="https://images.unsplash.com/photo-1573497019940-1c28c88b4f3e?w=400&q=80&auto=format" alt="Regional Manager">
              <div class="lp-role-body">
                <h3>Regional Manager</h3>
                <p>Oversee all stores in your chain. Track inventory levels, spot trends, and keep every location running smoothly.</p>
                <span class="lp-role-sees">Sees: Their chain only</span>
              </div>
            </div>
            <div class="lp-role-card">
              <img src="https://images.unsplash.com/photo-1580489944761-15a19d654956?w=400&q=80&auto=format" alt="Store Clerk">
              <div class="lp-role-body">
                <h3>Store Clerk</h3>
                <p>View your store's inventory, check stock levels, and flag items for restock. Simple, focused, no distractions.</p>
                <span class="lp-role-sees">Sees: Their store only</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- Testimonial -->
      <section class="lp-section lp-section--dark">
        <div class="lp-section-inner">
          <div class="lp-testimonial">
            <blockquote>
              &ldquo;We used to manage inventory with spreadsheets. Now every store manager can see exactly their stock,
              restock in one click, and we haven't had a stockout since switching. The role-based access is magic.&rdquo;
            </blockquote>
            <div class="lp-testimonial-author">
              <img src="https://i.pravatar.cc/80?img=32" alt="">
              <div>
                <strong>Sarah Chen</strong>
                <span>VP of Operations, RetailCorp</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      <!-- CTA -->
      <section class="lp-cta">
        <div class="lp-cta-inner">
          <h2>Ready to streamline your retail operations?</h2>
          <p>Join 2,400+ teams already using Northwind to manage their stores.</p>
          <div class="lp-hero-actions">
            @if (isAuthenticated()) {
              <a routerLink="/retail" class="lp-btn lp-btn--primary lp-btn--lg">Open Dashboard</a>
            } @else {
              <button type="button" class="lp-btn lp-btn--primary lp-btn--lg" [disabled]="starting() !== null" (click)="startAuth('signup')">
                {{ starting() === 'signup' ? 'Redirecting...' : 'Start Free Trial' }}
              </button>
              <button type="button" class="lp-btn lp-btn--ghost lp-btn--lg" [disabled]="starting() !== null" (click)="startAuth('login')">
                Sign In
              </button>
            }
          </div>
        </div>
      </section>

      <!-- Footer -->
      <footer class="lp-footer">
        <div class="lp-footer-inner">
          <div class="lp-footer-brand">
            <div class="lp-nav-logo">N</div>
            <span>Northwind Retail</span>
          </div>
          <div class="lp-footer-links">
            <a href="http://localhost:5062/sqlos/" target="_blank" rel="noopener">SqlOS Dashboard</a>
            <span class="lp-footer-sep">·</span>
            <span class="lp-footer-muted">Built with SqlOS — Auth &amp; FGA in a single NuGet package</span>
          </div>
        </div>
      </footer>
    </div>
  `,
})
export class LandingComponent {
  private auth = inject(AuthService);
  private sqlosAuth = inject(SqlosAuthService);
  private route = inject(ActivatedRoute);

  isAuthenticated = this.auth.isAuthenticated;
  starting = signal<'login' | 'signup' | null>(null);

  async startAuth(view: 'login' | 'signup') {
    this.starting.set(view);
    try {
      const next = this.route.snapshot.queryParamMap.get('next') || '/retail';
      const verifier = this.sqlosAuth.createOpaqueToken(48);
      const state = this.sqlosAuth.createOpaqueToken(24);
      const challenge = await this.sqlosAuth.createCodeChallenge(verifier);
      this.sqlosAuth.persistAuthFlow(view, state, verifier, next);

      const url = new URL(`${this.sqlosAuth.getAuthServerUrl()}/authorize`);
      url.searchParams.set('response_type', 'code');
      url.searchParams.set('client_id', this.sqlosAuth.getClientId());
      url.searchParams.set('redirect_uri', this.sqlosAuth.getRedirectUri());
      url.searchParams.set('state', state);
      url.searchParams.set('code_challenge', challenge);
      url.searchParams.set('code_challenge_method', 'S256');
      if (view === 'signup') url.searchParams.set('view', 'signup');

      window.location.replace(url.toString());
    } catch {
      this.starting.set(null);
    }
  }
}
