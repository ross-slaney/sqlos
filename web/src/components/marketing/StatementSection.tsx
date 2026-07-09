import { performanceStats } from "@/components/marketing/constants";

export default function StatementSection() {
  return (
    <section className="px-6 py-24 sm:py-32">
      <div className="mx-auto max-w-4xl text-center">
        <h2 className="text-balance text-[clamp(2rem,4.5vw,3.4rem)] font-semibold leading-[1.08] tracking-[-0.045em] text-foreground">
          Authorization isn’t an API call.
          <br />
          It’s a{" "}
          <code className="rounded-xl bg-primary/10 px-3 font-mono text-[0.85em] font-bold text-primary">
            WHERE
          </code>{" "}
          clause.
        </h2>
        <p className="mx-auto mt-6 max-w-2xl text-pretty text-base leading-7 text-muted-foreground sm:text-lg">
          Other stacks fetch rows and then ask a policy service about each one. SqlOS
          folds the access check into the query plan — filtering, sorting, pagination,
          and permissions in one round-trip to your SQL Server.
        </p>

        <div className="mx-auto mt-10 inline-block overflow-x-auto rounded-2xl border bg-zinc-950 px-6 py-4 text-left shadow-xl">
          <pre className="font-mono text-[13px] leading-7 text-zinc-300">
            <code>
              <span className="text-sky-400">var</span> projects ={" "}
              <span className="text-sky-400">await</span> db.Projects{"\n"}
              {"    "}.Where(<span className="text-violet-400">await</span>{" "}
              fga.BuildFilterAsync&lt;Project&gt;(user.Id,{" "}
              <span className="text-emerald-400">&quot;projects.read&quot;</span>)){"\n"}
              {"    "}.OrderBy(p =&gt; p.Name).Take(<span className="text-amber-400">20</span>)
              .ToListAsync(); <span className="text-zinc-500">{"// one query"}</span>
            </code>
          </pre>
        </div>

        <dl className="mx-auto mt-12 grid max-w-2xl grid-cols-3 gap-4">
          {performanceStats.map((stat) => (
            <div key={stat.label}>
              <dt className="sr-only">{stat.label}</dt>
              <dd className="font-mono text-2xl font-bold tracking-tight text-foreground sm:text-3xl">
                {stat.value}
              </dd>
              <dd className="mt-1 font-mono text-[10px] uppercase tracking-[0.14em] text-muted-foreground">
                {stat.label}
              </dd>
            </div>
          ))}
        </dl>
      </div>
    </section>
  );
}
