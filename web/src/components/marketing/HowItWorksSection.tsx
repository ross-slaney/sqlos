import SectionHeading from "@/components/marketing/SectionHeading";
import { fgaCode, performanceStats } from "@/components/marketing/constants";

export default function HowItWorksSection() {
  return (
    <section className="relative border-t bg-zinc-950 px-6 py-24 text-zinc-50 sm:py-28">
      <div
        className="pointer-events-none absolute inset-0 opacity-[0.04]"
        style={{
          backgroundImage:
            "repeating-linear-gradient(0deg, transparent, transparent 3px, currentColor 3px, currentColor 4px)",
        }}
        aria-hidden="true"
      />
      <div className="relative mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-16">
          <div>
            <SectionHeading
              dark
              index="01"
              eyebrow="How it works"
              title="Authorization is a database query, not an API call"
              description="Most auth systems make you choose: fetch data then check permissions, or call an external API per resource. SqlOS folds access checks into your SQL execution plan — filtering, sorting, pagination, and authorization in a single query."
            />
            <div className="mt-8 grid grid-cols-3 divide-x divide-white/10 rounded-xl border border-white/10 bg-white/[0.03]">
              {performanceStats.map((stat) => (
                <div key={stat.label} className="px-3 py-5 text-center">
                  <div className="font-mono text-base font-bold text-white sm:text-lg">
                    {stat.value}
                  </div>
                  <div className="mt-1.5 font-mono text-[10px] uppercase tracking-[0.12em] text-zinc-500">
                    {stat.label}
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="overflow-hidden rounded-2xl border border-white/10 bg-black/40 shadow-2xl">
            <div className="flex items-center justify-between border-b border-white/10 px-4 py-3">
              <div className="flex items-center gap-1.5">
                <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
                <span className="h-2.5 w-2.5 rounded-full bg-white/15" />
                <span className="ml-3 font-mono text-[11px] text-zinc-500">
                  ProjectsEndpoint.cs
                </span>
              </div>
              <span className="font-mono text-[10px] uppercase tracking-[0.16em] text-primary">
                1 round-trip
              </span>
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
