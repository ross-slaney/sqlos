import Link from "next/link";
import AsciiShader from "@/components/marketing/AsciiShader";
import InstallCommand from "@/components/marketing/InstallCommand";
import ProductScreenshot from "@/components/ProductScreenshot";
import { ArrowIcon, GitHubIcon } from "@/components/icons";

const heroTokens = ["OAuth 2.0 + PKCE", "SAML SSO", "Social login", "FGA engine", "One NuGet package"];

export default function HeroSection() {
  return (
    <section className="relative overflow-hidden">
      {/* real-time ASCII / dither field (WebGL) */}
      <div className="absolute inset-0 -z-10">
        <AsciiShader cell={11} intensity={0.8} />
        <div className="absolute inset-0 bg-gradient-to-b from-background via-transparent to-background" />
        <div className="absolute inset-0 bg-gradient-to-r from-background/90 via-background/40 to-transparent" />
      </div>

      <div className="mx-auto flex min-h-[max(640px,88svh)] max-w-6xl flex-col justify-center px-6 pb-16 pt-28 sm:pt-32">
        <div className="max-w-3xl">
          <div className="inline-flex items-center gap-2.5 rounded-full border bg-background/70 px-3.5 py-1.5 font-mono text-[11px] uppercase tracking-[0.18em] text-muted-foreground backdrop-blur">
            <span className="relative flex h-1.5 w-1.5">
              <span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-primary opacity-60" />
              <span className="relative inline-flex h-1.5 w-1.5 rounded-full bg-primary" />
            </span>
            Auth + FGA inside EF Core
          </div>

          <h1 className="mt-7 text-balance text-[clamp(2.9rem,7vw,5.4rem)] font-semibold leading-[0.98] tracking-[-0.055em] text-foreground">
            Enterprise auth,
            <br />
            <span className="text-primary">compiled into your SQL.</span>
          </h1>

          <p className="mt-6 max-w-xl text-pretty text-base leading-7 text-muted-foreground sm:text-lg">
            OAuth server, branded login, social auth, SAML SSO, and fine-grained
            authorization in one NuGet package — in your process, in your SQL Server.
          </p>

          <div className="mt-9 flex flex-wrap items-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center gap-2 rounded-lg bg-primary px-5 py-2.5 text-sm font-semibold text-primary-foreground shadow-lg shadow-primary/20 transition-all hover:bg-primary/90 hover:shadow-primary/30"
            >
              Get started
              <ArrowIcon />
            </Link>
            <InstallCommand />
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

          <div className="mt-12 flex flex-wrap items-center gap-x-5 gap-y-2 font-mono text-[11px] uppercase tracking-[0.16em] text-muted-foreground/80">
            {heroTokens.map((token, i) => (
              <span key={token} className="inline-flex items-center gap-5">
                {i > 0 && <span className="text-primary/50">/</span>}
                {token}
              </span>
            ))}
          </div>
        </div>
      </div>

      {/* dashboard peek, bleeding into the next section */}
      <div className="relative mx-auto max-w-5xl px-6">
        <div className="pointer-events-none absolute -inset-x-8 bottom-0 top-1/3 -z-10 bg-gradient-to-t from-background to-transparent" />
        <ProductScreenshot
          src="/docs/dashboard-home.png"
          alt="SqlOS admin dashboard"
          priority
          className="rounded-b-none border-b-0 shadow-2xl"
        />
      </div>
    </section>
  );
}
