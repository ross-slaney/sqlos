"use client";

import { Suspense, useState } from "react";
import { useSession } from "next-auth/react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  getExampleAuthServerUrl,
  getExampleClientId,
  getExampleRedirectUri,
  createOpaqueToken,
  createCodeChallenge,
  persistSqlOSAuthFlow,
} from "@/lib/sqlos-auth";

export default function LandingPage() {
  return (
    <Suspense>
      <LandingContent />
    </Suspense>
  );
}

function LandingContent() {
  const { data: session } = useSession();
  const searchParams = useSearchParams();
  const next = searchParams.get("next") || "/retail";
  const [starting, setStarting] = useState<"login" | "signup" | null>(null);

  async function startAuth(view: "login" | "signup") {
    setStarting(view);
    try {
      const verifier = createOpaqueToken(48);
      const state = createOpaqueToken(24);
      const challenge = await createCodeChallenge(verifier);
      persistSqlOSAuthFlow(view, state, verifier, next);
      const url = new URL(`${getExampleAuthServerUrl()}/authorize`);
      url.searchParams.set("response_type", "code");
      url.searchParams.set("client_id", getExampleClientId());
      url.searchParams.set("redirect_uri", getExampleRedirectUri());
      url.searchParams.set("state", state);
      url.searchParams.set("code_challenge", challenge);
      url.searchParams.set("code_challenge_method", "S256");
      if (view === "signup") url.searchParams.set("view", "signup");
      window.location.replace(url.toString());
    } catch {
      setStarting(null);
    }
  }

  function AuthButton({ view, label }: { view: "login" | "signup"; label: string }) {
    return (
      <button
        type="button"
        className={view === "login" ? "lp-btn lp-btn--primary" : "lp-btn lp-btn--ghost"}
        disabled={starting !== null}
        onClick={() => void startAuth(view)}
      >
        {starting === view ? "Redirecting..." : label}
      </button>
    );
  }

  return (
    <div className="lp">
      {/* ── Nav ── */}
      <nav className="lp-nav">
        <div className="lp-nav-inner">
          <div className="lp-nav-brand">
            <div className="lp-nav-logo">N</div>
            <span>Northwind Retail</span>
          </div>
          <div className="lp-nav-links">
            <a href="#features">Features</a>
            <a href="#how-it-works">How It Works</a>
            <a href="#roles">Roles</a>
            {session ? (
              <Link href="/retail" className="lp-btn lp-btn--primary lp-btn--sm">Open Dashboard</Link>
            ) : (
              <AuthButton view="login" label="Sign In" />
            )}
          </div>
        </div>
      </nav>

      {/* ── Hero ── */}
      <section className="lp-hero">
        <div className="lp-hero-inner">
          <div className="lp-hero-text">
            <div className="lp-pill">Powered by SqlOS</div>
            <h1>Retail management,<br />simplified.</h1>
            <p>
              Track chains, stores, and inventory across your entire operation.
              Fine-grained access control means every team member sees exactly what they need.
            </p>
            <div className="lp-hero-actions">
              {session ? (
                <Link href="/retail" className="lp-btn lp-btn--primary lp-btn--lg">Open Dashboard</Link>
              ) : (
                <>
                  <AuthButton view="signup" label="Get Started Free" />
                  <AuthButton view="login" label="Sign In" />
                </>
              )}
            </div>
            <div className="lp-hero-proof">
              <div className="lp-avatars">
                {[1, 2, 3, 4, 5].map((i) => (
                  <img key={i} src={`https://i.pravatar.cc/64?img=${i + 10}`} alt="" className="lp-avatar" />
                ))}
              </div>
              <span>Trusted by 2,400+ retail teams worldwide</span>
            </div>
          </div>
          <div className="lp-hero-visual">
            <img
              src="https://images.unsplash.com/photo-1556740758-90de374c12ad?w=800&q=80&auto=format"
              alt="Modern retail store interior"
              className="lp-hero-img"
            />
          </div>
        </div>
      </section>

      {/* ── Logos ── */}
      <section className="lp-logos">
        <div className="lp-logos-inner">
          <p>Trusted by leading retail brands</p>
          <div className="lp-logos-row">
            {["Walmart", "Target", "Costco", "Kroger", "Aldi"].map((name) => (
              <div key={name} className="lp-logo-item">{name}</div>
            ))}
          </div>
        </div>
      </section>

      {/* ── Features ── */}
      <section className="lp-section" id="features">
        <div className="lp-section-inner">
          <div className="lp-section-header">
            <div className="lp-pill">Features</div>
            <h2>Everything you need to run retail operations</h2>
            <p>From a single store to thousands of locations, Northwind scales with you.</p>
          </div>

          <div className="lp-features-grid">
            <div className="lp-feature-card lp-feature-card--hero">
              <img
                src="https://images.unsplash.com/photo-1441986300917-64674bd600d8?w=600&q=80&auto=format"
                alt="Retail store shelves"
              />
              <div className="lp-feature-card-body">
                <h3>Chain Management</h3>
                <p>Organize your retail empire by chain. Track headquarters, regions, and performance across every brand in your portfolio.</p>
              </div>
            </div>
            <div className="lp-feature-card">
              <div className="lp-feature-icon">📍</div>
              <h3>Store Locations</h3>
              <p>Every store with its address, manager, and real-time inventory data. Search, filter, and drill into any location instantly.</p>
            </div>
            <div className="lp-feature-card">
              <div className="lp-feature-icon">📦</div>
              <h3>Inventory Tracking</h3>
              <p>Visual stock levels with automatic low-stock alerts. One-click restock keeps your shelves full and customers happy.</p>
            </div>
            <div className="lp-feature-card">
              <div className="lp-feature-icon">🔐</div>
              <h3>Built-in Auth</h3>
              <p>OAuth 2.0 with PKCE, SAML SSO, and social login. No external auth service needed — it ships with your app.</p>
            </div>
            <div className="lp-feature-card">
              <div className="lp-feature-icon">🛡️</div>
              <h3>Fine-Grained Access</h3>
              <p>Company admins see everything. Store clerks see their store. Regional managers see their region. It just works.</p>
            </div>
          </div>
        </div>
      </section>

      {/* ── How It Works ── */}
      <section className="lp-section lp-section--dark" id="how-it-works">
        <div className="lp-section-inner">
          <div className="lp-section-header">
            <div className="lp-pill">How It Works</div>
            <h2>Three steps to retail clarity</h2>
          </div>

          <div className="lp-steps">
            <div className="lp-step">
              <div className="lp-step-num">1</div>
              <h3>Sign up & add your chains</h3>
              <p>Create your account and start adding your retail chains. Import existing data or build from scratch.</p>
            </div>
            <div className="lp-step">
              <div className="lp-step-num">2</div>
              <h3>Invite your team</h3>
              <p>Add store managers, regional leads, and clerks. Each person gets exactly the access they need — no more, no less.</p>
            </div>
            <div className="lp-step">
              <div className="lp-step-num">3</div>
              <h3>Track & manage</h3>
              <p>Monitor inventory levels, restock items, and keep your entire operation running smoothly from one dashboard.</p>
            </div>
          </div>
        </div>
      </section>

      {/* ── Roles showcase ── */}
      <section className="lp-section" id="roles">
        <div className="lp-section-inner">
          <div className="lp-section-header">
            <div className="lp-pill">Role-Based Access</div>
            <h2>Every role, one platform</h2>
            <p>Northwind automatically shows each user exactly what they need. No configuration required.</p>
          </div>

          <div className="lp-roles-grid">
            <div className="lp-role-card">
              <img src="https://images.unsplash.com/photo-1560250097-0b93528c311a?w=400&q=80&auto=format" alt="Company Admin" />
              <div className="lp-role-body">
                <h3>Company Admin</h3>
                <p>Full visibility across all chains, stores, and inventory. Create new chains, manage the team, and see the big picture.</p>
                <span className="lp-role-sees">Sees: Everything</span>
              </div>
            </div>
            <div className="lp-role-card">
              <img src="https://images.unsplash.com/photo-1573497019940-1c28c88b4f3e?w=400&q=80&auto=format" alt="Regional Manager" />
              <div className="lp-role-body">
                <h3>Regional Manager</h3>
                <p>Oversee all stores in your chain. Track inventory levels, spot trends, and keep every location running smoothly.</p>
                <span className="lp-role-sees">Sees: Their chain only</span>
              </div>
            </div>
            <div className="lp-role-card">
              <img src="https://images.unsplash.com/photo-1580489944761-15a19d654956?w=400&q=80&auto=format" alt="Store Clerk" />
              <div className="lp-role-body">
                <h3>Store Clerk</h3>
                <p>View your store&apos;s inventory, check stock levels, and flag items for restock. Simple, focused, no distractions.</p>
                <span className="lp-role-sees">Sees: Their store only</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ── Testimonial ── */}
      <section className="lp-section lp-section--dark">
        <div className="lp-section-inner">
          <div className="lp-testimonial">
            <blockquote>
              &ldquo;We used to manage inventory with spreadsheets. Now every store manager can see exactly their stock,
              restock in one click, and we haven&apos;t had a stockout since switching. The role-based access is magic.&rdquo;
            </blockquote>
            <div className="lp-testimonial-author">
              <img src="https://i.pravatar.cc/80?img=32" alt="" />
              <div>
                <strong>Sarah Chen</strong>
                <span>VP of Operations, RetailCorp</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* ── CTA ── */}
      <section className="lp-cta">
        <div className="lp-cta-inner">
          <h2>Ready to streamline your retail operations?</h2>
          <p>Join 2,400+ teams already using Northwind to manage their stores.</p>
          <div className="lp-hero-actions">
            {session ? (
              <Link href="/retail" className="lp-btn lp-btn--primary lp-btn--lg">Open Dashboard</Link>
            ) : (
              <>
                <button
                  type="button"
                  className="lp-btn lp-btn--primary lp-btn--lg"
                  disabled={starting !== null}
                  onClick={() => void startAuth("signup")}
                >
                  {starting === "signup" ? "Redirecting..." : "Start Free Trial"}
                </button>
                <button
                  type="button"
                  className="lp-btn lp-btn--ghost lp-btn--lg"
                  disabled={starting !== null}
                  onClick={() => void startAuth("login")}
                >
                  Sign In
                </button>
              </>
            )}
          </div>
        </div>
      </section>

      {/* ── Footer ── */}
      <footer className="lp-footer">
        <div className="lp-footer-inner">
          <div className="lp-footer-brand">
            <div className="lp-nav-logo">N</div>
            <span>Northwind Retail</span>
          </div>
          <div className="lp-footer-links">
            <a href="http://localhost:5062/sqlos/" target="_blank" rel="noopener">SqlOS Dashboard</a>
            <span className="lp-footer-sep">·</span>
            <span className="lp-footer-muted">Built with SqlOS — Auth & FGA in a single NuGet package</span>
          </div>
        </div>
      </footer>
    </div>
  );
}
