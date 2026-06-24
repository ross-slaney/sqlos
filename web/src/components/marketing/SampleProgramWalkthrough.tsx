"use client";

import Link from "next/link";
import { Button, Chip, ProgressBar } from "@heroui/react";
import {
  ArrowRight,
  Braces,
  Database,
  FileCode2,
  Fingerprint,
  KeyRound,
  Mail,
  Route,
  ServerCog,
  ShieldCheck,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";

const sampleSteps = [
  {
    id: "host",
    title: "Start with a normal ASP.NET host",
    label: "builder",
    source: "Program.cs:17",
    body: "The sample begins as a regular WebApplicationBuilder. SqlOS is added to the host instead of replacing the app pipeline.",
    code: `var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExampleWebOptions>(
    builder.Configuration.GetSection("ExampleFrontend"));`,
    result: "The application stays an ASP.NET Core app.",
    icon: FileCode2,
  },
  {
    id: "database",
    title: "Use the app database context",
    label: "EF Core",
    source: "Program.cs:25",
    body: "The sample reads the SQL Server connection string and registers the same EF context the rest of the app uses.",
    code: `var connectionString =
    builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<ExampleAppDbContext>(options =>
    options.UseSqlServer(connectionString));`,
    result: "App data and SqlOS state live behind the configured SQL Server boundary.",
    icon: Database,
  },
  {
    id: "sqlos",
    title: "Register SqlOS once",
    label: "AddSqlOS",
    source: "Program.cs:29",
    body: "The central setup call wires the dashboard, authorization server, FGA services, seeded policy, and runtime options.",
    code: `builder.AddSqlOS<ExampleAppDbContext>(options =>
{
    options.DashboardBasePath = "/sqlos";

    var auth = options.AuthServer;
    auth.Issuer =
        builder.Configuration["SqlOS:Issuer"]
        ?? "https://localhost/sqlos/auth";
});`,
    result: "This is the main SqlOS host integration point.",
    icon: ServerCog,
  },
  {
    id: "authpage",
    title: "Define the sign-in surface",
    label: "AuthPage",
    source: "Program.cs:105",
    body: "The sample seeds the hosted login page, enabled credential types, browser clients, and optional social identity providers.",
    code: `auth.SeedAuthPage(page =>
{
    page.PageTitle = "Sign in";
    page.PageSubtitle =
        "Use the hosted SqlOS auth page to sign in.";
    page.EnablePasswordSignup = true;
    page.EnabledCredentialTypes =
        ["password", "email_otp"];
});

auth.SeedBrowserClient(
    "example-web",
    "Example Web Client",
    "http://localhost:3000/auth/callback");`,
    result: "OAuth clients and hosted login behavior are declared in code.",
    icon: KeyRound,
  },
  {
    id: "otp",
    title: "Attach delivery and MFA settings",
    label: "email + MFA",
    source: "Program.cs:76",
    body: "Email OTP, phone OTP, and MFA are configured from host settings so a fresh checkout can still boot without secrets.",
    code: `auth.ConfigureEmailOtp(emailOtp =>
{
    emailOtp.AzureCommunicationServicesConnectionString =
        emailConnectionString;
    emailOtp.FromAddress = emailFromAddress;
    emailOtp.ApplicationName = "SqlOS Example";
});

auth.ConfigureMfa(mfa =>
{
    mfa.Enabled = true;
    mfa.AllowUserSelfEnrollmentByDefault = true;
});`,
    result: "Credential policy stays explicit and environment driven.",
    icon: Mail,
  },
  {
    id: "fga",
    title: "Seed authorization policy",
    label: "FGA",
    source: "Program.cs:244",
    body: "The sample creates resource types, permissions, roles, and role-permission links beside the auth configuration.",
    code: `options.Fga.Seed(seed =>
{
    seed.ResourceType("organization", "Organization");
    seed.ResourceType("workspace", "Workspace");

    seed.Permission(
        "perm_workspace_view",
        "workspace.view",
        "View workspaces",
        "workspace");

    seed.Role("role_org_admin", "org_admin", "Org Admin");
    seed.RolePermission("org_admin", "workspace.view");
});`,
    result: "The authorization model is data-backed, but the baseline policy is readable in the host program.",
    icon: ShieldCheck,
  },
  {
    id: "routes",
    title: "Map the SqlOS endpoints",
    label: "routes",
    source: "Program.cs:308",
    body: "After the app is built, the sample maps SqlOS and then maps the example API endpoints around it.",
    code: `var app = builder.Build();

app.MapSqlOS();

app.UseSwagger();
app.UseCors("example-frontend");
app.UseExampleBearerTokenMiddleware();

app.MapExampleAuthEndpoints();
app.MapExampleEndpoints();

app.Run();`,
    result: "The dashboard, auth endpoints, and FGA admin routes are part of the ASP.NET route table.",
    icon: Route,
  },
] as const;

const routeNotes = [
  { path: "/sqlos", label: "dashboard shell" },
  { path: "/sqlos/admin/auth", label: "auth admin UI" },
  { path: "/sqlos/admin/fga", label: "FGA admin UI" },
  { path: "/sqlos/auth/*", label: "OAuth and hosted AuthPage" },
];

export default function SampleProgramWalkthrough() {
  const [activeIndex, setActiveIndex] = useState(0);
  const stepRefs = useRef<Array<HTMLDivElement | null>>([]);
  const activeStep = sampleSteps[activeIndex] ?? sampleSteps[0];
  const progress = useMemo(
    () => Math.round(((activeIndex + 1) / sampleSteps.length) * 100),
    [activeIndex]
  );
  const ActiveIcon = activeStep.icon;

  useEffect(() => {
    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (prefersReducedMotion) {
      return;
    }

    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((entry) => entry.isIntersecting)
          .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];

        if (!visible) {
          return;
        }

        const index = Number((visible.target as HTMLElement).dataset.stepIndex);
        if (!Number.isNaN(index)) {
          setActiveIndex(index);
        }
      },
      {
        rootMargin: "-30% 0px -45% 0px",
        threshold: [0.24, 0.5, 0.72],
      }
    );

    stepRefs.current.forEach((node) => {
      if (node) {
        observer.observe(node);
      }
    });

    return () => observer.disconnect();
  }, []);

  const focusStep = (index: number) => {
    setActiveIndex(index);
    stepRefs.current[index]?.scrollIntoView({ behavior: "smooth", block: "center" });
  };

  return (
    <div className="relative overflow-hidden">
      <section className="px-4 pb-12 pt-20 sm:px-6 sm:pb-16 sm:pt-28">
        <div className="mx-0 grid max-w-[360px] gap-10 sm:mx-auto sm:max-w-[1400px] lg:grid-cols-[minmax(0,0.62fr)_minmax(420px,0.88fr)] lg:items-end">
          <div className="max-w-full sm:max-w-[720px]">
            <Chip
              size="sm"
              variant="soft"
              color="accent"
              className="max-w-full overflow-hidden border border-neon-cyan/30 bg-neon-cyan/10 text-neon-cyan"
            >
              examples/SqlOS.Example.Api/Program.cs
            </Chip>
            <h1 className="mt-6 text-balance text-[clamp(2.4rem,7vw,6.4rem)] font-semibold leading-[1.02] text-foreground">
              Read the SqlOS host program.
            </h1>
            <p className="mt-6 max-w-2xl text-base leading-7 text-muted-foreground sm:text-xl sm:leading-8">
              SqlOS is a .NET package that adds OAuth endpoints, a hosted AuthPage,
              fine-grained authorization, dashboard routes, and SQL-backed state to an
              ASP.NET application. The sample program shows the integration points directly.
            </p>
            <div className="mt-8 flex flex-col gap-3 sm:flex-row">
              <Link
                href="#program-walkthrough"
                className="inline-flex items-center justify-center gap-2 rounded-md bg-neon-green px-5 py-3 text-sm font-semibold text-background shadow-[0_0_34px_oklch(0.88_0.2_146_/_0.22)] transition-colors hover:bg-neon-cyan"
              >
                Walk the program
                <ArrowRight className="h-4 w-4" />
              </Link>
              <Link
                href="/docs/guides/configuration"
                className="inline-flex items-center justify-center gap-2 rounded-md border border-neon-cyan/35 bg-card/55 px-5 py-3 text-sm font-semibold text-neon-cyan transition-colors hover:bg-neon-cyan/10"
              >
                Configuration guide
              </Link>
            </div>
          </div>

          <div className="neon-panel relative min-w-0 max-w-full overflow-hidden rounded-lg p-4 sm:p-5">
            <div className="pointer-events-none absolute inset-x-[-20%] top-0 h-px animate-[neonSweep_4s_ease-in-out_infinite] bg-neon-cyan/80 shadow-[0_0_24px_oklch(0.82_0.17_200_/_0.7)]" />
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/70 pb-4">
              <div className="flex items-center gap-2">
                <span className="h-2.5 w-2.5 rounded-full bg-neon-coral/90" />
                <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow/90" />
                <span className="h-2.5 w-2.5 rounded-full bg-neon-green/90" />
                <span className="ml-2 font-mono text-xs text-muted-foreground">sample host</span>
              </div>
              <Chip
                size="sm"
                variant="soft"
                color="success"
                className="max-w-full border border-neon-green/25 bg-neon-green/10 text-neon-green"
              >
                ASP.NET + EF + SQL
              </Chip>
            </div>

            <div className="grid gap-3 pt-5 sm:grid-cols-2">
              {[
                ["Host", "WebApplicationBuilder"],
                ["State", "SQL Server"],
                ["Model", "ExampleAppDbContext"],
                ["Routes", "/sqlos/*"],
              ].map(([label, value]) => (
                <div
                  key={label}
                  className="rounded-lg border border-border/70 bg-background/68 p-4"
                >
                  <p className="font-mono text-xs text-neon-green">{label}</p>
                  <p className="mt-1 text-sm font-semibold text-foreground">{value}</p>
                </div>
              ))}
            </div>
          </div>
        </div>
      </section>

      <section id="program-walkthrough" className="px-4 pb-24 sm:px-6">
        <div className="mx-0 grid max-w-[360px] gap-8 sm:mx-auto sm:max-w-[1400px] lg:grid-cols-[minmax(0,0.86fr)_minmax(420px,1.14fr)] lg:items-start">
          <div className="min-w-0 space-y-4">
            <div className="mb-2 border-b border-border/70 pb-5">
              <p className="font-mono text-xs uppercase text-neon-cyan">Program map</p>
              <h2 className="mt-2 text-2xl font-semibold text-foreground sm:text-3xl">
                Seven responsibilities in the sample host
              </h2>
            </div>

            {sampleSteps.map((step, index) => {
              const Icon = step.icon;
              const isActive = activeIndex === index;

              return (
                <div
                  key={step.id}
                  ref={(node) => {
                    stepRefs.current[index] = node;
                  }}
                  data-step-index={index}
                  className={[
                    "rounded-lg border p-4 transition-all duration-300 sm:p-5",
                    isActive
                      ? "border-neon-cyan/60 bg-neon-cyan/10 shadow-[0_0_34px_oklch(0.82_0.17_200_/_0.14)]"
                      : "border-border/70 bg-card/45 hover:border-neon-green/40 hover:bg-card/70",
                  ].join(" ")}
                >
                  <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                    <div className="flex min-w-0 gap-4">
                      <span
                        className={[
                          "mt-1 flex h-10 w-10 shrink-0 items-center justify-center rounded-md border",
                          isActive
                            ? "border-neon-cyan/50 bg-neon-cyan/15 text-neon-cyan"
                            : "border-border bg-background/60 text-muted-foreground",
                        ].join(" ")}
                      >
                        <Icon className="h-5 w-5" />
                      </span>
                      <div className="min-w-0">
                        <p className="font-mono text-xs text-neon-green">
                          {String(index + 1).padStart(2, "0")} / {step.label} / {step.source}
                        </p>
                        <h3 className="mt-1 text-xl font-semibold text-foreground">{step.title}</h3>
                        <p className="mt-2 max-w-xl text-sm leading-6 text-muted-foreground">
                          {step.body}
                        </p>
                      </div>
                    </div>
                    <Button
                      size="sm"
                      variant={isActive ? "primary" : "outline"}
                      className={
                        isActive
                          ? "bg-neon-green text-background"
                          : "border-neon-cyan/25 bg-transparent text-neon-cyan"
                      }
                      onPress={() => focusStep(index)}
                    >
                      Focus
                    </Button>
                  </div>
                </div>
              );
            })}
          </div>

          <div className="min-w-0 max-w-full lg:sticky lg:top-24">
            <div className="neon-panel animate-[documentPulse_5s_ease-in-out_infinite] overflow-hidden rounded-lg p-4 sm:p-5">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/70 pb-4">
                <div className="flex items-center gap-2">
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-coral/90" />
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow/90" />
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-green/90" />
                  <span className="ml-2 font-mono text-xs text-muted-foreground">
                    {activeStep.source}
                  </span>
                </div>
                <Chip
                  size="sm"
                  variant="soft"
                  color="accent"
                  className="border border-neon-cyan/25 bg-neon-cyan/10 text-neon-cyan"
                >
                  {progress}% read
                </Chip>
              </div>

              <ProgressBar.Root
                aria-label="Sample program walkthrough progress"
                value={progress}
                className="mt-4 w-full"
              >
                <ProgressBar.Track className="h-1.5 rounded-full bg-white/10">
                  <ProgressBar.Fill className="h-full rounded-full bg-neon-green shadow-[0_0_18px_oklch(0.88_0.2_146_/_0.6)]" />
                </ProgressBar.Track>
              </ProgressBar.Root>

              <div className="mt-6 rounded-lg border border-neon-cyan/20 bg-background/78 p-4">
                <div className="flex items-center gap-3">
                  <span className="flex h-10 w-10 items-center justify-center rounded-md bg-neon-cyan/12 text-neon-cyan">
                    <ActiveIcon className="h-5 w-5" />
                  </span>
                  <div>
                    <p className="font-mono text-xs text-neon-green">
                      step {String(activeIndex + 1).padStart(2, "0")}
                    </p>
                    <h3 className="text-xl font-semibold text-foreground">{activeStep.title}</h3>
                  </div>
                </div>
                <p className="mt-4 text-sm leading-6 text-muted-foreground">{activeStep.result}</p>
              </div>

              <pre className="mt-4 min-h-[330px] overflow-x-auto rounded-lg border border-neon-green/20 bg-[oklch(0.055_0.022_248)] p-4 font-mono text-[12px] leading-6 text-foreground shadow-[inset_0_0_30px_oklch(0_0_0_/_0.28)] sm:text-[13px]">
                <code>{activeStep.code}</code>
              </pre>

              <div className="mt-4 grid gap-2 rounded-lg border border-border/70 bg-card/55 p-4">
                <div className="flex items-center gap-2 text-sm font-semibold text-foreground">
                  <Fingerprint className="h-4 w-4 text-neon-green" />
                  Paths created by the sample
                </div>
                <div className="grid gap-2">
                  {routeNotes.map((note) => (
                    <div
                      key={note.path}
                      className="flex flex-col gap-1 rounded-md border border-border/60 bg-background/52 px-3 py-2 sm:flex-row sm:items-center sm:justify-between"
                    >
                      <code className="font-mono text-xs text-neon-cyan">{note.path}</code>
                      <span className="text-xs text-muted-foreground">{note.label}</span>
                    </div>
                  ))}
                </div>
              </div>

              <div className="mt-4 flex flex-col gap-3 sm:flex-row">
                <Link
                  href="/docs/guides/configuration"
                  className="inline-flex flex-1 items-center justify-center rounded-md border border-neon-cyan/35 px-3 py-2 text-sm font-semibold text-neon-cyan transition-colors hover:bg-neon-cyan/10"
                >
                  Open configuration guide
                </Link>
                <a
                  href="https://github.com/ross-slaney/sqlos/blob/main/examples/SqlOS.Example.Api/Program.cs"
                  target="_blank"
                  rel="noopener noreferrer"
                  className="inline-flex flex-1 items-center justify-center gap-2 rounded-md border border-border px-3 py-2 text-sm font-semibold text-muted-foreground transition-colors hover:border-neon-green/40 hover:text-neon-green"
                >
                  View source
                  <Braces className="h-4 w-4" />
                </a>
              </div>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}
