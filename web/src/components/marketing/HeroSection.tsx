import Link from "next/link";
import ProductScreenshot from "@/components/ProductScreenshot";
import { ArrowIcon, GitHubIcon } from "@/components/icons";

export default function HeroSection() {
  return (
    <section className="relative overflow-hidden px-6 pb-20 pt-24 sm:pb-28 sm:pt-32">
      <div className="absolute inset-0 -z-10 bg-gradient-to-b from-primary/5 via-background to-background" />
      <div className="mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[1.05fr_0.95fr] lg:gap-16">
          <div>
            <span className="inline-flex items-center rounded-full border border-primary/20 bg-primary/5 px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-primary">
              Auth + FGA inside EF Core
            </span>
            <h1 className="mt-6 text-[clamp(2.6rem,5vw,4.4rem)] font-semibold leading-[1.02] tracking-[-0.05em] text-foreground">
              Enterprise auth for your .NET app.
            </h1>
            <p className="mt-5 max-w-xl text-base leading-7 text-muted-foreground sm:text-lg">
              OAuth server, branded login, social auth, SAML SSO, and fine-grained authorization in one
              NuGet package — in your process, in your SQL Server.
            </p>
            <div className="mt-8 flex flex-wrap items-center gap-3">
              <Link
                href="/docs/getting-started"
                className="inline-flex items-center gap-2 rounded-md bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
              >
                Get started
                <ArrowIcon />
              </Link>
              <Link
                href="/docs"
                className="inline-flex items-center gap-2 rounded-md border bg-background px-5 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-accent"
              >
                Read the docs
              </Link>
              <a
                href="https://github.com/ross-slaney/sqlos"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 px-2 py-2 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground"
              >
                <GitHubIcon className="h-4 w-4" />
                GitHub
              </a>
            </div>
          </div>

          <ProductScreenshot
            src="/docs/dashboard-home.png"
            alt="SqlOS admin dashboard"
            priority
          />
        </div>
      </div>
    </section>
  );
}
