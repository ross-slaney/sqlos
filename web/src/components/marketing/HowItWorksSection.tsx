import { fgaCode, performanceStats } from "@/components/marketing/constants";

export default function HowItWorksSection() {
  return (
    <section className="border-t border-border/70 bg-background/68 px-6 py-20 text-foreground sm:py-24">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[0.85fr_1.15fr] lg:gap-16">
          <div>
            <p className="font-mono text-[11px] font-semibold uppercase text-neon-green">
              How it works
            </p>
            <h2 className="mt-3 text-3xl font-semibold sm:text-4xl">
              Authorization is a database query, not an API call
            </h2>
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              Most auth systems make you choose: fetch data then check permissions, or call an external API
              per resource. SqlOS folds access checks into your SQL execution plan, so filtering, sorting,
              pagination, and authorization in a single query.
            </p>
            <div className="mt-6 grid grid-cols-3 gap-3">
              {performanceStats.map((stat) => (
                <div
                  key={stat.label}
                  className="rounded-lg border border-neon-cyan/20 bg-card/60 px-3 py-4 text-center"
                >
                  <div className="font-mono text-sm font-bold text-neon-cyan sm:text-base">
                    {stat.value}
                  </div>
                  <div className="mt-1 text-[10px] uppercase text-muted-foreground">
                    {stat.label}
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="overflow-hidden rounded-lg border border-neon-cyan/25 bg-card/75 shadow-[0_18px_70px_oklch(0_0_0_/_0.34)]">
            <div className="flex items-center gap-1.5 border-b border-border/70 px-4 py-3">
              <span className="h-2.5 w-2.5 rounded-full bg-neon-coral/80" />
              <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow/80" />
              <span className="h-2.5 w-2.5 rounded-full bg-neon-green/80" />
              <span className="ml-3 font-mono text-[11px] text-muted-foreground">ProjectsEndpoint.cs</span>
            </div>
            <pre className="overflow-x-auto bg-[oklch(0.055_0.022_248)] px-4 py-5 font-mono text-[11px] leading-7 text-foreground sm:px-5 sm:text-[13px]">
              <code>{fgaCode}</code>
            </pre>
          </div>
        </div>
      </div>
    </section>
  );
}
