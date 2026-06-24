import Link from "next/link";
import { Chip } from "@heroui/react";
import { ArrowRight, Github, Terminal } from "lucide-react";

export default function HeroSection() {
  return (
    <section className="relative overflow-hidden px-4 pb-12 pt-24 sm:px-6 sm:pb-16 sm:pt-32">
      <div className="mx-auto grid max-w-[1400px] gap-10 overflow-hidden lg:grid-cols-[minmax(0,0.96fr)_minmax(320px,0.74fr)] lg:items-end">
        <div className="min-w-0 max-w-[360px] sm:max-w-none">
          <Chip
            size="sm"
            variant="soft"
            color="accent"
            className="border border-neon-cyan/30 bg-neon-cyan/10 text-neon-cyan"
          >
            OAuth + AuthPage + SSO + FGA inside ASP.NET
          </Chip>
          <h1 className="mt-7 max-w-full text-balance text-[clamp(2.25rem,10vw,7.2rem)] font-semibold leading-[1.04] text-foreground">
            Ship the auth stack without leaving your app.
          </h1>
          <p className="mt-6 max-w-full text-base leading-7 text-muted-foreground sm:max-w-3xl sm:text-xl sm:leading-8">
            SqlOS gives .NET builders hosted login, social auth, SSO, fine-grained authorization,
            and dashboard UI in one package that runs in your process and stores state in SQL Server.
          </p>
          <div className="mt-8 flex flex-col gap-3 sm:flex-row">
            <Link
              href="#configuration-walkthrough"
              className="inline-flex items-center justify-center gap-2 rounded-md bg-neon-green px-5 py-3 text-sm font-semibold text-background shadow-[0_0_34px_oklch(0.88_0.2_146_/_0.26)] transition-colors hover:bg-neon-cyan"
            >
              Walk the setup
              <ArrowRight className="h-4 w-4" />
            </Link>
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center justify-center gap-2 rounded-md border border-neon-cyan/35 bg-card/55 px-5 py-3 text-sm font-semibold text-neon-cyan transition-colors hover:bg-neon-cyan/10"
            >
              Read docs
              <Terminal className="h-4 w-4" />
            </Link>
            <a
              href="https://github.com/ross-slaney/sqlos"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center justify-center gap-2 rounded-md border border-border bg-background/55 px-5 py-3 text-sm font-semibold text-muted-foreground transition-colors hover:border-neon-green/40 hover:text-neon-green"
            >
              GitHub
              <Github className="h-4 w-4" />
            </a>
          </div>
        </div>

        <div className="neon-panel min-w-0 max-w-[360px] overflow-hidden rounded-lg p-4 sm:max-w-none">
          <div className="flex items-center gap-2 border-b border-border/70 pb-3">
            <span className="h-2.5 w-2.5 rounded-full bg-neon-coral" />
            <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow" />
            <span className="h-2.5 w-2.5 rounded-full bg-neon-green" />
            <span className="ml-2 font-mono text-xs text-muted-foreground">dotnet shell</span>
          </div>
          <div className="space-y-3 overflow-x-auto pt-4 font-mono text-xs leading-7 sm:text-sm">
            <p className="text-neon-green">$ dotnet add package SqlOS</p>
            <p className="text-foreground/90">builder.AddSqlOS&lt;AppDbContext&gt;(...);</p>
            <p className="text-foreground/90">app.MapSqlOS();</p>
            <p className="text-neon-cyan">/sqlos/auth/* online</p>
            <p className="text-neon-cyan">/sqlos/admin/fga online</p>
          </div>
        </div>
      </div>
    </section>
  );
}
