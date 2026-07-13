import Image from "next/image";
import Link from "next/link";
import AsciiType from "@/components/marketing/AsciiType";
import { LoginMini } from "@/components/marketing/fragments";
import InstallCommand from "@/components/marketing/InstallCommand";
import { ArrowIcon, GitHubIcon } from "@/components/icons";

export default function HeroCollage() {
  return (
    <section className="relative overflow-hidden">
      {/* restrained backdrop: one soft accent bloom + hairline grid, fading fast */}
      <div
        className="pointer-events-none absolute inset-x-0 top-0 h-[640px]"
        style={{
          background:
            "radial-gradient(56rem 28rem at 50% -12%, hsl(var(--primary) / 0.08), transparent 65%)",
        }}
        aria-hidden="true"
      />
      <div
        className="pointer-events-none absolute inset-x-0 top-0 h-[420px] [mask-image:linear-gradient(to_bottom,black,transparent)]"
        style={{
          backgroundImage:
            "linear-gradient(to right, hsl(var(--border) / 0.5) 1px, transparent 1px), linear-gradient(to bottom, hsl(var(--border) / 0.5) 1px, transparent 1px)",
          backgroundSize: "72px 72px",
        }}
        aria-hidden="true"
      />

      <div className="relative mx-auto max-w-3xl px-6 pt-24 text-center sm:pt-32">
        <div className="inline-flex items-center gap-2 rounded-full border bg-background px-3.5 py-1.5 text-xs font-medium text-muted-foreground shadow-sm">
          <span className="h-1.5 w-1.5 rounded-full bg-primary" />
          Auth + FGA for .NET
          <span className="text-border">|</span>
          <span className="font-mono text-[11px]">v3.15</span>
        </div>

        <h1 className="mt-8 text-balance text-[clamp(2.6rem,5vw,4.25rem)] font-semibold leading-[1.02] tracking-[-0.045em] text-foreground">
          Enterprise auth, <span className="text-primary">embedded</span> in
          your database.
        </h1>

        <p className="mx-auto mt-6 max-w-xl text-pretty text-base leading-7 text-muted-foreground sm:text-lg">
          Branded login, social auth, SAML SSO, and fine-grained permissions — one
          NuGet package that runs in your app and lives in your database.
        </p>

        <div className="mt-9 flex flex-wrap items-center justify-center gap-3">
          <Link
            href="/docs/getting-started"
            className="inline-flex h-11 items-center gap-2 rounded-lg bg-primary px-5 text-sm font-semibold text-primary-foreground shadow-sm transition-colors hover:bg-primary/90"
          >
            Get started
            <ArrowIcon />
          </Link>
          <Link
            href="/docs"
            className="inline-flex h-11 items-center rounded-lg border bg-background px-5 text-sm font-medium text-foreground shadow-sm transition-colors hover:bg-muted/50"
          >
            Read the docs
          </Link>
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex h-11 items-center gap-2 px-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
          >
            <GitHubIcon className="h-4 w-4" />
            GitHub
          </a>
        </div>

        <div className="mt-5 flex justify-center">
          <InstallCommand />
        </div>
      </div>

      {/* the brand, as a live ASCII field — full-bleed, pointer-reactive */}
      <div className="relative mt-14 h-[clamp(150px,24vw,340px)] w-full sm:mt-16">
        <AsciiType text="SQLOS" cell={13} />
      </div>

      {/* anchored product frame, rising over the field */}
      <div className="relative mx-auto -mt-[clamp(40px,7vw,104px)] max-w-5xl px-6">
        <div className="relative">
          <div className="overflow-hidden rounded-xl border bg-card shadow-[0_32px_80px_-24px_rgb(0_0_0/0.3)] ring-1 ring-black/5">
            <div className="flex items-center gap-3 border-b bg-muted/40 px-4 py-2.5">
              <div className="flex gap-1.5">
                <span className="h-2.5 w-2.5 rounded-full border border-border bg-background" />
                <span className="h-2.5 w-2.5 rounded-full border border-border bg-background" />
                <span className="h-2.5 w-2.5 rounded-full border border-border bg-background" />
              </div>
              <div className="mx-auto flex items-center gap-2 rounded-md border bg-background px-3 py-1">
                <svg
                  className="h-3 w-3 text-muted-foreground/70"
                  viewBox="0 0 24 24"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                >
                  <rect x="4" y="11" width="16" height="10" rx="2" />
                  <path d="M8 11V7a4 4 0 0 1 8 0v4" />
                </svg>
                <span className="font-mono text-[11px] text-muted-foreground">
                  localhost:5001/sqlos
                </span>
              </div>
              <span className="w-10" />
            </div>
            <Image
              src="/docs/dashboard-home.png"
              alt="SqlOS admin dashboard"
              width={2174}
              height={1426}
              priority
              className="h-auto w-full"
            />
          </div>

          {/* one precision fragment, pinned to the frame */}
          <div className="absolute -left-12 bottom-10 hidden rotate-[-1.5deg] xl:block">
            <LoginMini />
          </div>
        </div>
      </div>

      <div className="h-24 sm:h-28" />
    </section>
  );
}
