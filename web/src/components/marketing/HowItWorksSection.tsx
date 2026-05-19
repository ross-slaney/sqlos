import { fgaCode, performanceStats } from "@/components/marketing/constants";

export default function HowItWorksSection() {
  return (
    <section className="border-t bg-zinc-950 px-6 py-20 text-zinc-50 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[0.85fr_1.15fr] lg:gap-16">
          <div>
            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-zinc-400">
              How it works
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] sm:text-4xl">
              Authorization is a database query, not an API call
            </h2>
            <p className="mt-5 text-base leading-7 text-zinc-400">
              Most auth systems make you choose: fetch data then check permissions, or call an external API
              per resource. SqlOS folds access checks into your SQL execution plan — filtering, sorting,
              pagination, and authorization in a single query.
            </p>
            <div className="mt-6 grid grid-cols-3 gap-3">
              {performanceStats.map((stat) => (
                <div
                  key={stat.label}
                  className="rounded-xl border border-white/10 bg-white/5 px-3 py-4 text-center"
                >
                  <div className="font-mono text-sm font-bold text-white sm:text-base">
                    {stat.value}
                  </div>
                  <div className="mt-1 text-[10px] uppercase tracking-[0.12em] text-zinc-400">
                    {stat.label}
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="overflow-hidden rounded-2xl border border-white/10 bg-white/5 shadow-lg">
            <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-3">
              <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
              <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
              <span className="h-2.5 w-2.5 rounded-full bg-white/20" />
              <span className="ml-3 text-[11px] text-zinc-400">ProjectsEndpoint.cs</span>
            </div>
            <pre className="overflow-x-auto px-4 py-5 font-mono text-[11px] leading-7 text-zinc-300 sm:px-5 sm:text-[13px]">
              <code>{fgaCode}</code>
            </pre>
          </div>
        </div>
      </div>
    </section>
  );
}
