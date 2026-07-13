/**
 * Small, real-looking product fragments used in the hero collage and the
 * capability canvases. Pure HTML/CSS — crisp at any size, themeable.
 */

export function LoginMini({ className }: { className?: string }) {
  return (
    <div
      className={[
        "w-60 rounded-2xl border bg-white/90 p-5 shadow-xl shadow-primary/10 backdrop-blur",
        className ?? "",
      ].join(" ")}
    >
      <div className="mx-auto flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-[10px] font-bold text-primary-foreground">
        YP
      </div>
      <p className="mt-2 text-center text-[12px] font-semibold text-foreground">
        Sign in to YourProduct
      </p>
      <div className="mt-3 space-y-2">
        <div className="rounded-md border bg-background px-2.5 py-1.5 text-[11px] text-muted-foreground">
          sarah@acme.co
        </div>
        <div className="rounded-md bg-primary px-2.5 py-1.5 text-center text-[11px] font-semibold text-primary-foreground">
          Continue
        </div>
        <div className="rounded-md border bg-background px-2.5 py-1.5 text-center text-[11px] font-medium text-foreground">
          Continue with Google
        </div>
      </div>
    </div>
  );
}

export function SsoPill({ className }: { className?: string }) {
  return (
    <div
      className={[
        "flex items-center gap-2 rounded-full border bg-white/90 py-2 pl-3 pr-4 shadow-lg shadow-primary/10 backdrop-blur",
        className ?? "",
      ].join(" ")}
    >
      <span className="relative flex h-2 w-2">
        <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-emerald-500 opacity-60" />
        <span className="relative inline-flex h-2 w-2 rounded-full bg-emerald-500" />
      </span>
      <span className="font-mono text-[11px] text-foreground">
        @acme.co → Okta SSO
      </span>
    </div>
  );
}




export function ProviderRow({ className }: { className?: string }) {
  const providers = [
    { name: "Google", glyph: <GoogleGlyph /> },
    { name: "Microsoft", glyph: <MicrosoftGlyph /> },
    { name: "Apple", glyph: <AppleGlyph /> },
    { name: "Okta", glyph: <OktaGlyph /> },
    { name: "SAML", glyph: <span className="font-mono text-[9px] font-bold">SAML</span> },
  ];
  return (
    <div className={["flex flex-wrap items-center gap-2", className ?? ""].join(" ")}>
      {providers.map((p) => (
        <span
          key={p.name}
          title={p.name}
          className="flex h-10 w-10 items-center justify-center rounded-xl border bg-white shadow-sm"
        >
          {p.glyph}
        </span>
      ))}
    </div>
  );
}

export function TreeMini({ className }: { className?: string }) {
  const rows = [
    { label: "Acme Corp", depth: 0, badge: "manager" },
    { label: "North America", depth: 1, badge: "inherits" },
    { label: "Flagship Retail", depth: 2, badge: "inherits" },
    { label: "Seattle #01", depth: 3, badge: "inherits" },
  ];
  return (
    <div className={["space-y-1.5", className ?? ""].join(" ")}>
      {rows.map((row) => (
        <div
          key={row.label}
          style={{ marginLeft: row.depth * 14 }}
          className={[
            "flex items-center justify-between rounded-lg border px-3 py-2",
            row.depth === 0 ? "border-primary/40 bg-white shadow-sm" : "bg-white/70",
          ].join(" ")}
        >
          <span className="text-[12px] font-medium text-foreground">{row.label}</span>
          <span
            className={[
              "rounded-full px-2 py-0.5 font-mono text-[9px] uppercase tracking-wide",
              row.depth === 0
                ? "bg-primary text-primary-foreground"
                : "border border-primary/30 text-primary",
            ].join(" ")}
          >
            {row.badge}
          </span>
        </div>
      ))}
    </div>
  );
}

function GoogleGlyph() {
  return (
    <svg viewBox="0 0 24 24" className="h-4 w-4">
      <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 01-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4" />
      <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853" />
      <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05" />
      <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335" />
    </svg>
  );
}

function MicrosoftGlyph() {
  return (
    <svg viewBox="0 0 24 24" className="h-4 w-4">
      <rect x="2" y="2" width="9" height="9" fill="#F25022" />
      <rect x="13" y="2" width="9" height="9" fill="#7FBA00" />
      <rect x="2" y="13" width="9" height="9" fill="#00A4EF" />
      <rect x="13" y="13" width="9" height="9" fill="#FFB900" />
    </svg>
  );
}

function AppleGlyph() {
  return (
    <svg viewBox="0 0 24 24" className="h-4 w-4" fill="currentColor">
      <path d="M18.71 19.5c-.83 1.24-1.71 2.45-3.05 2.47-1.34.03-1.77-.79-3.29-.79-1.53 0-2 .77-3.27.82-1.31.05-2.3-1.32-3.14-2.53C4.25 17 2.94 12.45 4.7 9.39c.87-1.52 2.43-2.48 4.12-2.51 1.28-.02 2.5.87 3.29.87.78 0 2.26-1.07 3.8-.91.65.03 2.47.26 3.64 1.98-.09.06-2.17 1.28-2.15 3.81.03 3.02 2.65 4.03 2.68 4.04-.03.07-.42 1.44-1.38 2.83M13 3.5c.73-.83 1.94-1.46 2.94-1.5.13 1.17-.34 2.35-1.04 3.19-.69.85-1.83 1.51-2.95 1.42-.15-1.15.41-2.35 1.05-3.11z" />
    </svg>
  );
}

function OktaGlyph() {
  return (
    <svg viewBox="0 0 24 24" className="h-4 w-4">
      <circle cx="12" cy="12" r="9" fill="none" stroke="#007DC1" strokeWidth="2.5" />
      <circle cx="12" cy="12" r="3.5" fill="#007DC1" />
    </svg>
  );
}
