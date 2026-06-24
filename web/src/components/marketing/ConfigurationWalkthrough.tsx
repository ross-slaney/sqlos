"use client";

import Link from "next/link";
import { Button, Chip, ProgressBar } from "@heroui/react";
import {
  Braces,
  Database,
  KeyRound,
  LockKeyhole,
  Mail,
  Map,
  Route,
  ShieldCheck,
  TerminalSquare,
  Workflow,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";

const configurationSteps = [
  {
    id: "host-registration",
    title: "Host registration",
    label: "AddSqlOS",
    body: "Register SqlOS in the ASP.NET builder so OAuth, hosted login, dashboard, and FGA services are wired with the host.",
    code: `builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.Issuer =
        "https://app.example.com/sqlos/auth";
    options.AuthServer.PublicOrigin =
        "https://app.example.com";
});`,
    icon: TerminalSquare,
  },
  {
    id: "ef-model",
    title: "EF model registration",
    label: "UseSqlOS",
    body: "Attach SqlOS tables and FGA functions to the same EF model your app already uses.",
    code: `protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    modelBuilder.UseSqlOS(GetType());
    base.OnModelCreating(modelBuilder);
}`,
    icon: Database,
  },
  {
    id: "routes",
    title: "Route mapping",
    label: "MapSqlOS",
    body: "Expose the dashboard, hosted AuthPage, OAuth endpoints, and admin surfaces after the app is built.",
    code: `var app = builder.Build();

app.MapSqlOS();
app.MapGet("/api/health", () => Results.Ok());

app.Run();`,
    icon: Route,
  },
  {
    id: "owned-app",
    title: "Owned app setup",
    label: "Seed client",
    body: "Seed the web app client and the hosted page copy your users see during sign in.",
    code: `options.AuthServer.SeedAuthPage(page =>
{
    page.PageTitle = "Sign in";
    page.PageSubtitle = "Secure your owned app first.";
});

options.AuthServer.SeedOwnedWebApp(
    "web",
    "Main Web App",
    "https://app.example.com/auth/callback");`,
    icon: KeyRound,
  },
  {
    id: "fga-seeding",
    title: "FGA seeding",
    label: "Roles",
    body: "Define resource types, permissions, roles, and role-permission links beside auth setup.",
    code: `options.Fga.Seed(seed =>
{
    seed.ResourceType("workspace", "Workspace");
    seed.Permission(
        "perm_workspace_view",
        "workspace.view",
        "View workspace",
        "workspace");
    seed.Role("role_workspace_admin",
        "workspace_admin",
        "Workspace Admin");
});`,
    icon: ShieldCheck,
  },
  {
    id: "onboarding",
    title: "Client onboarding modes",
    label: "CIMD",
    body: "Choose seeded owned apps, portable clients discovered by metadata URL, or DCR compatibility.",
    code: `options.AuthServer.EnablePortableMcpClients(registration =>
{
    registration.Cimd.TrustedHosts.Add(
        "clients.example.com");
});`,
    icon: Workflow,
  },
  {
    id: "email-otp",
    title: "Email OTP and invitations",
    label: "OTP",
    body: "Point the AuthPage at your sender so passwordless sign in and invitations are ready.",
    code: `options.AuthServer.ConfigureEmailOtp(email =>
{
    email.AzureCommunicationServicesConnectionString =
        builder.Configuration[
            "SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"];
    email.FromAddress =
        builder.Configuration["SqlOS:EmailOtp:FromAddress"];
    email.ApplicationName = "My App";
});`,
    icon: Mail,
  },
  {
    id: "dashboard-password",
    title: "Dashboard password",
    label: "Admin",
    body: "Keep `/sqlos` and `/sqlos/admin` behind an explicit production access boundary.",
    code: `{
  "SqlOS": {
    "Dashboard": {
      "AuthMode": "Password",
      "Password": "your-strong-password"
    }
  }
}`,
    icon: LockKeyhole,
  },
  {
    id: "paths",
    title: "Dashboard paths",
    label: "Routes",
    body: "Know which paths serve the dashboard shell, auth admin, FGA admin, and OAuth/AuthPage endpoints.",
    code: `/sqlos              dashboard shell
/sqlos/admin/auth   auth admin UI
/sqlos/admin/fga    FGA admin UI
/sqlos/auth/*       OAuth and hosted AuthPage`,
    icon: Map,
  },
] as const;

export default function ConfigurationWalkthrough() {
  const [activeIndex, setActiveIndex] = useState(0);
  const stepRefs = useRef<Array<HTMLDivElement | null>>([]);
  const activeStep = configurationSteps[activeIndex] ?? configurationSteps[0];
  const progress = useMemo(
    () => Math.round(((activeIndex + 1) / configurationSteps.length) * 100),
    [activeIndex]
  );

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

        const nextIndex = Number((visible.target as HTMLElement).dataset.stepIndex);
        if (!Number.isNaN(nextIndex)) {
          setActiveIndex(nextIndex);
        }
      },
      {
        rootMargin: "-28% 0px -48% 0px",
        threshold: [0.2, 0.45, 0.7],
      }
    );

    stepRefs.current.forEach((node) => {
      if (node) {
        observer.observe(node);
      }
    });

    return () => observer.disconnect();
  }, []);

  const jumpToStep = (index: number) => {
    setActiveIndex(index);
    stepRefs.current[index]?.scrollIntoView({ behavior: "smooth", block: "center" });
  };

  return (
    <section id="configuration-walkthrough" className="relative px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-[1400px]">
        <div className="mx-auto max-w-3xl text-center">
          <Chip
            size="sm"
            variant="soft"
            color="accent"
            className="max-w-full overflow-hidden border border-neon-cyan/30 bg-neon-cyan/10 text-neon-cyan"
          >
            web/content/docs/guides/configuration.mdx
          </Chip>
          <h2 className="mt-5 text-balance text-3xl font-semibold leading-tight text-foreground sm:text-5xl">
            The setup guide becomes the interface.
          </h2>
          <p className="mt-4 text-base leading-7 text-muted-foreground sm:text-lg">
            Scroll the document and watch SqlOS move from package wiring to AuthPage, FGA, and
            dashboard routes.
          </p>
        </div>

        <div className="mt-12 grid min-w-0 gap-8 overflow-hidden lg:grid-cols-[minmax(0,0.92fr)_minmax(420px,1.08fr)] lg:items-start">
          <div className="min-w-0 space-y-4">
            {configurationSteps.map((step, index) => {
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
                    <div className="flex gap-4">
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
                      <div>
                        <p className="font-mono text-xs text-neon-green">
                          {String(index + 1).padStart(2, "0")} / {step.label}
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
                      onPress={() => jumpToStep(index)}
                    >
                      Focus
                    </Button>
                  </div>
                </div>
              );
            })}
          </div>

          <div className="min-w-0 lg:sticky lg:top-24">
            <div className="neon-panel relative animate-[documentPulse_5s_ease-in-out_infinite] overflow-hidden rounded-lg p-4 sm:p-5">
              <div className="pointer-events-none absolute inset-x-[-20%] top-0 h-px animate-[neonSweep_4s_ease-in-out_infinite] bg-neon-cyan/80 shadow-[0_0_24px_oklch(0.82_0.17_200_/_0.7)]" />
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/70 pb-4">
                <div className="flex items-center gap-2">
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-coral/90" />
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow/90" />
                  <span className="h-2.5 w-2.5 rounded-full bg-neon-green/90" />
                  <span className="ml-2 font-mono text-xs text-muted-foreground">configuration.mdx</span>
                </div>
                <Chip
                  size="sm"
                  variant="soft"
                  color="success"
                  className="border border-neon-green/30 bg-neon-green/10 text-neon-green"
                >
                  {progress}% wired
                </Chip>
              </div>

              <div className="mt-4">
                <ProgressBar.Root
                  aria-label="Configuration guide progress"
                  value={progress}
                  className="w-full"
                >
                  <ProgressBar.Track className="h-1.5 rounded-full bg-white/10">
                    <ProgressBar.Fill className="h-full rounded-full bg-neon-green shadow-[0_0_18px_oklch(0.88_0.2_146_/_0.6)]" />
                  </ProgressBar.Track>
                </ProgressBar.Root>
              </div>

              <div className="mt-6 grid gap-4">
                <div className="rounded-lg border border-neon-cyan/20 bg-background/78 p-4">
                  <div className="flex items-center gap-3">
                    <span className="flex h-9 w-9 items-center justify-center rounded-md bg-neon-cyan/12 text-neon-cyan">
                      <Braces className="h-5 w-5" />
                    </span>
                    <div>
                      <p className="font-mono text-xs text-neon-green">
                        step {String(activeIndex + 1).padStart(2, "0")}
                      </p>
                      <h3 className="text-xl font-semibold text-foreground">{activeStep.title}</h3>
                    </div>
                  </div>
                  <p className="mt-4 text-sm leading-6 text-muted-foreground">{activeStep.body}</p>
                </div>

                <pre className="min-h-[300px] overflow-x-auto rounded-lg border border-neon-green/20 bg-[oklch(0.055_0.022_248)] p-4 font-mono text-[12px] leading-6 text-foreground shadow-[inset_0_0_30px_oklch(0_0_0_/_0.28)] sm:text-[13px]">
                  <code>{activeStep.code}</code>
                </pre>

                <div className="flex flex-col gap-3 rounded-lg border border-border/70 bg-card/55 p-4 sm:flex-row sm:items-center sm:justify-between">
                  <p className="text-sm leading-6 text-muted-foreground">
                    Full guide stays in the docs. This surface makes the setup sequence feel live.
                  </p>
                  <Link
                    href="/docs/guides/configuration"
                    className="inline-flex items-center justify-center rounded-md border border-neon-cyan/35 px-3 py-2 text-sm font-semibold text-neon-cyan transition-colors hover:bg-neon-cyan/10"
                  >
                    Open guide
                  </Link>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
