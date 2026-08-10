import Link from "next/link";
import InstallCommand from "@/components/marketing/InstallCommand";

const check = (
  <svg
    className="h-[15px] w-[15px]"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
  >
    <path d="M20 6 9 17l-5-5" />
  </svg>
);

export default function Home() {
  return (
    <div className="relative min-h-screen">
      <Hero />
      <TrustBand />
      <Features />
      <CodeSplit />
      <ClosingCta />
    </div>
  );
}

/* ---------- Hero ---------- */

function Hero() {
  return (
    <div className="relative overflow-hidden border-b">
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 opacity-50 [background-image:linear-gradient(hsl(var(--border)/0.6)_1px,transparent_1px),linear-gradient(90deg,hsl(var(--border)/0.6)_1px,transparent_1px)] [background-size:44px_44px] [mask-image:linear-gradient(180deg,#000,transparent_75%)]"
      />
      <div
        aria-hidden="true"
        className="pointer-events-none absolute inset-0 [background:radial-gradient(60%_55%_at_78%_8%,hsl(244_76%_59%/0.07),transparent_60%)]"
      />
      <div className="relative mx-auto grid max-w-[1160px] items-center gap-14 px-7 pb-16 pt-20 lg:grid-cols-[1.05fr_1.15fr]">
        <div>
          <span className="mb-5 inline-flex items-center gap-2 rounded-full border border-primary/20 bg-accent px-3 py-1 text-[12.5px] font-semibold text-accent-foreground">
            <span className="h-1.5 w-1.5 rounded-full bg-primary" />
            Self-hosted · .NET · SQL Server
          </span>
          <h1 className="mb-5 text-4xl font-extrabold leading-[1.04] tracking-[-0.035em] text-foreground sm:text-5xl lg:text-[52px]">
            Enterprise auth,
            <br />
            embedded in your database.
          </h1>
          <p className="mb-7 max-w-[33ch] text-lg leading-normal text-muted-foreground">
            OAuth, SAML SSO, social login, and fine-grained authorization —
            self-hosted inside your SQL Server. No external identity vendor.
          </p>
          <div className="flex flex-wrap items-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center gap-2 rounded-[9px] bg-primary px-4 py-2.5 text-sm font-semibold text-primary-foreground shadow-[0_1px_2px_rgba(79,70,229,0.35),inset_0_1px_0_rgba(255,255,255,0.15)] transition-colors hover:bg-[#4338ca]"
            >
              Get started
              <svg
                className="h-[15px] w-[15px]"
                viewBox="0 0 24 24"
                fill="none"
                stroke="currentColor"
                strokeWidth="2.2"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M5 12h14M13 6l6 6-6 6" />
              </svg>
            </Link>
            <Link
              href="/docs"
              className="inline-flex items-center rounded-[9px] border bg-background px-4 py-2.5 text-sm font-semibold text-foreground transition-colors hover:bg-secondary"
            >
              Read the docs
            </Link>
          </div>
          <div className="mt-5">
            <InstallCommand />
          </div>
          <div className="mt-6 flex items-center gap-2.5 text-[13px] text-muted-foreground/90">
            <span className="text-emerald-500">{check}</span>
            Sub-millisecond authorization checks on tables with millions of rows
          </div>
        </div>

        <DashboardWindow />
      </div>
    </div>
  );
}

function DashboardWindow() {
  const sideLinks = [
    "Dashboard",
    "Organizations",
    "Users",
    "Sessions",
    "Providers",
    "Grants",
    "Audit log",
  ];
  const rows = [
    { user: "dana@acme.com", role: "admin", admin: true, seen: "2m ago" },
    { user: "will@acme.com", role: "member", admin: false, seen: "1h ago" },
    { user: "priya@acme.com", role: "member", admin: false, seen: "3h ago" },
  ];

  return (
    <div
      aria-hidden="true"
      className="hidden overflow-hidden rounded-[14px] border bg-background shadow-[0_12px_40px_-12px_rgba(10,10,20,0.18),0_4px_12px_-6px_rgba(10,10,20,0.10)] md:block"
    >
      <div className="flex items-center gap-3.5 border-b bg-secondary/70 px-3.5 py-[11px]">
        <div className="flex gap-[7px]">
          <span className="h-[11px] w-[11px] rounded-full bg-[#ff5f57]" />
          <span className="h-[11px] w-[11px] rounded-full bg-[#febc2e]" />
          <span className="h-[11px] w-[11px] rounded-full bg-[#28c840]" />
        </div>
        <div className="flex-1 rounded-[7px] border bg-background px-3 py-[5px] text-center font-mono text-xs text-muted-foreground/70">
          app.sqlos.dev/dashboard
        </div>
      </div>
      <div className="grid min-h-[340px] grid-cols-[150px_1fr]">
        <div className="flex flex-col gap-[3px] bg-[#0b0d12] px-3 py-4">
          <div className="mb-4 flex items-center gap-2 px-1.5 text-[13px] font-bold text-white">
            <span className="inline-block h-[18px] w-[18px] rounded-[5px] bg-gradient-to-br from-[#4f46e5] to-[#7c69f5]" />
            SqlOS
          </div>
          {sideLinks.map((label, i) => (
            <span
              key={label}
              className={[
                "rounded-[7px] px-2 py-[7px] text-xs font-medium",
                i === 0 ? "bg-[#1b1f2a] text-white" : "text-[#a1a1aa]",
              ].join(" ")}
            >
              {label}
            </span>
          ))}
        </div>
        <div className="bg-background p-5">
          <h4 className="mb-[3px] text-sm font-semibold text-foreground">
            Overview
          </h4>
          <p className="mb-4 text-[11.5px] text-muted-foreground/70">
            acme-corp · production
          </p>
          <div className="mb-[18px] grid grid-cols-3 gap-3">
            <Stat n="23" label="Orgs" />
            <Stat n="1,284" label="Users" highlight />
            <Stat n="6" label="Providers" />
          </div>
          <div className="overflow-hidden rounded-[10px] border">
            <div className="grid grid-cols-[1.6fr_1fr_0.8fr] gap-2 bg-secondary/70 px-3 py-[9px] text-[9.5px] font-semibold uppercase tracking-[0.05em] text-muted-foreground/70">
              <span>User</span>
              <span>Role</span>
              <span>Last seen</span>
            </div>
            {rows.map((r) => (
              <div
                key={r.user}
                className="grid grid-cols-[1.6fr_1fr_0.8fr] items-center gap-2 border-t px-3 py-[9px] text-[11.5px]"
              >
                <span className="font-semibold text-foreground">{r.user}</span>
                <span>
                  <span
                    className={[
                      "inline-block rounded-full px-2 py-0.5 text-[10px] font-semibold",
                      r.admin
                        ? "bg-accent text-accent-foreground"
                        : "bg-emerald-500/10 text-emerald-600",
                    ].join(" ")}
                  >
                    {r.role}
                  </span>
                </span>
                <span className="text-muted-foreground/70">{r.seen}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function Stat({
  n,
  label,
  highlight,
}: {
  n: string;
  label: string;
  highlight?: boolean;
}) {
  return (
    <div className="rounded-[10px] border p-3">
      <div
        className={[
          "text-[22px] font-bold tracking-[-0.03em]",
          highlight ? "text-primary" : "text-foreground",
        ].join(" ")}
      >
        {n}
      </div>
      <div className="mt-0.5 text-[10.5px] uppercase tracking-[0.05em] text-muted-foreground/70">
        {label}
      </div>
    </div>
  );
}

/* ---------- Trust band ---------- */

function TrustBand() {
  return (
    <div className="border-b pb-2 pt-9">
      <div className="mx-auto max-w-[1160px] px-7">
        <div className="mb-6 text-center text-[12.5px] font-semibold uppercase tracking-[0.08em] text-muted-foreground/70">
          Trusted by engineering teams
        </div>
        <div className="flex flex-wrap items-center justify-center gap-11 opacity-60 grayscale">
          <TrustLogo name="Northwind">
            <svg viewBox="0 0 24 24" fill="currentColor">
              <path d="M3 3h8v8H3zM13 3h8v8h-8zM3 13h8v8H3zM13 13h8v8h-8z" />
            </svg>
          </TrustLogo>
          <TrustLogo name="Contoso">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
            >
              <circle cx="12" cy="12" r="9" />
              <path d="M12 3v18M3 12h18" />
            </svg>
          </TrustLogo>
          <TrustLogo name="Fabrikam">
            <svg viewBox="0 0 24 24" fill="currentColor">
              <path d="M12 2 2 7l10 5 10-5zM2 17l10 5 10-5M2 12l10 5 10-5" />
            </svg>
          </TrustLogo>
          <TrustLogo name="Adventure Works">
            <svg
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
            >
              <path d="M12 2 4 6v6c0 5 3.5 8 8 10 4.5-2 8-5 8-10V6z" />
            </svg>
          </TrustLogo>
          <TrustLogo name="Tailspin">
            <svg viewBox="0 0 24 24" fill="currentColor">
              <circle cx="7" cy="12" r="4" />
              <circle cx="17" cy="12" r="4" />
            </svg>
          </TrustLogo>
        </div>
      </div>
    </div>
  );
}

function TrustLogo({
  name,
  children,
}: {
  name: string;
  children: React.ReactNode;
}) {
  return (
    <span className="flex items-center gap-2 text-base font-bold tracking-tight text-foreground/75 [&_svg]:h-5 [&_svg]:w-5">
      {children}
      {name}
    </span>
  );
}

/* ---------- Features ---------- */

const features = [
  {
    title: "Branded login pages",
    body: "Server-rendered auth UI you customize with logos, colors, and providers — all from the dashboard, no frontend build required.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <rect x="3" y="4" width="18" height="16" rx="2" />
        <path d="M3 9h18M8 14h5" />
      </svg>
    ),
  },
  {
    title: "Enterprise SSO",
    body: "SAML and OIDC with home-realm discovery — users are routed to the right identity provider by their email domain.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M12 3l8 4v5c0 5-3.5 8-8 9-4.5-1-8-4-8-9V7z" />
        <path d="M9 12l2 2 4-4" />
      </svg>
    ),
  },
  {
    title: "Hierarchical permissions",
    body: "Role-based access control that cascades through your org structure with inheritance — model real reporting lines, not flat lists.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <circle cx="12" cy="6" r="2.5" />
        <circle cx="6" cy="18" r="2.5" />
        <circle cx="18" cy="18" r="2.5" />
        <path d="M12 8.5v4M12 12.5l-5 3M12 12.5l5 3" />
      </svg>
    ),
  },
  {
    title: "Authorization in your queries",
    body: "Access control compiles into EF Core queries as WHERE clauses — checks happen in the database, with no extra round-trips.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M4 6h16M4 12h16M4 18h10" />
        <path d="M17 15l3 3-3 3" opacity=".55" />
      </svg>
    ),
  },
  {
    title: "Built-in admin dashboard",
    body: "Manage organizations, users, sessions, providers, grants, and audit logs — plus an access-testing tool to debug permissions live.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <rect x="3" y="3" width="8" height="8" rx="1.5" />
        <rect x="13" y="3" width="8" height="5" rx="1.5" />
        <rect x="13" y="10" width="8" height="11" rx="1.5" />
        <rect x="3" y="13" width="8" height="8" rx="1.5" />
      </svg>
    ),
  },
  {
    title: "Sub-millisecond checks",
    body: "Authorization stays fast at scale — sub-millisecond decisions on tables with millions of rows, evaluated where your data lives.",
    icon: (
      <svg
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <path d="M13 2 4 14h7l-1 8 9-12h-7z" />
      </svg>
    ),
  },
];

function Features() {
  return (
    <section id="features" className="scroll-mt-16 py-20 lg:py-[88px]">
      <div className="mx-auto max-w-[1160px] px-7">
        <div className="mx-auto mb-12 max-w-[640px] text-center">
          <div className="mb-3 text-[13px] font-semibold text-primary">
            One package. Every identity primitive.
          </div>
          <h2 className="mb-3.5 text-3xl font-extrabold leading-[1.1] tracking-[-0.03em] text-foreground lg:text-4xl">
            Everything you need to authenticate and authorize
          </h2>
          <p className="text-[17px] text-muted-foreground">
            Start with one application and hosted login. Add organizations,
            SSO, and SQL-backed authorization when your product needs them.
          </p>
        </div>
        <div className="grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
          {features.map((f) => (
            <div
              key={f.title}
              className="rounded-[14px] border bg-background p-6 transition-all duration-200 hover:-translate-y-0.5 hover:shadow-sm"
            >
              <div className="mb-4 grid h-[38px] w-[38px] place-items-center rounded-[10px] bg-accent text-primary [&_svg]:h-[19px] [&_svg]:w-[19px]">
                {f.icon}
              </div>
              <h3 className="mb-[7px] text-[16.5px] font-semibold tracking-[-0.02em] text-foreground">
                {f.title}
              </h3>
              <p className="text-sm leading-relaxed text-muted-foreground">
                {f.body}
              </p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

/* ---------- Code split + metrics ---------- */

function CodeSplit() {
  return (
    <div className="mx-auto max-w-[1160px] px-7">
      <div className="grid overflow-hidden rounded-[20px] border border-[#16181f] bg-gradient-to-b from-[#0a0c11] to-[#0b0d12] lg:grid-cols-[1fr_1.15fr]">
        <div className="flex flex-col justify-center px-7 py-9 lg:px-11 lg:py-[52px]">
          <div className="mb-3 text-[13px] font-semibold text-[#a5b4fc]">
            Fine-grained authorization
          </div>
          <h2 className="mb-3.5 text-[26px] font-extrabold leading-[1.1] tracking-[-0.03em] text-white lg:text-[30px]">
            Authorization compiled into your queries
          </h2>
          <p className="mb-5 text-[15.5px] text-[#a1a1aa]">
            Instead of loading rows and filtering them in application code,
            SqlOS turns a permission into a WHERE clause. One query returns
            exactly what the user is allowed to see.
          </p>
          <ul className="flex flex-col gap-3">
            {[
              "A single SQL query — no N+1, no extra round-trips",
              "Sub-millisecond on tables with millions of rows",
              "Works with the EF Core you already write",
            ].map((item) => (
              <li
                key={item}
                className="flex items-start gap-[11px] text-[14.5px] text-[#d4d4d8]"
              >
                <span className="mt-px shrink-0 text-[#818cf8]">
                  <svg
                    className="h-[18px] w-[18px]"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  >
                    <path d="M20 6 9 17l-5-5" />
                  </svg>
                </span>
                {item}
              </li>
            ))}
          </ul>
        </div>
        <div className="border-t border-[#16181f] bg-[#0b0d12] lg:border-l lg:border-t-0">
          <div className="flex items-center gap-2 border-b border-[#1b1f2a] px-4 py-[11px] text-xs text-[#71717a]">
            <div className="flex gap-[7px]">
              <span className="h-2.5 w-2.5 rounded-full bg-[#ff5f57]" />
              <span className="h-2.5 w-2.5 rounded-full bg-[#febc2e]" />
              <span className="h-2.5 w-2.5 rounded-full bg-[#28c840]" />
            </div>
            <span className="ml-1.5 font-mono text-[#a1a1aa]">
              ProjectsController.cs
            </span>
          </div>
          <pre className="overflow-x-auto p-[22px] font-mono text-[13px] leading-[1.75] text-[#e5e7eb]">
            <code>
              <span className="italic text-[#6b7280]">
                {"// Filter by permission — resolved inside the database"}
              </span>
              {"\n"}
              <span className="text-[#c4b5fd]">var</span>
              {" projects = "}
              <span className="text-[#c4b5fd]">await</span>
              {" db."}
              <span className="text-[#f0abfc]">Projects</span>
              {"\n    ."}
              <span className="text-[#fcd34d]">Where</span>
              {"("}
              <span className="text-[#c4b5fd]">await</span>
              {" fga."}
              <span className="text-[#fcd34d]">BuildFilterAsync</span>
              {"<"}
              <span className="text-[#7dd3fc]">Project</span>
              {">(user."}
              <span className="text-[#f0abfc]">Id</span>
              {", "}
              <span className="text-[#86efac]">&quot;projects.read&quot;</span>
              {"))\n    ."}
              <span className="text-[#fcd34d]">OrderBy</span>
              {"(p => p."}
              <span className="text-[#f0abfc]">Name</span>
              {")."}
              <span className="text-[#fcd34d]">Take</span>
              {"("}
              <span className="text-[#fca5a5]">20</span>
              {")\n    ."}
              <span className="text-[#fcd34d]">ToListAsync</span>
              {"();  "}
              <span className="italic text-[#6b7280]">{"// single query"}</span>
            </code>
          </pre>
        </div>
      </div>

      <div className="mt-16 grid grid-cols-2 gap-8 text-center lg:grid-cols-4 lg:gap-6">
        <Metric n="<1ms" label="Authorization check latency" />
        <Metric n="1" label="NuGet package to install" />
        <Metric n="0" label="External identity vendors" />
        <Metric n="SAML+OIDC" label="Enterprise SSO out of the box" />
      </div>
    </div>
  );
}

function Metric({ n, label }: { n: string; label: string }) {
  return (
    <div>
      <div className="bg-gradient-to-r from-[#4f46e5] to-[#8b7cf6] bg-clip-text text-[30px] font-extrabold tracking-[-0.04em] text-transparent lg:text-[34px]">
        {n}
      </div>
      <div className="mt-1 text-[13.5px] text-muted-foreground">{label}</div>
    </div>
  );
}

/* ---------- CTA ---------- */

function ClosingCta() {
  return (
    <div className="py-20 lg:py-24">
      <div className="mx-auto max-w-[1160px] px-7">
        <div className="relative overflow-hidden rounded-[22px] px-6 py-12 text-center text-white [background:radial-gradient(120%_130%_at_50%_0%,#4f46e5,#312a9c)] lg:px-10 lg:py-[66px]">
          <div
            aria-hidden="true"
            className="pointer-events-none absolute inset-0 [background-image:linear-gradient(rgba(255,255,255,0.06)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,0.06)_1px,transparent_1px)] [background-size:40px_40px] [mask-image:radial-gradient(70%_70%_at_50%_0%,#000,transparent)]"
          />
          <h2 className="relative text-3xl font-extrabold tracking-[-0.03em] lg:text-[38px]">
            Ship enterprise auth this week.
          </h2>
          <p className="relative mx-auto mb-7 mt-3 max-w-[44ch] text-[17px] text-[#dcd8fb]">
            Add SqlOS to your .NET app, point it at SQL Server, and go live with
            SSO and fine-grained authorization — self-hosted, on your terms.
          </p>
          <div className="relative flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center rounded-[9px] bg-white px-4 py-2.5 text-sm font-semibold text-[#4338ca] transition-colors hover:bg-[#f1f0ff]"
            >
              Get started
            </Link>
            <Link
              href="/docs"
              className="inline-flex items-center rounded-[9px] border border-white/35 px-4 py-2.5 text-sm font-semibold text-white transition-colors hover:bg-white/10"
            >
              Read the docs
            </Link>
          </div>
        </div>
      </div>
    </div>
  );
}
